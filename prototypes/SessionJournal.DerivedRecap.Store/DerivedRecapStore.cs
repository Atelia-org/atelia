using System.Collections.Immutable;
using System.Text;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

internal sealed record RecapStoreTestHooks(
    Action? AfterPublicationSealed = null,
    Action? BeforePublishedPromotion = null,
    Action? BeforeMaterializationEnvelopeRecheck = null,
    Action<RecapIoPoint, string>? IoObserver = null
);

public sealed class DerivedRecapStore {
    internal const long MaxStoreHeaderBytes = 16 * 1024;
    internal const long MaxManifestBytes = 2 * 1024 * 1024;
    internal const long MaxFrozenInputBytes = 5 * 1024 * 1024;
    internal const long MaxBlockBytes = 512 * 1024;
    internal const long MaxPublicationBytes = 3 * 1024 * 1024;

    private readonly RecapDurableFileSystem _fileSystem;
    private readonly RecapStoreTestHooks _testHooks;
    private readonly string _v4Root;
    private readonly string _locksRoot;
    private readonly string _refsRoot;
    private readonly string _lockPath;
    private readonly string _storeRoot;
    private readonly string _buildingRoot;
    private readonly string _publishedRoot;
    private readonly string _storeHeaderPath;

    private DerivedRecapStore(
        string sessionRepositoryPath,
        RefId refId,
        RecapStoreTestHooks? testHooks
    ) {
        SessionRepositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(sessionRepositoryPath)
        );
        RefId = refId;
        _testHooks = testHooks ?? new RecapStoreTestHooks();
        _fileSystem = new RecapDurableFileSystem(
            SessionRepositoryPath,
            _testHooks.IoObserver
        );
        _v4Root = Path.Combine(
            SessionRepositoryPath,
            "derived",
            "recap",
            "v4"
        );
        _locksRoot = Path.Combine(_v4Root, "locks");
        _refsRoot = Path.Combine(_v4Root, "refs");
        string refToken = refId.ToHexString();
        _lockPath = Path.Combine(_locksRoot, $"{refToken}.lock");
        _storeRoot = Path.Combine(_refsRoot, refToken);
        _buildingRoot = Path.Combine(_storeRoot, "building");
        _publishedRoot = Path.Combine(_storeRoot, "published");
        _storeHeaderPath = Path.Combine(_storeRoot, "store.json");
    }

    public string SessionRepositoryPath { get; }

    public RefId RefId { get; }

    public static DerivedRecapStore Open(
        string sessionRepositoryPath,
        RefId refId
    ) => OpenCore(sessionRepositoryPath, refId, testHooks: null);

    internal static DerivedRecapStore OpenForTest(
        string sessionRepositoryPath,
        RefId refId,
        RecapStoreTestHooks testHooks
    ) => OpenCore(sessionRepositoryPath, refId, testHooks);

    public async ValueTask CreateAsync(
        CancellationToken cancellationToken = default
    ) {
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (PathEntryExists(_storeRoot)) {
            throw new IOException(
                $"DerivedRecap Store already exists for RefId {RefId}."
            );
        }
        await CreateRootCoreAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask ResetAsync(
        CancellationToken cancellationToken = default
    ) {
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (PathEntryExists(_storeRoot)) {
            _fileSystem.EnsureSafeDescendant(_storeRoot);
            string quarantine = Path.Combine(
                _refsRoot,
                $".{RefId.ToHexString()}.quarantine."
                + Guid.NewGuid().ToString("N")
            );
            if (Directory.Exists(_storeRoot)) {
                _fileSystem.MoveDirectoryCreateNew(
                    _storeRoot,
                    quarantine
                );
            }
            else {
                File.Move(_storeRoot, quarantine, overwrite: false);
            }
            _fileSystem.FlushDirectory(_refsRoot);
        }
        await CreateRootCoreAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask CreateBuildingAsync(
        DerivedRecapSetManifest manifest,
        IReadOnlyList<DerivedRecapFrozenInput> inputs,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(inputs);
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        await RequireReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        DerivedRecapCodec.ValidateManifest(manifest);
        if (manifest.RefId != RefId) {
            throw new InvalidDataException(
                "Recap manifest belongs to a different RefId."
            );
        }

        FrozenInputIndex inputIndex =
            ValidateAndIndexInputs(manifest, inputs);
        if (manifest.Blocks.Any(
                static plan =>
                    plan is InheritRecapBlockPlan
                    || plan is MaintainRecapBlockPlan {
                        Source: ExistingRecapMaintainSource
                    }
            )) {
            throw new NotSupportedException(
                "R0 CreateBuilding supports only Empty Maintain "
                + "sources. Existing/Inherit requires the R1 exact "
                + "Published-source freeze protocol."
            );
        }
        string anchorToken = EventAddressFileNameCodec.Format(
            manifest.SetAdmissionAnchor
        );
        string buildingPath = Path.Combine(
            _buildingRoot,
            anchorToken
        );
        string publishedPath = Path.Combine(
            _publishedRoot,
            anchorToken
        );
        if (PathEntryExists(buildingPath)
            || PathEntryExists(publishedPath)) {
            throw new IOException(
                "A Building or Published Recap set already exists "
                + $"at {manifest.SetAdmissionAnchor}."
            );
        }

        string stagingPath = Path.Combine(
            _buildingRoot,
            $".{anchorToken}.create.{Guid.NewGuid():N}"
        );
        _fileSystem.EnsureDirectoryDurable(stagingPath);
        string inputsPath = Path.Combine(stagingPath, "inputs");
        string blocksPath = Path.Combine(stagingPath, "blocks");
        string workPath = Path.Combine(stagingPath, "work");
        _fileSystem.EnsureDirectoryDurable(inputsPath);
        _fileSystem.EnsureDirectoryDurable(blocksPath);
        _fileSystem.EnsureDirectoryDurable(workPath);

        foreach (DerivedRecapFrozenInput input
                 in inputIndex.Ordered) {
            await _fileSystem.WriteFileCreateNewAsync(
                    GetBlockFilePath(inputsPath, input.RecapBlockId),
                    DerivedRecapCodec.EncodeFrozenInput(input),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        // Manifest is the Building authority and is installed only after
        // every frozen input is durable.
        await _fileSystem.WriteFileCreateNewAsync(
                Path.Combine(stagingPath, "manifest.json"),
                DerivedRecapCodec.EncodeManifest(manifest),
                cancellationToken
            )
            .ConfigureAwait(false);
        _fileSystem.FlushDirectory(inputsPath);
        _fileSystem.FlushDirectory(blocksPath);
        _fileSystem.FlushDirectory(workPath);
        _fileSystem.FlushDirectory(stagingPath);
        _fileSystem.MoveDirectoryCreateNew(
            stagingPath,
            buildingPath
        );
        _fileSystem.FlushDirectory(_buildingRoot);
    }

    public async ValueTask WriteFinalBlockAsync(
        EventAddress admissionAnchor,
        DerivedRecapBlock block,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(block);
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        await RequireReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        string buildPath = GetBuildingPath(admissionAnchor);
        DerivedRecapSetManifest manifest =
            await ReadManifestRequiredAsync(
                    buildPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        RecapBlockPlan plan = manifest.Blocks.SingleOrDefault(
            candidate => candidate.RecapBlockId == block.RecapBlockId
        ) ?? throw new InvalidDataException(
            $"Recap block '{block.RecapBlockId}' is not in the manifest."
        );
        DerivedRecapCodec.ValidateBlock(block);
        if (block.Target != plan.Target
            || !string.Equals(
                block.BlockPlanSha256,
                DerivedRecapCodec.ComputeBlockPlanSha256(plan),
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Recap block does not match its frozen block plan."
            );
        }
        await ValidateFinalCursorAndInputAsync(
                buildPath,
                manifest,
                plan,
                block,
                cancellationToken
            )
            .ConfigureAwait(false);
        EnsureContentWithinPlanLimit(block.Content, plan);

        string path = GetBlockFilePath(
            Path.Combine(buildPath, "blocks"),
            block.RecapBlockId
        );
        if (File.Exists(path)) {
            DerivedRecapBlock existing =
                await ReadBlockRequiredAsync(path, cancellationToken)
                    .ConfigureAwait(false);
            if (existing != block) {
                throw new InvalidDataException(
                    "Immutable final Recap block collision."
                );
            }
            return;
        }
        await _fileSystem.WriteFileCreateNewAsync(
                path,
                DerivedRecapCodec.EncodeBlock(block),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<RecapPublishability> CanPublishAsync(
        EventAddress admissionAnchor,
        SessionCurrentLineageSnapshot currentLineage,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(currentLineage);
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        string? unavailable =
            await TryGetUnavailableReasonAsync(cancellationToken)
                .ConfigureAwait(false);
        if (unavailable is not null) {
            return NotPublishable("StoreUnavailable", unavailable);
        }
        return await CanPublishCoreAsync(
                admissionAnchor,
                currentLineage,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<PublishedRecapDescriptor> PublishAsync(
        EventAddress admissionAnchor,
        SessionCurrentLineageSnapshot currentLineage,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(currentLineage);
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        await RequireReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        RecapPublishability initial =
            await CanPublishCoreAsync(
                    admissionAnchor,
                    currentLineage,
                    cancellationToken
                )
                .ConfigureAwait(false);
        ThrowIfNotPublishable(initial);

        string buildPath = GetBuildingPath(admissionAnchor);
        DerivedRecapSetManifest manifest =
            await ReadManifestRequiredAsync(
                    buildPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        IReadOnlyList<DerivedRecapFrozenInput> inputs =
            await ReadExpectedInputsAsync(
                    buildPath,
                    manifest,
                    cancellationToken
                )
                .ConfigureAwait(false);
        IReadOnlyList<DerivedRecapBlock> blocks =
            await ReadFinalBlocksAsync(
                    buildPath,
                    manifest,
                    cancellationToken
                )
                .ConfigureAwait(false);

        FlushPublicationDependencies(
            buildPath,
            manifest,
            inputs,
            blocks
        );
        PublishedRecapSet publication =
            DerivedRecapCodec.CreatePublication(manifest, blocks);
        string publicationPath =
            Path.Combine(buildPath, "publication.json");
        if (File.Exists(publicationPath)) {
            PublishedRecapSet existing =
                await ReadPublicationRequiredAsync(
                        publicationPath,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (!DerivedRecapCodec.EncodePublication(existing)
                    .SequenceEqual(
                        DerivedRecapCodec.EncodePublication(publication)
                    )) {
                throw new InvalidDataException(
                    "Sealed publication candidate conflicts with "
                    + "the current Building."
                );
            }
        }
        else {
            string temporaryPath =
                await _fileSystem.WriteNamedTemporaryFileAsync(
                        buildPath,
                        "publication",
                        DerivedRecapCodec.EncodePublication(publication),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            _fileSystem.InstallTemporaryFileCreateNew(
                temporaryPath,
                publicationPath
            );
        }
        _testHooks.AfterPublicationSealed?.Invoke();

        // From the sealed candidate onward, finish the commit protocol even
        // if the caller cancels. Returning early here would make durability
        // ambiguous.
        CancellationToken commitToken = CancellationToken.None;
        RecapPublishability final =
            await CanPublishCoreAsync(
                    admissionAnchor,
                    currentLineage,
                    commitToken
                )
                .ConfigureAwait(false);
        ThrowIfNotPublishable(final);
        _testHooks.BeforePublishedPromotion?.Invoke();

        string publishedPath = GetPublishedPath(admissionAnchor);
        _fileSystem.MoveDirectoryCreateNew(
            buildPath,
            publishedPath
        );
        _fileSystem.FlushDirectory(_buildingRoot);
        _fileSystem.FlushDirectory(_publishedRoot);
        return new PublishedRecapDescriptor(
            RefId,
            admissionAnchor,
            publication.EnvelopeSha256
        );
    }

    public async ValueTask<DerivedRecapSelection>
        SelectNthPreviousAsync(
        SessionCurrentLineageSnapshot lineage,
        int nthPrevious,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(lineage);
        if (nthPrevious < 0) {
            throw new ArgumentOutOfRangeException(nameof(nthPrevious));
        }
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        string? unavailable =
            await TryGetUnavailableReasonAsync(cancellationToken)
                .ConfigureAwait(false);
        if (unavailable is not null) {
            return new DerivedRecapSelection.StoreUnavailable(
                unavailable
            );
        }
        try {
            _ = ValidateAndIndexLineage(lineage);
        }
        catch (InvalidDataException exception) {
            return new DerivedRecapSelection.StoreUnavailable(
                exception.Message
            );
        }

        int ordinal = 0;
        bool observedAny = false;
        foreach (SessionCurrentLineageHeader node
                 in lineage.HeadToRoot) {
            cancellationToken.ThrowIfCancellationRequested();
            string path = GetPublishedPath(node.Address);
            if (!PathEntryExists(path)) {
                continue;
            }
            observedAny = true;
            if (ordinal++ != nthPrevious) {
                continue;
            }
            IReadOnlyList<RecapStructuralDefect> defects =
                await ValidatePublishedAsync(
                        path,
                        node.Address,
                        lineage,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (defects.Count != 0) {
                return new DerivedRecapSelection
                    .ExactPublishedSetInvalid(
                        node.Address,
                        defects
                    );
            }
            PublishedRecapSet publication =
                await ReadPublicationRequiredAsync(
                        Path.Combine(path, "publication.json"),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            return new DerivedRecapSelection.Selected(
                new PublishedRecapDescriptor(
                    RefId,
                    node.Address,
                    publication.EnvelopeSha256
                )
            );
        }
        return observedAny
            ? new DerivedRecapSelection.OrdinalUnavailable()
            : new DerivedRecapSelection.EmptyLineage();
    }

    public async ValueTask<DerivedRecapMaterialization>
        MaterializeAsync(
        PublishedRecapDescriptor descriptor,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.RefId != RefId) {
            throw new ArgumentException(
                "Published Recap descriptor belongs to another RefId.",
                nameof(descriptor)
            );
        }
        DerivedRecapCodec.ValidateSha256(
            descriptor.EnvelopeSha256,
            "descriptor.envelopeSha256"
        );
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        await RequireReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        string publishedPath =
            GetPublishedPath(descriptor.SetAdmissionAnchor);
        string publicationPath =
            Path.Combine(publishedPath, "publication.json");
        PublishedRecapSet before =
            await ReadPublicationRequiredAsync(
                    publicationPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        RequireDescriptorMatches(descriptor, before);

        var contributions = new List<SessionContextContribution>(
            before.BlockCommitments.Count
        );
        foreach (RecapBlockCommitment commitment
                 in before.BlockCommitments) {
            DerivedRecapBlock block =
                await ReadBlockRequiredAsync(
                        GetBlockFilePath(
                            Path.Combine(publishedPath, "blocks"),
                            commitment.RecapBlockId
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (block.RecapBlockId != commitment.RecapBlockId
                || block.Target != commitment.Target
                || block.AbsorbedThrough
                    != commitment.AbsorbedThrough
                || !string.Equals(
                    block.PayloadSha256,
                    commitment.PayloadSha256,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    "Published Recap block does not match its commitment."
                );
            }
            contributions.Add(ToContribution(block));
        }
        ImmutableArray<SessionContextContribution> normalized =
            SessionContextContributionContract.ValidateAndNormalize(
                contributions
            );
        RequireCanonicalContributionOrder(contributions, normalized);

        _testHooks.BeforeMaterializationEnvelopeRecheck?.Invoke();
        PublishedRecapSet after =
            await ReadPublicationRequiredAsync(
                    publicationPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        RequireDescriptorMatches(descriptor, after);
        if (!string.Equals(
                before.EnvelopeSha256,
                after.EnvelopeSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Published Recap envelope changed during materialization."
            );
        }
        return new DerivedRecapMaterialization(
            descriptor.SetAdmissionAnchor,
            Array.AsReadOnly(contributions.ToArray())
        );
    }

    internal string GetBuildingPathForTest(EventAddress anchor)
        => GetBuildingPath(anchor);

    internal string GetPublishedPathForTest(EventAddress anchor)
        => GetPublishedPath(anchor);

    internal string StoreRootPathForTest => _storeRoot;

    private static DerivedRecapStore OpenCore(
        string sessionRepositoryPath,
        RefId refId,
        RecapStoreTestHooks? testHooks
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            sessionRepositoryPath
        );
        if (refId.IsDefault) {
            throw new ArgumentException(
                "DerivedRecap Store requires a non-default RefId.",
                nameof(refId)
            );
        }
        string fullPath = Path.GetFullPath(sessionRepositoryPath);
        RecapDurableFileSystem
            .EnsureExistingPathChainHasNoReparsePoint(fullPath);
        if (!Directory.Exists(fullPath)) {
            throw new DirectoryNotFoundException(
                $"SessionJournal repository does not exist: {fullPath}"
            );
        }
        return new DerivedRecapStore(fullPath, refId, testHooks);
    }

    private void EnsureScaffolding() {
        _fileSystem.EnsureDirectoryDurable(
            Path.Combine(SessionRepositoryPath, "derived")
        );
        _fileSystem.EnsureDirectoryDurable(
            Path.Combine(SessionRepositoryPath, "derived", "recap")
        );
        _fileSystem.EnsureDirectoryDurable(_v4Root);
        _fileSystem.EnsureDirectoryDurable(_locksRoot);
        _fileSystem.EnsureDirectoryDurable(_refsRoot);
    }

    private async ValueTask CreateRootCoreAsync(
        CancellationToken cancellationToken
    ) {
        string stagingPath = Path.Combine(
            _refsRoot,
            $".{RefId.ToHexString()}.create.{Guid.NewGuid():N}"
        );
        _fileSystem.EnsureDirectoryDurable(stagingPath);
        _fileSystem.EnsureDirectoryDurable(
            Path.Combine(stagingPath, "building")
        );
        _fileSystem.EnsureDirectoryDurable(
            Path.Combine(stagingPath, "published")
        );
        await _fileSystem.WriteFileCreateNewAsync(
                Path.Combine(stagingPath, "store.json"),
                DerivedRecapCodec.EncodeStoreHeader(RefId),
                cancellationToken
            )
            .ConfigureAwait(false);
        _fileSystem.FlushDirectory(stagingPath);
        _fileSystem.MoveDirectoryCreateNew(
            stagingPath,
            _storeRoot
        );
        _fileSystem.FlushDirectory(_refsRoot);
    }

    private async ValueTask<string?> TryGetUnavailableReasonAsync(
        CancellationToken cancellationToken
    ) {
        try {
            if (!Directory.Exists(_storeRoot)) {
                return "Recap Store root is missing.";
            }
            _fileSystem.EnsureSafeDescendant(_storeRoot);
            if (!File.Exists(_storeHeaderPath)) {
                return "Recap Store header is missing.";
            }
            RefId storedRef = DerivedRecapCodec.DecodeStoreHeader(
                await _fileSystem.ReadBoundedAsync(
                        _storeHeaderPath,
                        MaxStoreHeaderBytes,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            );
            if (storedRef != RefId) {
                return "Recap Store header RefId does not match.";
            }
            if (!Directory.Exists(_buildingRoot)
                || !Directory.Exists(_publishedRoot)) {
                return "Recap Store required directories are missing.";
            }
            _fileSystem.EnsureSafeDescendant(_buildingRoot);
            _fileSystem.EnsureSafeDescendant(_publishedRoot);
            return null;
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException
                  or IOException
                  or UnauthorizedAccessException) {
            return exception.Message;
        }
    }

    private async ValueTask RequireReadyAsync(
        CancellationToken cancellationToken
    ) {
        string? reason =
            await TryGetUnavailableReasonAsync(cancellationToken)
                .ConfigureAwait(false);
        if (reason is not null) {
            throw new InvalidDataException(
                $"DerivedRecap Store is unavailable: {reason}"
            );
        }
    }

    private async ValueTask<RecapPublishability>
        CanPublishCoreAsync(
        EventAddress admissionAnchor,
        SessionCurrentLineageSnapshot lineage,
        CancellationToken cancellationToken
    ) {
        var defects = new List<RecapStructuralDefect>();
        IReadOnlyDictionary<EventAddress, int> lineageIndex;
        try {
            lineageIndex = ValidateAndIndexLineage(lineage);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException) {
            return NotPublishable(
                "RawLineageInvalid",
                exception.Message
            );
        }
        if (!lineageIndex.TryGetValue(
                admissionAnchor,
                out int targetIndex
            )) {
            return NotPublishable(
                "AdmissionAnchorOffLineage",
                "SetAdmissionAnchor is outside the supplied raw lineage."
            );
        }

        string buildPath = GetBuildingPath(admissionAnchor);
        if (!Directory.Exists(buildPath)) {
            return NotPublishable(
                "BuildingMissing",
                "Exact Building directory is missing."
            );
        }
        try {
            _fileSystem.EnsureSafeDescendant(buildPath);
            DerivedRecapSetManifest manifest =
                await ReadManifestRequiredAsync(
                        buildPath,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (manifest.RefId != RefId
                || manifest.SetAdmissionAnchor != admissionAnchor) {
                AddDefect(
                    defects,
                    "ManifestIdentityMismatch",
                    "Manifest RefId or admission anchor is incorrect."
                );
            }
            ValidatePlanLineage(
                manifest,
                lineageIndex,
                targetIndex,
                defects
            );
            IReadOnlyList<DerivedRecapFrozenInput> inputs =
                await TryReadExpectedInputsAsync(
                        buildPath,
                        manifest,
                        defects,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            IReadOnlyList<DerivedRecapBlock> blocks =
                await TryReadFinalBlocksAsync(
                        buildPath,
                        manifest,
                        defects,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (defects.Count == 0) {
                ValidateInputsAndBlocks(
                    manifest,
                    inputs,
                    blocks,
                    lineageIndex,
                    targetIndex,
                    defects
                );
            }
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException
                  or IOException
                  or UnauthorizedAccessException) {
            AddDefect(
                defects,
                "BuildingInvalid",
                exception.Message
            );
        }

        ValidateNoRetroactivePublication(
            admissionAnchor,
            lineage,
            defects
        );
        return defects.Count == 0
            ? RecapPublishability.Publishable
            : new RecapPublishability(
                false,
                Array.AsReadOnly(defects.ToArray())
            );
    }

    private static IReadOnlyDictionary<EventAddress, int>
        ValidateAndIndexLineage(
        SessionCurrentLineageSnapshot lineage
    ) {
        ArgumentNullException.ThrowIfNull(lineage.HeadToRoot);
        if (lineage.HeadToRoot.Count == 0
            || lineage.HeadToRoot[0].Address
                != lineage.CapturedHead) {
            throw new InvalidDataException(
                "Raw lineage snapshot does not start at CapturedHead."
            );
        }
        var index = new Dictionary<EventAddress, int>();
        for (int position = 0;
             position < lineage.HeadToRoot.Count;
             position++) {
            SessionCurrentLineageHeader node =
                lineage.HeadToRoot[position]
                ?? throw new InvalidDataException(
                    "Raw lineage contains a null node."
                );
            if (!index.TryAdd(node.Address, position)) {
                throw new InvalidDataException(
                    "Raw lineage contains a cycle or duplicate."
                );
            }
            EventAddress? expectedParent =
                position + 1 < lineage.HeadToRoot.Count
                    ? lineage.HeadToRoot[position + 1].Address
                    : null;
            if (node.Parent != expectedParent) {
                throw new InvalidDataException(
                    "Raw lineage is not Parent-contiguous."
                );
            }
            if (!Enum.IsDefined(node.Kind)) {
                throw new InvalidDataException(
                    "Raw lineage contains an unknown SessionEventKind."
                );
            }
        }
        return index;
    }

    private static void ValidatePlanLineage(
        DerivedRecapSetManifest manifest,
        IReadOnlyDictionary<EventAddress, int> lineage,
        int targetIndex,
        List<RecapStructuralDefect> defects
    ) {
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            switch (plan) {
                case InheritRecapBlockPlan inherit:
                    RequireStrictAncestor(
                        inherit.SourceSetAnchor,
                        targetIndex,
                        lineage,
                        "Inherit source set",
                        defects
                    );
                    break;
                case MaintainRecapBlockPlan maintain:
                    int? priorIndex = null;
                    switch (maintain.Source) {
                        case ExistingRecapMaintainSource existing:
                            RequireStrictAncestor(
                                existing.SourceSetAnchor,
                                targetIndex,
                                lineage,
                                "Maintain source set",
                                defects
                            );
                            break;
                        case EmptyRecapMaintainSource empty:
                            if (!lineage.TryGetValue(
                                    empty.ReplayStartExclusive,
                                    out int emptyStartIndex
                                )
                                || emptyStartIndex <= targetIndex) {
                                AddDefect(
                                    defects,
                                    "ReplayStartInvalid",
                                    $"Block '{plan.RecapBlockId}' replay "
                                    + "start is not a strict "
                                    + "admission-anchor ancestor."
                                );
                            }
                            else {
                                priorIndex = emptyStartIndex;
                            }
                            break;
                    }
                    foreach (EventAddress endpoint
                             in maintain.CatchUpThrough) {
                        if (!lineage.TryGetValue(
                                endpoint,
                                out int endpointIndex
                            )
                            || priorIndex is int previous
                               && endpointIndex >= previous) {
                            AddDefect(
                                defects,
                                "CatchUpRouteInvalid",
                                $"Block '{plan.RecapBlockId}' catch-up "
                                + "endpoints are not strictly increasing."
                            );
                            break;
                        }
                        priorIndex = endpointIndex;
                    }
                    if (maintain.CatchUpThrough[^1]
                        != manifest.SetAdmissionAnchor) {
                        AddDefect(
                            defects,
                            "CatchUpRouteIncomplete",
                            $"Block '{plan.RecapBlockId}' final endpoint "
                            + "is not SetAdmissionAnchor."
                        );
                    }
                    if (maintain.PriorContext
                            is InlineRecapPriorContext inline
                        && (!lineage.TryGetValue(
                                inline.AdmissionAnchor,
                                out int priorContextIndex
                            )
                            || maintain.Source
                                   is EmptyRecapMaintainSource
                                       emptyPriorSource
                               && (!lineage.TryGetValue(
                                       emptyPriorSource
                                           .ReplayStartExclusive,
                                       out int emptyReplayIndex
                                   )
                                   || priorContextIndex
                                       < emptyReplayIndex))) {
                        AddDefect(
                            defects,
                            "PriorContextAnchorInvalid",
                            $"Block '{plan.RecapBlockId}' prior context "
                            + "is not an ancestor of its replay start."
                        );
                    }
                    break;
            }
        }
    }

    private static void RequireStrictAncestor(
        EventAddress candidate,
        int descendantIndex,
        IReadOnlyDictionary<EventAddress, int> lineage,
        string description,
        List<RecapStructuralDefect> defects
    ) {
        if (!lineage.TryGetValue(candidate, out int candidateIndex)
            || candidateIndex <= descendantIndex) {
            AddDefect(
                defects,
                "SourceAnchorInvalid",
                $"{description} is not a strict raw ancestor."
            );
        }
    }

    private void ValidateNoRetroactivePublication(
        EventAddress target,
        SessionCurrentLineageSnapshot lineage,
        List<RecapStructuralDefect> defects
    ) {
        foreach (SessionCurrentLineageHeader node
                 in lineage.HeadToRoot) {
            if (node.Address == target) {
                return;
            }
            if (PathEntryExists(GetPublishedPath(node.Address))) {
                AddDefect(
                    defects,
                    "RetroactivePublication",
                    "A newer current-lineage Published set already exists."
                );
                return;
            }
        }
        AddDefect(
            defects,
            "AdmissionAnchorOffLineage",
            "SetAdmissionAnchor was not reached on current lineage."
        );
    }

    private static void ValidateInputsAndBlocks(
        DerivedRecapSetManifest manifest,
        IReadOnlyList<DerivedRecapFrozenInput> inputs,
        IReadOnlyList<DerivedRecapBlock> blocks,
        IReadOnlyDictionary<EventAddress, int> lineage,
        int targetIndex,
        List<RecapStructuralDefect> defects
    ) {
        var inputsById = inputs.ToDictionary(
            static input => input.RecapBlockId
        );
        var contributions =
            new List<SessionContextContribution>(blocks.Count);
        for (int index = 0; index < manifest.Blocks.Count; index++) {
            RecapBlockPlan plan = manifest.Blocks[index];
            DerivedRecapBlock block = blocks[index];
            if (block.RecapBlockId != plan.RecapBlockId
                || block.Target != plan.Target
                || !string.Equals(
                    block.BlockPlanSha256,
                    DerivedRecapCodec.ComputeBlockPlanSha256(plan),
                    StringComparison.Ordinal
                )) {
                AddDefect(
                    defects,
                    "BlockPlanMismatch",
                    $"Block '{plan.RecapBlockId}' does not match its plan."
                );
                continue;
            }
            if (!lineage.TryGetValue(
                    block.AbsorbedThrough,
                    out int absorbedIndex
                )
                || absorbedIndex < targetIndex) {
                AddDefect(
                    defects,
                    "AbsorbedThroughOffLineage",
                    $"Block '{plan.RecapBlockId}' cursor is not at or "
                    + "before SetAdmissionAnchor."
                );
            }
            switch (plan) {
                case MaintainRecapBlockPlan maintain:
                    ValidateFrozenSourceCursor(
                        maintain.Source
                            is ExistingRecapMaintainSource existing
                                ? existing.SourceSetAnchor
                                : null,
                        plan,
                        inputsById,
                        lineage,
                        defects
                    );
                    ValidateExistingMaintainRoute(
                        maintain,
                        plan,
                        inputsById,
                        lineage,
                        defects
                    );
                    if (block.AbsorbedThrough
                        != manifest.SetAdmissionAnchor) {
                        AddDefect(
                            defects,
                            "MaintainCursorIncomplete",
                            $"Maintain block '{plan.RecapBlockId}' did "
                            + "not absorb through SetAdmissionAnchor."
                        );
                    }
                    break;
                case InheritRecapBlockPlan inherit:
                    ValidateFrozenSourceCursor(
                        inherit.SourceSetAnchor,
                        plan,
                        inputsById,
                        lineage,
                        defects
                    );
                    if (!inputsById.TryGetValue(
                            plan.RecapBlockId,
                            out DerivedRecapFrozenInput? input
                        )
                        || block.AbsorbedThrough
                            != input.AbsorbedThrough
                        || !string.Equals(
                            block.Content,
                            input.Content,
                            StringComparison.Ordinal
                        )) {
                        AddDefect(
                            defects,
                            "InheritNotExactCopy",
                            $"Inherit block '{plan.RecapBlockId}' is "
                            + "not an exact frozen-input copy."
                        );
                    }
                    break;
            }
            try {
                EnsureContentWithinPlanLimit(block.Content, plan);
                contributions.Add(ToContribution(block));
            }
            catch (InvalidDataException exception) {
                AddDefect(
                    defects,
                    "ContributionInvalid",
                    exception.Message
                );
            }
        }
        try {
            ImmutableArray<SessionContextContribution> normalized =
                SessionContextContributionContract.ValidateAndNormalize(
                    contributions
                );
            RequireCanonicalContributionOrder(
                contributions,
                normalized
            );
        }
        catch (InvalidDataException exception) {
            AddDefect(
                defects,
                "ContributionSetInvalid",
                exception.Message
            );
        }
    }

    private static void ValidateExistingMaintainRoute(
        MaintainRecapBlockPlan maintain,
        RecapBlockPlan plan,
        IReadOnlyDictionary<RecapBlockId, DerivedRecapFrozenInput>
            inputsById,
        IReadOnlyDictionary<EventAddress, int> lineage,
        List<RecapStructuralDefect> defects
    ) {
        if (maintain.Source
                is not ExistingRecapMaintainSource
            || !inputsById.TryGetValue(
                plan.RecapBlockId,
                out DerivedRecapFrozenInput? input
            )
            || !lineage.TryGetValue(
                input.AbsorbedThrough,
                out int previousIndex
            )) {
            return;
        }
        foreach (EventAddress endpoint in maintain.CatchUpThrough) {
            if (!lineage.TryGetValue(endpoint, out int endpointIndex)
                || endpointIndex >= previousIndex) {
                AddDefect(
                    defects,
                    "CatchUpRouteInvalid",
                    $"Block '{plan.RecapBlockId}' first catch-up "
                    + "endpoint is not strictly newer than its "
                    + "frozen input cursor."
                );
                break;
            }
            previousIndex = endpointIndex;
        }
        if (maintain.PriorContext is InlineRecapPriorContext inline
            && (!lineage.TryGetValue(
                    inline.AdmissionAnchor,
                    out int priorContextIndex
                )
                || priorContextIndex
                    < lineage[input.AbsorbedThrough])) {
            AddDefect(
                defects,
                "PriorContextAnchorInvalid",
                $"Block '{plan.RecapBlockId}' prior context is not "
                + "an ancestor of its frozen replay start."
            );
        }
    }

    private static void ValidateFrozenSourceCursor(
        EventAddress? sourceSetAnchor,
        RecapBlockPlan plan,
        IReadOnlyDictionary<RecapBlockId, DerivedRecapFrozenInput>
            inputsById,
        IReadOnlyDictionary<EventAddress, int> lineage,
        List<RecapStructuralDefect> defects
    ) {
        if (sourceSetAnchor is not { } sourceAnchor) {
            return;
        }
        if (!inputsById.TryGetValue(
                plan.RecapBlockId,
                out DerivedRecapFrozenInput? input
            )
            || !lineage.TryGetValue(
                sourceAnchor,
                out int sourceIndex
            )
            || !lineage.TryGetValue(
                input.AbsorbedThrough,
                out int absorbedIndex
            )
            || absorbedIndex < sourceIndex) {
            AddDefect(
                defects,
                "FrozenSourceCursorInvalid",
                $"Block '{plan.RecapBlockId}' frozen input cursor "
                + "is not at or before its source set anchor."
            );
        }
    }

    private async ValueTask<IReadOnlyList<RecapStructuralDefect>>
        ValidatePublishedAsync(
        string publishedPath,
        EventAddress expectedAnchor,
        SessionCurrentLineageSnapshot lineage,
        CancellationToken cancellationToken
    ) {
        var defects = new List<RecapStructuralDefect>();
        try {
            _fileSystem.EnsureSafeDescendant(publishedPath);
            PublishedRecapSet publication =
                await ReadPublicationRequiredAsync(
                        Path.Combine(
                            publishedPath,
                            "publication.json"
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (publication.RefId != RefId
                || publication.SetAdmissionAnchor != expectedAnchor) {
                AddDefect(
                    defects,
                    "PublicationIdentityMismatch",
                    "Publication identity does not match its directory."
                );
                return Array.AsReadOnly(defects.ToArray());
            }
            IReadOnlyDictionary<EventAddress, int> lineageIndex =
                ValidateAndIndexLineage(lineage);
            int targetIndex = lineageIndex[expectedAnchor];
            ValidatePlanLineage(
                publication.FrozenPlanSnapshot,
                lineageIndex,
                targetIndex,
                defects
            );
            var contributions =
                new List<SessionContextContribution>();
            for (int index = 0;
                 index < publication.BlockCommitments.Count;
                 index++) {
                RecapBlockCommitment commitment =
                    publication.BlockCommitments[index];
                RecapBlockPlan plan =
                    publication.FrozenPlanSnapshot.Blocks[index];
                DerivedRecapBlock block =
                    await ReadBlockRequiredAsync(
                            GetBlockFilePath(
                                Path.Combine(
                                    publishedPath,
                                    "blocks"
                                ),
                                commitment.RecapBlockId
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (block.RecapBlockId
                        != commitment.RecapBlockId
                    || block.Target != commitment.Target
                    || block.AbsorbedThrough
                        != commitment.AbsorbedThrough
                    || !string.Equals(
                        block.PayloadSha256,
                        commitment.PayloadSha256,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        block.BlockPlanSha256,
                        DerivedRecapCodec.ComputeBlockPlanSha256(plan),
                        StringComparison.Ordinal
                    )) {
                    AddDefect(
                        defects,
                        "PublishedBlockCommitmentMismatch",
                        $"Published block '{commitment.RecapBlockId}' "
                        + "does not match its commitment."
                    );
                    continue;
                }
                if (!lineageIndex.TryGetValue(
                        block.AbsorbedThrough,
                        out int absorbedIndex
                    )
                    || absorbedIndex < targetIndex) {
                    AddDefect(
                        defects,
                        "PublishedCursorOffLineage",
                        $"Published block '{commitment.RecapBlockId}' "
                        + "cursor is outside the admitted prefix."
                    );
                }
                switch (plan) {
                    case MaintainRecapBlockPlan
                        when block.AbsorbedThrough != expectedAnchor:
                        AddDefect(
                            defects,
                            "PublishedMaintainCursorIncomplete",
                            $"Published Maintain block "
                            + $"'{commitment.RecapBlockId}' did not "
                            + "absorb through SetAdmissionAnchor."
                        );
                        break;
                    case InheritRecapBlockPlan inherit
                        when !lineageIndex.TryGetValue(
                                 inherit.SourceSetAnchor,
                                 out int sourceIndex
                             )
                             || !lineageIndex.TryGetValue(
                                 block.AbsorbedThrough,
                                 out int inheritedIndex
                             )
                             || inheritedIndex < sourceIndex:
                        AddDefect(
                            defects,
                            "PublishedInheritCursorInvalid",
                            $"Published Inherit block "
                            + $"'{commitment.RecapBlockId}' cursor is "
                            + "newer than its source set anchor."
                        );
                        break;
                }
                EnsureContentWithinPlanLimit(block.Content, plan);
                contributions.Add(ToContribution(block));
            }
            ImmutableArray<SessionContextContribution> normalized =
                SessionContextContributionContract.ValidateAndNormalize(
                    contributions
                );
            RequireCanonicalContributionOrder(
                contributions,
                normalized
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException
                  or IOException
                  or UnauthorizedAccessException
                  or KeyNotFoundException) {
            AddDefect(
                defects,
                "PublishedSetInvalid",
                exception.Message
            );
        }
        return Array.AsReadOnly(defects.ToArray());
    }

    private FrozenInputIndex ValidateAndIndexInputs(
        DerivedRecapSetManifest manifest,
        IReadOnlyList<DerivedRecapFrozenInput> inputs
    ) {
        var byId = new Dictionary<
            RecapBlockId,
            DerivedRecapFrozenInput
        >();
        foreach (DerivedRecapFrozenInput input in inputs) {
            ArgumentNullException.ThrowIfNull(input);
            DerivedRecapCodec.ValidateFrozenInput(input);
            if (!byId.TryAdd(input.RecapBlockId, input)) {
                throw new InvalidDataException(
                    "Frozen inputs contain duplicate RecapBlockId."
                );
            }
        }
        var ordered = new List<DerivedRecapFrozenInput>();
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            string? expectedHash =
                GetExpectedInputHash(plan);
            if (expectedHash is null) {
                if (byId.ContainsKey(plan.RecapBlockId)) {
                    throw new InvalidDataException(
                        $"Empty-source block '{plan.RecapBlockId}' "
                        + "must not have a frozen source input."
                    );
                }
                continue;
            }
            if (!byId.TryGetValue(
                    plan.RecapBlockId,
                    out DerivedRecapFrozenInput? input
                )) {
                throw new InvalidDataException(
                    $"Block '{plan.RecapBlockId}' is missing its "
                    + "frozen source input."
                );
            }
            if (input.Target != plan.Target
                || !string.Equals(
                    input.PayloadSha256,
                    expectedHash,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Block '{plan.RecapBlockId}' frozen input does "
                    + "not match its exact plan."
                );
            }
            ordered.Add(input);
        }
        if (ordered.Count != byId.Count) {
            throw new InvalidDataException(
                "Frozen inputs contain an undeclared block."
            );
        }
        return new FrozenInputIndex(
            byId,
            Array.AsReadOnly(ordered.ToArray())
        );
    }


    private async ValueTask<IReadOnlyList<DerivedRecapFrozenInput>>
        ReadExpectedInputsAsync(
        string buildPath,
        DerivedRecapSetManifest manifest,
        CancellationToken cancellationToken
    ) {
        var inputs = new List<DerivedRecapFrozenInput>();
        string root = Path.Combine(buildPath, "inputs");
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            if (GetExpectedInputHash(plan) is null) {
                continue;
            }
            inputs.Add(
                await ReadFrozenInputRequiredAsync(
                        GetBlockFilePath(root, plan.RecapBlockId),
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            );
        }
        _ = ValidateAndIndexInputs(manifest, inputs);
        return Array.AsReadOnly(inputs.ToArray());
    }

    private async ValueTask<IReadOnlyList<DerivedRecapFrozenInput>>
        TryReadExpectedInputsAsync(
        string buildPath,
        DerivedRecapSetManifest manifest,
        List<RecapStructuralDefect> defects,
        CancellationToken cancellationToken
    ) {
        try {
            return await ReadExpectedInputsAsync(
                    buildPath,
                    manifest,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or IOException
                  or UnauthorizedAccessException) {
            AddDefect(
                defects,
                "FrozenInputInvalid",
                exception.Message
            );
            return Array.Empty<DerivedRecapFrozenInput>();
        }
    }

    private async ValueTask<IReadOnlyList<DerivedRecapBlock>>
        ReadFinalBlocksAsync(
        string buildPath,
        DerivedRecapSetManifest manifest,
        CancellationToken cancellationToken
    ) {
        var blocks = new List<DerivedRecapBlock>(
            manifest.Blocks.Count
        );
        string root = Path.Combine(buildPath, "blocks");
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            blocks.Add(
                await ReadBlockRequiredAsync(
                        GetBlockFilePath(root, plan.RecapBlockId),
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            );
        }
        return Array.AsReadOnly(blocks.ToArray());
    }

    private async ValueTask<IReadOnlyList<DerivedRecapBlock>>
        TryReadFinalBlocksAsync(
        string buildPath,
        DerivedRecapSetManifest manifest,
        List<RecapStructuralDefect> defects,
        CancellationToken cancellationToken
    ) {
        try {
            return await ReadFinalBlocksAsync(
                    buildPath,
                    manifest,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or IOException
                  or UnauthorizedAccessException) {
            AddDefect(
                defects,
                "FinalBlockInvalid",
                exception.Message
            );
            return Array.Empty<DerivedRecapBlock>();
        }
    }

    private async ValueTask ValidateFinalCursorAndInputAsync(
        string buildPath,
        DerivedRecapSetManifest manifest,
        RecapBlockPlan plan,
        DerivedRecapBlock block,
        CancellationToken cancellationToken
    ) {
        switch (plan) {
            case MaintainRecapBlockPlan:
                if (block.AbsorbedThrough
                    != manifest.SetAdmissionAnchor) {
                    throw new InvalidDataException(
                        "Maintain final block must absorb through "
                        + "SetAdmissionAnchor."
                    );
                }
                break;
            case InheritRecapBlockPlan:
                DerivedRecapFrozenInput input =
                    await ReadFrozenInputRequiredAsync(
                            GetBlockFilePath(
                                Path.Combine(buildPath, "inputs"),
                                plan.RecapBlockId
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (block.AbsorbedThrough != input.AbsorbedThrough
                    || !string.Equals(
                        block.Content,
                        input.Content,
                        StringComparison.Ordinal
                    )) {
                    throw new InvalidDataException(
                        "Inherit final block must exactly copy its "
                        + "frozen input content and cursor."
                    );
                }
                break;
        }
    }

    private void FlushPublicationDependencies(
        string buildPath,
        DerivedRecapSetManifest manifest,
        IReadOnlyList<DerivedRecapFrozenInput> inputs,
        IReadOnlyList<DerivedRecapBlock> blocks
    ) {
        _fileSystem.FlushFile(
            Path.Combine(buildPath, "manifest.json")
        );
        string inputsRoot = Path.Combine(buildPath, "inputs");
        foreach (DerivedRecapFrozenInput input in inputs) {
            _fileSystem.FlushFile(
                GetBlockFilePath(inputsRoot, input.RecapBlockId)
            );
        }
        string blocksRoot = Path.Combine(buildPath, "blocks");
        foreach (DerivedRecapBlock block in blocks) {
            _fileSystem.FlushFile(
                GetBlockFilePath(blocksRoot, block.RecapBlockId)
            );
        }
        _fileSystem.FlushDirectory(inputsRoot);
        _fileSystem.FlushDirectory(blocksRoot);
        _fileSystem.FlushDirectory(Path.Combine(buildPath, "work"));
        _fileSystem.FlushDirectory(buildPath);
    }

    private async ValueTask<DerivedRecapSetManifest>
        ReadManifestRequiredAsync(
        string buildPath,
        CancellationToken cancellationToken
    ) {
        string path = Path.Combine(buildPath, "manifest.json");
        byte[] bytes = await _fileSystem.ReadBoundedAsync(
                path,
                MaxManifestBytes,
                cancellationToken
            )
            .ConfigureAwait(false);
        return DerivedRecapCodec.DecodeManifest(bytes);
    }

    private async ValueTask<DerivedRecapFrozenInput>
        ReadFrozenInputRequiredAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        byte[] bytes = await _fileSystem.ReadBoundedAsync(
                path,
                MaxFrozenInputBytes,
                cancellationToken
            )
            .ConfigureAwait(false);
        return DerivedRecapCodec.DecodeFrozenInput(bytes);
    }

    private async ValueTask<DerivedRecapBlock>
        ReadBlockRequiredAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        byte[] bytes = await _fileSystem.ReadBoundedAsync(
                path,
                MaxBlockBytes,
                cancellationToken
            )
            .ConfigureAwait(false);
        return DerivedRecapCodec.DecodeBlock(bytes);
    }

    private async ValueTask<PublishedRecapSet>
        ReadPublicationRequiredAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        byte[] bytes = await _fileSystem.ReadBoundedAsync(
                path,
                MaxPublicationBytes,
                cancellationToken
            )
            .ConfigureAwait(false);
        return DerivedRecapCodec.DecodePublication(bytes);
    }

    private string GetBuildingPath(EventAddress anchor)
        => Path.Combine(
            _buildingRoot,
            EventAddressFileNameCodec.Format(anchor)
        );

    private string GetPublishedPath(EventAddress anchor)
        => Path.Combine(
            _publishedRoot,
            EventAddressFileNameCodec.Format(anchor)
        );

    private static string GetBlockFilePath(
        string root,
        RecapBlockId blockId
    ) => Path.Combine(root, $"{blockId.Value}.json");

    private static string? GetExpectedInputHash(
        RecapBlockPlan plan
    ) => plan switch {
        InheritRecapBlockPlan inherit =>
            inherit.SourceInputPayloadSha256,
        MaintainRecapBlockPlan {
            Source: ExistingRecapMaintainSource existing
        } => existing.SourceInputPayloadSha256,
        MaintainRecapBlockPlan {
            Source: EmptyRecapMaintainSource
        } => null,
        _ => throw new InvalidDataException(
            $"Unsupported Recap block plan '{plan.GetType().Name}'."
        )
    };

    private static SessionContextContribution ToContribution(
        DerivedRecapBlock block
    ) => new(
        block.Target,
        block.Content,
        SessionContextContributionHasher.CodecId,
        SessionContextContributionHasher.ComputeSha256(block.Content),
        block.AbsorbedThrough
    );

    private static void EnsureContentWithinPlanLimit(
        string content,
        RecapBlockPlan plan
    ) {
        int bytes;
        try {
            bytes = new UTF8Encoding(false, true)
                .GetByteCount(content);
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidDataException(
                $"Block '{plan.RecapBlockId}' content is not valid UTF-8.",
                exception
            );
        }
        if (bytes > plan.MaxContentUtf8Bytes) {
            throw new InvalidDataException(
                $"Block '{plan.RecapBlockId}' exceeds its frozen "
                + "maxContentUtf8Bytes."
            );
        }
    }

    private static void RequireCanonicalContributionOrder(
        IReadOnlyList<SessionContextContribution> original,
        ImmutableArray<SessionContextContribution> normalized
    ) {
        if (!original.SequenceEqual(normalized)) {
            throw new InvalidDataException(
                "Recap contributions are not in canonical target order."
            );
        }
    }

    private static void RequireDescriptorMatches(
        PublishedRecapDescriptor descriptor,
        PublishedRecapSet publication
    ) {
        if (descriptor.RefId != publication.RefId
            || descriptor.SetAdmissionAnchor
                != publication.SetAdmissionAnchor
            || !string.Equals(
                descriptor.EnvelopeSha256,
                publication.EnvelopeSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Published Recap descriptor no longer matches its "
                + "publication envelope."
            );
        }
    }

    private static RecapPublishability NotPublishable(
        string code,
        string detail
    ) => new(
        false,
        Array.AsReadOnly([
            new RecapStructuralDefect(code, detail)
        ])
    );

    private static void ThrowIfNotPublishable(
        RecapPublishability result
    ) {
        if (result.IsPublishable) {
            return;
        }
        throw new InvalidDataException(
            "Recap Building is not publishable: "
            + string.Join(
                "; ",
                result.Defects.Select(
                    static defect =>
                        $"{defect.Code}: {defect.Detail}"
                )
            )
        );
    }

    private static void AddDefect(
        List<RecapStructuralDefect> defects,
        string code,
        string detail
    ) => defects.Add(new RecapStructuralDefect(code, detail));

    private static bool PathEntryExists(string path) {
        try {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) {
            return false;
        }
        catch (DirectoryNotFoundException) {
            return false;
        }
    }

    private sealed record FrozenInputIndex(
        IReadOnlyDictionary<RecapBlockId, DerivedRecapFrozenInput>
            ById,
        IReadOnlyList<DerivedRecapFrozenInput> Ordered
    );

}
