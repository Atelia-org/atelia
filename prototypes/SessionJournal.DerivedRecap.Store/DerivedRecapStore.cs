using System.Collections.Immutable;
using System.Text;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

internal sealed record RecapStoreTestHooks(
    Action? AfterPublicationSealed = null,
    Action? BeforePublishedPromotion = null,
    Action? AfterPublishedPromotion = null,
    Action? BeforeMaterializationEnvelopeRecheck = null,
    Action? BeforeRootCommit = null,
    Action? AfterRootCommit = null,
    Action? AfterResetQuarantine = null,
    Action? AfterResetNewRootCommit = null,
    Action? BeforePublicationSealInstall = null,
    Action? BeforeSourceEnvelopeRecheck = null,
    Action? BeforeBuildingSourceFinalRecheck = null,
    Action? BeforeBuildingRawHeadRecheck = null,
    Action<string>? BeforeAtomicFileReplace = null,
    Action<string>? AfterAtomicFileReplace = null,
    Action<RecapIoPoint, string>? IoObserver = null,
    Action? BeforeRestorePublicationRead = null,
    Action? BeforeRestoreEnvelopeRawHeadRecheck = null
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
        await CreateRootCoreAsync(
                isReset: false,
                cancellationToken
            )
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
            _testHooks.AfterResetQuarantine?.Invoke();
        }
        await CreateRootCoreAsync(
                isReset: true,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<PublishedRecapSourceReadResult>
        ReadPublishedSourceAsync(
        PublishedRecapDescriptor source,
        IReadOnlyList<RecapBlockId> requiredBlocks,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(requiredBlocks);
        ValidateSourceDescriptor(source);
        if (source.RefId != RefId) {
            throw new ArgumentException(
                "Published source descriptor belongs to another RefId.",
                nameof(source)
            );
        }
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        await RequireReadyAsync(cancellationToken)
            .ConfigureAwait(false);

        SourceCaptureResult capture =
            await CapturePublishedSourceAsync(
                    source,
                    requiredBlocks,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (capture is not SourceCaptureResult.Available available) {
            return ToPublicSourceResult(capture);
        }
        _testHooks.BeforeSourceEnvelopeRecheck?.Invoke();
        PublicationRecheck recheck =
            await RecheckPublicationAsync(
                source.SetAdmissionAnchor,
                available.Capture.CanonicalEnvelope,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!recheck.IsExact) {
            return new PublishedRecapSourceReadResult
                .ChangedDuringRead(
                    source.EnvelopeSha256,
                    recheck.ObservedEnvelopeSha256
                );
        }
        return new PublishedRecapSourceReadResult.Available(
            new PublishedRecapSourceSnapshot(
                source,
                available.Capture.Publication,
                available.Capture.FrozenInputs
            )
        );
    }

    public async ValueTask<CreateBuildingResult> CreateBuildingAsync(
        DerivedRecapSetManifest manifest,
        CancellationToken cancellationToken = default
    ) => await CreateBuildingCoreAsync(
            manifest,
            expectedRawHead: null,
            readCurrentHead: null,
            cancellationToken
        )
        .ConfigureAwait(false);

    internal async ValueTask<CreateBuildingResult>
        CreateBuildingTrustedAsync(
        DerivedRecapSetManifest manifest,
        EventAddress expectedRawHead,
        Func<EventAddress?> readCurrentHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(readCurrentHead);
        return await CreateBuildingCoreAsync(
                manifest,
                expectedRawHead,
                readCurrentHead,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<CreateBuildingResult>
        CreateBuildingCoreAsync(
        DerivedRecapSetManifest manifest,
        EventAddress? expectedRawHead,
        Func<EventAddress?>? readCurrentHead,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(manifest);
        if (expectedRawHead == default(EventAddress)) {
            throw new ArgumentException(
                "Expected raw head cannot be default.",
                nameof(expectedRawHead)
            );
        }
        if ((expectedRawHead is null) != (readCurrentHead is null)) {
            throw new ArgumentException(
                "Expected raw head and reader must be supplied together."
            );
        }
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

        IReadOnlyList<SourceRequest> sourceRequests =
            GetSourceRequests(manifest);
        var captures = new List<PublishedSourceCapture>(
            sourceRequests.Count
        );
        var frozenInputs = new List<DerivedRecapFrozenInput>();
        foreach (SourceRequest request in sourceRequests) {
            SourceCaptureResult result =
                await CapturePublishedSourceAsync(
                        request.Descriptor,
                        request.BlockIds,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            switch (result) {
                case SourceCaptureResult.Available available:
                    DerivedRecapFrozenInput? mismatched =
                        available.Capture.FrozenInputs.FirstOrDefault(
                            input =>
                                !string.Equals(
                                    input.PayloadSha256,
                                    request.ExpectedPayloadSha256[
                                        input.RecapBlockId
                                    ],
                                    StringComparison.Ordinal
                                )
                        );
                    if (mismatched is not null) {
                        return new CreateBuildingResult
                            .SourceUnavailable(
                                request.Descriptor,
                                [
                                    new RecapStructuralDefect(
                                        "SourceInputCommitmentMismatch",
                                        $"Source block "
                                        + $"'{mismatched.RecapBlockId}' "
                                        + "does not match the frozen "
                                        + "manifest input commitment."
                                    )
                                ]
                            );
                    }
                    captures.Add(available.Capture);
                    frozenInputs.AddRange(
                        available.Capture.FrozenInputs
                    );
                    break;
                case SourceCaptureResult.Changed changed:
                    return new CreateBuildingResult.SourceChanged(
                        request.Descriptor,
                        changed.ObservedEnvelopeSha256
                    );
                case SourceCaptureResult.Unavailable unavailable:
                    return new CreateBuildingResult.SourceUnavailable(
                        request.Descriptor,
                        unavailable.Defects
                    );
                default:
                    throw new InvalidOperationException(
                        "Unknown source capture result."
                    );
            }
        }
        FrozenInputIndex inputIndex =
            ValidateAndIndexInputs(manifest, frozenInputs);

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

        // All source bytes are now durable in the private staging
        // directory. Recheck every distinct source as one snapshot before
        // installing the manifest authority.
        _testHooks.BeforeBuildingSourceFinalRecheck?.Invoke();
        foreach (PublishedSourceCapture capture in captures) {
            PublicationRecheck recheck =
                await RecheckPublicationAsync(
                    capture.Descriptor.SetAdmissionAnchor,
                    capture.CanonicalEnvelope,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!recheck.IsExact) {
                return new CreateBuildingResult.SourceChanged(
                    capture.Descriptor,
                    recheck.ObservedEnvelopeSha256
                );
            }
        }

        if (expectedRawHead is { } expected) {
            _testHooks.BeforeBuildingRawHeadRecheck?.Invoke();
            EventAddress? observed = readCurrentHead!();
            if (observed != expected) {
                return new CreateBuildingResult.RawHeadChanged(
                    expected,
                    observed
                );
            }
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
        return new CreateBuildingResult.Created(
            new BuildingDescriptor(
                RefId,
                manifest.SetAdmissionAnchor,
                manifest.ManifestPayloadSha256
            )
        );
    }

    public async ValueTask<BuildingReadResult> ReadBuildingAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken = default
    ) {
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        await RequireReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadBuildingCoreAsync(
                admissionAnchor,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<BuildingBlockInspection>
        InspectBuildingBlockAsync(
        BuildingDescriptor building,
        RecapBlockId blockId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(blockId);
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        await RequireReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        BuildingSnapshot snapshot =
            await ReadExactBuildingRequiredAsync(
                    building,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return await InspectBuildingBlockCoreAsync(
                snapshot,
                blockId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<CheckpointWriteResult>
        AdvanceRollingCheckpointAsync(
        BuildingDescriptor building,
        RecapBlockId blockId,
        string expectedStateToken,
        DerivedRecapBlock candidate,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(blockId);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            expectedStateToken
        );
        ArgumentNullException.ThrowIfNull(candidate);
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        await RequireReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        BuildingSnapshot snapshot =
            await ReadExactBuildingRequiredAsync(
                    building,
                    cancellationToken
                )
                .ConfigureAwait(false);
        BuildingBlockInspection inspection =
            await InspectBuildingBlockCoreAsync(
                    snapshot,
                    blockId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (!string.Equals(
                expectedStateToken,
                inspection.Checkpoint.StateToken,
                StringComparison.Ordinal
            )) {
            return new CheckpointWriteResult.Stale(
                inspection.Checkpoint.StateToken
            );
        }
        if (inspection.Plan is not MaintainRecapBlockPlan maintain) {
            throw new InvalidDataException(
                "Only Maintain blocks have rolling checkpoints."
            );
        }
        int candidateEndpoint = ValidateCheckpointCandidate(
            maintain,
            candidate
        );
        if (inspection.Checkpoint
                is RollingRecapCheckpointHealth.Healthy healthy) {
            if (healthy.Block == candidate) {
                return new CheckpointWriteResult.AlreadyCurrent(
                    healthy.StateToken
                );
            }
            if (candidateEndpoint != healthy.EndpointIndex + 1) {
                throw new InvalidDataException(
                    "Checkpoint candidate must advance exactly one "
                    + "frozen catch-up endpoint."
                );
            }
        }
        else if (candidateEndpoint != 0) {
            throw new InvalidDataException(
                "A missing or unusable checkpoint must restart at "
                + "the first frozen catch-up endpoint."
            );
        }

        string path = GetBlockFilePath(
            Path.Combine(
                GetBuildingPath(building.SetAdmissionAnchor),
                "work"
            ),
            blockId
        );
        await _fileSystem.WriteFileAtomicReplaceAsync(
                path,
                DerivedRecapCodec.EncodeBlock(candidate),
                () => _testHooks.BeforeAtomicFileReplace
                    ?.Invoke(path),
                () => _testHooks.AfterAtomicFileReplace
                    ?.Invoke(path),
                cancellationToken
            )
            .ConfigureAwait(false);
        return new CheckpointWriteResult.Updated(
            HealthyStateToken(candidate)
        );
    }

    public async ValueTask<FinalBlockWriteResult>
        EnsureFinalBlockAsync(
        BuildingDescriptor building,
        RecapBlockId blockId,
        string expectedStateToken,
        DerivedRecapBlock candidate,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(blockId);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            expectedStateToken
        );
        ArgumentNullException.ThrowIfNull(candidate);
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        await RequireReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        BuildingSnapshot snapshot =
            await ReadExactBuildingRequiredAsync(
                    building,
                    cancellationToken
                )
                .ConfigureAwait(false);
        BuildingBlockInspection inspection =
            await InspectBuildingBlockCoreAsync(
                    snapshot,
                    blockId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (!string.Equals(
                expectedStateToken,
                inspection.Final.StateToken,
                StringComparison.Ordinal
            )) {
            return new FinalBlockWriteResult.Stale(
                inspection.Final.StateToken
            );
        }
        ValidateFinalCandidate(
            snapshot.Manifest,
            inspection.Plan,
            inspection.FrozenInput,
            candidate
        );
        if (inspection.Final is FinalRecapBlockHealth.Healthy healthy) {
            return healthy.Block == candidate
                ? new FinalBlockWriteResult.AlreadyHealthy(
                    healthy.Block,
                    healthy.StateToken
                )
                : new FinalBlockWriteResult.HealthyConflict(
                    healthy.Block,
                    healthy.StateToken
                );
        }
        if (inspection.Plan is MaintainRecapBlockPlan maintain
            && (inspection.Checkpoint
                    is not RollingRecapCheckpointHealth.Healthy
                        checkpoint
                || checkpoint.EndpointIndex
                    != maintain.CatchUpThrough.Count - 1
                || checkpoint.Block != candidate)) {
            throw new InvalidDataException(
                "Maintain final installation requires a healthy, "
                + "byte-identical final-endpoint rolling checkpoint."
            );
        }

        bool replacingDamaged =
            inspection.Final is FinalRecapBlockHealth.Damaged;
        string path = GetBlockFilePath(
            Path.Combine(
                GetBuildingPath(building.SetAdmissionAnchor),
                "blocks"
            ),
            blockId
        );
        await _fileSystem.WriteFileAtomicReplaceAsync(
                path,
                DerivedRecapCodec.EncodeBlock(candidate),
                () => _testHooks.BeforeAtomicFileReplace
                    ?.Invoke(path),
                () => _testHooks.AfterAtomicFileReplace
                    ?.Invoke(path),
                cancellationToken
            )
            .ConfigureAwait(false);
        string stateToken = HealthyStateToken(candidate);
        return replacingDamaged
            ? new FinalBlockWriteResult.ReplacedDamaged(stateToken)
            : new FinalBlockWriteResult.Installed(stateToken);
    }

    /// <summary>
    /// Diagnoses one caller-supplied lineage snapshot. This is not a
    /// publication authority; public publication must go through
    /// <see cref="DerivedRecapPublisher"/>.
    /// </summary>
    internal async ValueTask<RecapPublishability>
        DiagnosePublishabilityAsync(
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

    internal async ValueTask<PublishedRecapDescriptor>
        PublishTrustedAsync(
        EventAddress admissionAnchor,
        SessionCurrentLineageSnapshot currentLineage,
        Func<EventAddress?> readCurrentHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(currentLineage);
        ArgumentNullException.ThrowIfNull(readCurrentHead);
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
            _testHooks.BeforePublicationSealInstall?.Invoke();
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
        EventAddress? authoritativeHead = readCurrentHead();
        if (authoritativeHead != currentLineage.CapturedHead) {
            throw new InvalidOperationException(
                "Raw SessionJournal head changed before Recap "
                + "publication promotion. Expected "
                + $"'{currentLineage.CapturedHead}', observed "
                + $"'{authoritativeHead}'."
            );
        }

        string publishedPath = GetPublishedPath(admissionAnchor);
        _fileSystem.MoveDirectoryCreateNew(
            buildPath,
            publishedPath
        );
        _testHooks.AfterPublishedPromotion?.Invoke();
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

    public async ValueTask<PublishedRestoreInspectionResult>
        InspectPublishedForRestoreAsync(
        EventAddress admissionAnchor,
        SessionCurrentLineageSnapshot lineage,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(lineage);
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
            return RestoreUnavailable(
                admissionAnchor,
                "StoreUnavailable",
                unavailable
            );
        }

        IReadOnlyDictionary<EventAddress, int> lineageIndex;
        try {
            lineageIndex = ValidateAndIndexLineage(lineage);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException) {
            return RestoreUnavailable(
                admissionAnchor,
                "RawLineageInvalid",
                exception.Message
            );
        }
        if (!lineageIndex.TryGetValue(
                admissionAnchor,
                out int targetIndex
            )) {
            return RestoreUnavailable(
                admissionAnchor,
                "AdmissionAnchorOffLineage",
                "SetAdmissionAnchor is outside the supplied raw lineage."
            );
        }

        string publishedPath = GetPublishedPath(admissionAnchor);
        if (!PathEntryExists(publishedPath)) {
            return RestoreUnavailable(
                admissionAnchor,
                "PublishedMembershipMissing",
                "Exact Published directory membership is missing."
            );
        }
        try {
            _fileSystem.EnsureSafeDescendant(publishedPath);
            return await InspectPublishedForRestoreCoreAsync(
                    publishedPath,
                    admissionAnchor,
                    lineageIndex,
                    targetIndex,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException
                  or IOException
                  or UnauthorizedAccessException
                  or KeyNotFoundException) {
            return RestoreUnavailable(
                admissionAnchor,
                "PublishedRestoreInspectionFailed",
                exception.Message
            );
        }
    }

    public async ValueTask<PublishedCheckpointWriteResult>
        AdvancePublishedCheckpointAsync(
        PublishedRestoreHandle handle,
        RecapBlockId blockId,
        string expectedStateToken,
        DerivedRecapBlock candidate,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(blockId);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            expectedStateToken
        );
        ArgumentNullException.ThrowIfNull(candidate);
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        string? storeUnavailable =
            await TryGetUnavailableReasonAsync(cancellationToken)
                .ConfigureAwait(false);
        if (storeUnavailable is not null) {
            return new PublishedCheckpointWriteResult.Unavailable(
                [
                    new RecapStructuralDefect(
                        "StoreUnavailable",
                        storeUnavailable
                    )
                ]
            );
        }
        RestoreHandleRead authority =
            await ReadRestoreHandleAsync(
                    handle,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (authority.IsStale) {
            return new PublishedCheckpointWriteResult.Stale(null);
        }
        if (authority.Capture is not { } capture) {
            return new PublishedCheckpointWriteResult.Unavailable(
                authority.Defects
            );
        }
        RecapBlockPlan? plan = capture.Manifest.Blocks
            .SingleOrDefault(item => item.RecapBlockId == blockId);
        if (plan is not MaintainRecapBlockPlan maintain) {
            return new PublishedCheckpointWriteResult.Unavailable(
                [
                    new RecapStructuralDefect(
                        "PublishedCheckpointPlanUnavailable",
                        $"Block '{blockId}' is not a Maintain plan in "
                        + "the exact restore authority."
                    )
                ]
            );
        }
        string publishedPath =
            GetPublishedPath(handle.SetAdmissionAnchor);
        RollingRecapCheckpointHealth checkpoint =
            await InspectCheckpointHealthAsync(
                    maintain,
                    GetBlockFilePath(
                        Path.Combine(publishedPath, "work"),
                        blockId
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (!string.Equals(
                expectedStateToken,
                checkpoint.StateToken,
                StringComparison.Ordinal
            )) {
            return new PublishedCheckpointWriteResult.Stale(
                checkpoint.StateToken
            );
        }
        int candidateEndpoint = ValidateCheckpointCandidate(
            maintain,
            candidate
        );
        if (checkpoint
                is RollingRecapCheckpointHealth.Healthy healthy) {
            if (healthy.Block == candidate) {
                return new PublishedCheckpointWriteResult
                    .AlreadyCurrent(healthy.StateToken);
            }
            if (candidateEndpoint != healthy.EndpointIndex + 1) {
                throw new InvalidDataException(
                    "Published checkpoint candidate must advance "
                    + "exactly one frozen catch-up endpoint."
                );
            }
        }
        else if (candidateEndpoint != 0) {
            throw new InvalidDataException(
                "A missing or unusable Published checkpoint must "
                + "restart at the first frozen catch-up endpoint."
            );
        }

        string path = GetBlockFilePath(
            Path.Combine(publishedPath, "work"),
            blockId
        );
        await _fileSystem.WriteFileAtomicReplaceAsync(
                path,
                DerivedRecapCodec.EncodeBlock(candidate),
                () => _testHooks.BeforeAtomicFileReplace
                    ?.Invoke(path),
                () => _testHooks.AfterAtomicFileReplace
                    ?.Invoke(path),
                cancellationToken
            )
            .ConfigureAwait(false);
        return new PublishedCheckpointWriteResult.Updated(
            HealthyStateToken(candidate)
        );
    }

    public async ValueTask<PublishedFinalWriteResult>
        InstallPublishedReplacementAsync(
        PublishedRestoreHandle handle,
        RecapBlockId blockId,
        string expectedStateToken,
        DerivedRecapBlock candidate,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(blockId);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            expectedStateToken
        );
        ArgumentNullException.ThrowIfNull(candidate);
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        string? storeUnavailable =
            await TryGetUnavailableReasonAsync(cancellationToken)
                .ConfigureAwait(false);
        if (storeUnavailable is not null) {
            return new PublishedFinalWriteResult.Unavailable(
                [
                    new RecapStructuralDefect(
                        "StoreUnavailable",
                        storeUnavailable
                    )
                ]
            );
        }
        RestoreHandleRead authority =
            await ReadRestoreHandleAsync(
                    handle,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (authority.IsStale) {
            return new PublishedFinalWriteResult.Stale(null);
        }
        if (authority.Capture is not { } capture) {
            return new PublishedFinalWriteResult.Unavailable(
                authority.Defects
            );
        }
        RecapBlockPlan? plan = capture.Manifest.Blocks
            .SingleOrDefault(item => item.RecapBlockId == blockId);
        if (plan is null) {
            return new PublishedFinalWriteResult.Unavailable(
                [
                    new RecapStructuralDefect(
                        "PublishedFinalPlanUnavailable",
                        $"Block '{blockId}' is not in the exact "
                        + "restore authority."
                    )
                ]
            );
        }
        string publishedPath =
            GetPublishedPath(handle.SetAdmissionAnchor);
        FrozenRecapInputHealth input =
            await InspectPublishedFrozenInputAsync(
                    publishedPath,
                    plan,
                    lineage: null,
                    cancellationToken
                )
                .ConfigureAwait(false);
        ValidateBlockAgainstPlan(plan, candidate);
        if (plan is MaintainRecapBlockPlan
            && candidate.AbsorbedThrough
                != capture.Manifest.SetAdmissionAnchor) {
            throw new InvalidDataException(
                "Published Maintain final block must absorb through "
                + "SetAdmissionAnchor."
            );
        }
        if (plan is InheritRecapBlockPlan
            && input is FrozenRecapInputHealth.Healthy
                candidateInput) {
            ValidateFinalCandidate(
                capture.Manifest,
                plan,
                candidateInput.Input,
                candidate
            );
        }
        RecapBlockCommitment? commitment = capture.Publication?
            .BlockCommitments.Single(
                item => item.RecapBlockId == blockId
            );
        PublishedFinalInspection final =
            await InspectPublishedFinalAsync(
                    publishedPath,
                    capture.Manifest,
                    plan,
                    input,
                    commitment,
                    lineage: null,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (!string.Equals(
                expectedStateToken,
                final.Health.StateToken,
                StringComparison.Ordinal
            )) {
            return new PublishedFinalWriteResult.Stale(
                final.Health.StateToken
            );
        }
        if (final.Health
                is FinalRecapBlockHealth.Healthy healthyFinal) {
            return healthyFinal.Block == candidate
                ? new PublishedFinalWriteResult.AlreadyHealthy(
                    healthyFinal.Block,
                    healthyFinal.StateToken
                )
                : new PublishedFinalWriteResult.HealthyConflict(
                    healthyFinal.Block,
                    healthyFinal.StateToken
                );
        }
        if (plan is InheritRecapBlockPlan
            && input is not FrozenRecapInputHealth.Healthy) {
            return new PublishedFinalWriteResult.Unavailable(
                RestoreDependencyDefects(plan, input)
            );
        }
        DerivedRecapFrozenInput? frozenInput =
            input is FrozenRecapInputHealth.Healthy healthyInput
                ? healthyInput.Input
                : null;
        if (plan is InheritRecapBlockPlan) {
            ValidateFinalCandidate(
                capture.Manifest,
                plan,
                frozenInput,
                candidate
            );
        }
        if (plan is MaintainRecapBlockPlan maintain
            && (await InspectCheckpointHealthAsync(
                    maintain,
                    GetBlockFilePath(
                        Path.Combine(publishedPath, "work"),
                        blockId
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false)
                is not RollingRecapCheckpointHealth.Healthy checkpoint
                || checkpoint.EndpointIndex
                    != maintain.CatchUpThrough.Count - 1
                || checkpoint.Block != candidate)) {
            throw new InvalidDataException(
                "Published Maintain final installation requires a "
                + "healthy, byte-identical final-endpoint checkpoint."
            );
        }

        bool replacingDamaged =
            final.Health is FinalRecapBlockHealth.Damaged;
        string path = GetBlockFilePath(
            Path.Combine(publishedPath, "blocks"),
            blockId
        );
        await _fileSystem.WriteFileAtomicReplaceAsync(
                path,
                DerivedRecapCodec.EncodeBlock(candidate),
                () => _testHooks.BeforeAtomicFileReplace
                    ?.Invoke(path),
                () => _testHooks.AfterAtomicFileReplace
                    ?.Invoke(path),
                cancellationToken
            )
            .ConfigureAwait(false);
        string stateToken = HealthyStateToken(candidate);
        return replacingDamaged
            ? new PublishedFinalWriteResult
                .ReplacedDamaged(stateToken)
            : new PublishedFinalWriteResult.Installed(stateToken);
    }

    internal async ValueTask<PublishedEnvelopeCommitResult>
        CommitPublishedEnvelopeTrustedAsync(
        PublishedRestoreHandle handle,
        IReadOnlyDictionary<RecapBlockId, string>
            expectedFinalStateTokens,
        SessionCurrentLineageSnapshot lineage,
        EventAddress expectedRawHead,
        Func<EventAddress?> readCurrentHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(expectedFinalStateTokens);
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(readCurrentHead);
        if (lineage.CapturedHead != expectedRawHead) {
            return new PublishedEnvelopeCommitResult.Stale(
                "RawHeadChanged",
                "Captured raw lineage does not match the caller-frozen "
                + "expected head."
            );
        }
        var expectedTokens =
            new Dictionary<RecapBlockId, string>();
        foreach ((RecapBlockId blockId, string token)
                 in expectedFinalStateTokens) {
            ArgumentNullException.ThrowIfNull(blockId);
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
            expectedTokens.Add(blockId, token);
        }

        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        string? storeUnavailable =
            await TryGetUnavailableReasonAsync(cancellationToken)
                .ConfigureAwait(false);
        if (storeUnavailable is not null) {
            return EnvelopeUnavailable(
                "StoreUnavailable",
                storeUnavailable
            );
        }
        IReadOnlyDictionary<EventAddress, int> lineageIndex;
        try {
            lineageIndex = ValidateAndIndexLineage(lineage);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException) {
            return EnvelopeUnavailable(
                "RawLineageInvalid",
                exception.Message
            );
        }
        if (!lineageIndex.TryGetValue(
                handle.SetAdmissionAnchor,
                out int targetIndex
            )) {
            return EnvelopeUnavailable(
                "AdmissionAnchorOffLineage",
                "SetAdmissionAnchor is outside the caller-frozen "
                + "raw lineage."
            );
        }

        RestoreHandleRead authority =
            await ReadRestoreHandleAsync(
                    handle,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (authority.IsStale) {
            return new PublishedEnvelopeCommitResult.Stale(
                "RestoreAuthorityChanged",
                "Published restore authority changed after inspection."
            );
        }
        if (authority.Capture is not { } capture) {
            return new PublishedEnvelopeCommitResult.Unavailable(
                authority.Defects
            );
        }
        if (expectedTokens.Count != capture.Manifest.Blocks.Count
            || capture.Manifest.Blocks.Any(
                plan => !expectedTokens.ContainsKey(plan.RecapBlockId)
            )) {
            return EnvelopeUnavailable(
                "ExpectedFinalRosterInvalid",
                "Expected final state tokens do not cover the exact "
                + "frozen plan roster."
            );
        }
        var planDefects = new List<RecapStructuralDefect>();
        ValidatePlanLineage(
            capture.Manifest,
            lineageIndex,
            targetIndex,
            planDefects
        );
        if (planDefects.Count != 0) {
            return new PublishedEnvelopeCommitResult.Unavailable(
                Array.AsReadOnly(planDefects.ToArray())
            );
        }

        string publishedPath =
            GetPublishedPath(handle.SetAdmissionAnchor);
        var inputs =
            new Dictionary<RecapBlockId, FrozenRecapInputHealth>();
        foreach (RecapBlockPlan plan in capture.Manifest.Blocks) {
            FrozenRecapInputHealth input =
                await InspectPublishedFrozenInputAsync(
                        publishedPath,
                        plan,
                        lineageIndex,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            inputs.Add(plan.RecapBlockId, input);
        }
        if (capture.Kind
                == PublishedRestoreAuthorityKind.ManifestWitness) {
            var witnessDefects = new List<RecapStructuralDefect>();
            foreach (RecapBlockPlan plan in capture.Manifest.Blocks) {
                FrozenRecapInputHealth input =
                    inputs[plan.RecapBlockId];
                if (input
                    is FrozenRecapInputHealth.NotRequired
                        or FrozenRecapInputHealth.Healthy) {
                    continue;
                }
                witnessDefects.AddRange(
                    RestoreDependencyDefects(plan, input)
                );
            }
            if (witnessDefects.Count != 0) {
                return new PublishedEnvelopeCommitResult.Unavailable(
                    Array.AsReadOnly(witnessDefects.ToArray())
                );
            }
        }

        IReadOnlyDictionary<RecapBlockId, RecapBlockCommitment>
            commitments = capture.Publication is { } publication
                ? publication.BlockCommitments.ToDictionary(
                    static item => item.RecapBlockId
                )
                : ImmutableDictionary<
                    RecapBlockId,
                    RecapBlockCommitment
                >.Empty;
        var blocks = new List<DerivedRecapBlock>(
            capture.Manifest.Blocks.Count
        );
        var contributions = new List<SessionContextContribution>(
            capture.Manifest.Blocks.Count
        );
        foreach (RecapBlockPlan plan in capture.Manifest.Blocks) {
            commitments.TryGetValue(
                plan.RecapBlockId,
                out RecapBlockCommitment? commitment
            );
            PublishedFinalInspection final =
                await InspectPublishedFinalAsync(
                        publishedPath,
                        capture.Manifest,
                        plan,
                        inputs[plan.RecapBlockId],
                        commitment,
                        lineageIndex,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (!string.Equals(
                    expectedTokens[plan.RecapBlockId],
                    final.Health.StateToken,
                    StringComparison.Ordinal
                )) {
                return new PublishedEnvelopeCommitResult.Stale(
                    "FinalComponentChanged",
                    $"Final block '{plan.RecapBlockId}' changed "
                    + "after inspection."
                );
            }
            if (final.Health
                    is not FinalRecapBlockHealth.Healthy healthy) {
                return new PublishedEnvelopeCommitResult.Unavailable(
                    final.Health
                        is FinalRecapBlockHealth.Damaged damaged
                            ? damaged.Defects
                            : [
                                new RecapStructuralDefect(
                                    "FinalBlockMissing",
                                    $"Final block '{plan.RecapBlockId}' "
                                    + "is missing."
                                )
                            ]
                );
            }
            blocks.Add(healthy.Block);
            contributions.Add(ToContribution(healthy.Block));
        }
        try {
            ImmutableArray<SessionContextContribution> normalized =
                SessionContextContributionContract
                    .ValidateAndNormalize(contributions);
            RequireCanonicalContributionOrder(
                contributions,
                normalized
            );
        }
        catch (InvalidDataException exception) {
            return EnvelopeUnavailable(
                "ContributionSetInvalid",
                exception.Message
            );
        }

        PublishedRecapSet next =
            DerivedRecapCodec.CreatePublication(
                capture.Manifest,
                Array.AsReadOnly(blocks.ToArray())
            );
        byte[] nextBytes =
            DerivedRecapCodec.EncodePublication(next);
        if (capture.Publication is { } current
            && nextBytes.SequenceEqual(
                DerivedRecapCodec.EncodePublication(current)
            )) {
            return new PublishedEnvelopeCommitResult
                .AlreadyCommitted(
                    new PublishedRecapDescriptor(
                        RefId,
                        handle.SetAdmissionAnchor,
                        current.EnvelopeSha256
                    )
                );
        }

        string publicationPath =
            Path.Combine(publishedPath, "publication.json");
        try {
            await _fileSystem.WriteFileAtomicReplaceAsync(
                    publicationPath,
                    nextBytes,
                    () => {
                        _testHooks
                            .BeforeRestoreEnvelopeRawHeadRecheck
                            ?.Invoke();
                        EventAddress? observed = readCurrentHead();
                        if (observed != expectedRawHead) {
                            throw new RestoreRawHeadChangedException(
                                expectedRawHead,
                                observed
                            );
                        }
                        _testHooks.BeforeAtomicFileReplace
                            ?.Invoke(publicationPath);
                    },
                    () => _testHooks.AfterAtomicFileReplace
                        ?.Invoke(publicationPath),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (RestoreRawHeadChangedException exception) {
            return new PublishedEnvelopeCommitResult.Stale(
                "RawHeadChanged",
                exception.Message
            );
        }
        return new PublishedEnvelopeCommitResult.Committed(
            new PublishedRecapDescriptor(
                RefId,
                handle.SetAdmissionAnchor,
                next.EnvelopeSha256
            )
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
        bool isReset,
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
        _testHooks.BeforeRootCommit?.Invoke();
        _fileSystem.MoveDirectoryCreateNew(
            stagingPath,
            _storeRoot
        );
        _testHooks.AfterRootCommit?.Invoke();
        if (isReset) {
            _testHooks.AfterResetNewRootCommit?.Invoke();
        }
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

    private async ValueTask<PublishedRestoreInspectionResult>
        InspectPublishedForRestoreCoreAsync(
        string publishedPath,
        EventAddress expectedAnchor,
        IReadOnlyDictionary<EventAddress, int> lineage,
        int targetIndex,
        CancellationToken cancellationToken
    ) {
        RestoreAuthorityRead authority =
            await ReadRestoreAuthorityAsync(
                    publishedPath,
                    expectedAnchor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (authority.Capture is not { } capture) {
            return new PublishedRestoreInspectionResult.Unavailable(
                expectedAnchor,
                authority.Defects
            );
        }

        var planDefects = new List<RecapStructuralDefect>();
        ValidatePlanLineage(
            capture.Manifest,
            lineage,
            targetIndex,
            planDefects
        );
        if (planDefects.Count != 0) {
            return new PublishedRestoreInspectionResult.Unavailable(
                expectedAnchor,
                Array.AsReadOnly(planDefects.ToArray())
            );
        }

        var inputs =
            new Dictionary<RecapBlockId, FrozenRecapInputHealth>();
        foreach (RecapBlockPlan plan in capture.Manifest.Blocks) {
            FrozenRecapInputHealth health =
                await InspectPublishedFrozenInputAsync(
                        publishedPath,
                        plan,
                        lineage,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            inputs.Add(plan.RecapBlockId, health);
        }
        if (capture.Kind
                == PublishedRestoreAuthorityKind.ManifestWitness) {
            RecapStructuralDefect[] witnessInputDefects = inputs
                .Where(static item =>
                    item.Value
                        is not FrozenRecapInputHealth.NotRequired
                        and not FrozenRecapInputHealth.Healthy)
                .SelectMany(static item =>
                    item.Value switch {
                        FrozenRecapInputHealth.Damaged damaged =>
                            damaged.Defects,
                        _ => [
                            new RecapStructuralDefect(
                                "ManifestWitnessInputMissing",
                                $"Block '{item.Key}' required frozen "
                                + "input is missing."
                            )
                        ]
                    })
                .ToArray();
            if (witnessInputDefects.Length != 0) {
                return new PublishedRestoreInspectionResult.Unavailable(
                    expectedAnchor,
                    Array.AsReadOnly(witnessInputDefects)
                );
            }
        }

        IReadOnlyDictionary<RecapBlockId, RecapBlockCommitment>
            commitments = capture.Publication is { } publication
                ? publication.BlockCommitments.ToDictionary(
                    static commitment => commitment.RecapBlockId
                )
                : ImmutableDictionary<
                    RecapBlockId,
                    RecapBlockCommitment
                >.Empty;
        var blocks = new Dictionary<
            RecapBlockId,
            PublishedBlockRestoreInspection
        >();
        foreach (RecapBlockPlan plan in capture.Manifest.Blocks) {
            commitments.TryGetValue(
                plan.RecapBlockId,
                out RecapBlockCommitment? commitment
            );
            FrozenRecapInputHealth input = inputs[plan.RecapBlockId];
            PublishedFinalInspection final =
                await InspectPublishedFinalAsync(
                        publishedPath,
                        capture.Manifest,
                        plan,
                        input,
                        commitment,
                        lineage,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            RollingRecapCheckpointHealth checkpoint =
                await InspectCheckpointHealthAsync(
                        plan,
                        GetBlockFilePath(
                            Path.Combine(publishedPath, "work"),
                            plan.RecapBlockId
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            PublishedBlockRestoreCapability capability =
                ClassifyPublishedRestoreCapability(
                    plan,
                    input,
                    final,
                    checkpoint
                );
            blocks.Add(
                plan.RecapBlockId,
                new PublishedBlockRestoreInspection(
                    plan,
                    input,
                    final.Health,
                    checkpoint,
                    capability
                )
            );
        }

        var handle = new PublishedRestoreHandle(
            RefId,
            expectedAnchor,
            capture.Kind,
            capture.AuthorityStateToken,
            capture.Manifest.ManifestPayloadSha256
        );
        return new PublishedRestoreInspectionResult.Available(
            new PublishedRestoreInspection(
                handle,
                capture.Manifest,
                blocks.ToImmutableDictionary()
            )
        );
    }

    private async ValueTask<RestoreAuthorityRead>
        ReadRestoreAuthorityAsync(
        string publishedPath,
        EventAddress expectedAnchor,
        CancellationToken cancellationToken
    ) {
        string publicationPath =
            Path.Combine(publishedPath, "publication.json");
        var authorityDefects = new List<RecapStructuralDefect>();
        if (!PathEntryExists(publicationPath)) {
            AddDefect(
                authorityDefects,
                "PublicationMissing",
                "Published envelope is missing."
            );
            return await ReadManifestWitnessAsync(
                    publishedPath,
                    expectedAnchor,
                    MissingStateToken,
                    authorityDefects,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        byte[] bytes;
        try {
            _testHooks.BeforeRestorePublicationRead?.Invoke();
            bytes = await _fileSystem.ReadBoundedAsync(
                    publicationPath,
                    MaxPublicationBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or IOException
                  or UnauthorizedAccessException) {
            return new RestoreAuthorityRead(
                null,
                [
                    new RecapStructuralDefect(
                        "PublicationReadUnavailable",
                        exception.Message
                    )
                ]
            );
        }

        try {
            PublishedRecapSet publication =
                DerivedRecapCodec.DecodePublication(bytes);
            if (!bytes.SequenceEqual(
                    DerivedRecapCodec.EncodePublication(publication)
                )) {
                throw new InvalidDataException(
                    "Published envelope bytes are not canonical."
                );
            }
            if (publication.RefId != RefId
                || publication.SetAdmissionAnchor != expectedAnchor) {
                return new RestoreAuthorityRead(
                    null,
                    [
                        new RecapStructuralDefect(
                            "RestoreAuthorityConflict",
                            "Self-valid publication identity does "
                            + "not match its exact directory."
                        )
                    ]
                );
            }
            return new RestoreAuthorityRead(
                new RestoreAuthorityCapture(
                    PublishedRestoreAuthorityKind.Publication,
                    $"publication:{publication.EnvelopeSha256}",
                    publication.FrozenPlanSnapshot,
                    publication
                ),
                Array.Empty<RecapStructuralDefect>()
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException) {
            AddDefect(
                authorityDefects,
                "PublicationDamaged",
                exception.Message
            );
            return await ReadManifestWitnessAsync(
                    publishedPath,
                    expectedAnchor,
                    DamagedStateToken(bytes),
                    authorityDefects,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<RestoreHandleRead>
        ReadRestoreHandleAsync(
        PublishedRestoreHandle handle,
        CancellationToken cancellationToken
    ) {
        if (handle.RefId != RefId) {
            return new RestoreHandleRead(
                null,
                IsStale: false,
                [
                    new RecapStructuralDefect(
                        "RestoreHandleRefMismatch",
                        "Restore handle belongs to another RefId."
                    )
                ]
            );
        }
        string publishedPath =
            GetPublishedPath(handle.SetAdmissionAnchor);
        if (!PathEntryExists(publishedPath)) {
            return new RestoreHandleRead(
                null,
                IsStale: false,
                [
                    new RecapStructuralDefect(
                        "PublishedMembershipMissing",
                        "Exact Published directory membership is "
                        + "missing."
                    )
                ]
            );
        }
        _fileSystem.EnsureSafeDescendant(publishedPath);
        RestoreAuthorityRead authority =
            await ReadRestoreAuthorityAsync(
                    publishedPath,
                    handle.SetAdmissionAnchor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (authority.Capture is not { } capture) {
            return new RestoreHandleRead(
                null,
                IsStale: false,
                authority.Defects
            );
        }
        bool exact = capture.Kind == handle.AuthorityKind
            && string.Equals(
                capture.AuthorityStateToken,
                handle.AuthorityStateToken,
                StringComparison.Ordinal
            )
            && string.Equals(
                capture.Manifest.ManifestPayloadSha256,
                handle.ManifestPayloadSha256,
                StringComparison.Ordinal
            );
        return exact
            ? new RestoreHandleRead(
                capture,
                IsStale: false,
                Array.Empty<RecapStructuralDefect>()
            )
            : new RestoreHandleRead(
                null,
                IsStale: true,
                Array.Empty<RecapStructuralDefect>()
            );
    }

    private async ValueTask<RestoreAuthorityRead>
        ReadManifestWitnessAsync(
        string publishedPath,
        EventAddress expectedAnchor,
        string publicationStateToken,
        List<RecapStructuralDefect> authorityDefects,
        CancellationToken cancellationToken
    ) {
        try {
            string manifestPath =
                Path.Combine(publishedPath, "manifest.json");
            byte[] bytes = await _fileSystem.ReadBoundedAsync(
                    manifestPath,
                    MaxManifestBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
            DerivedRecapSetManifest manifest =
                DerivedRecapCodec.DecodeManifest(bytes);
            if (!bytes.SequenceEqual(
                    DerivedRecapCodec.EncodeManifest(manifest)
                )) {
                throw new InvalidDataException(
                    "Manifest witness bytes are not canonical."
                );
            }
            if (manifest.RefId != RefId
                || manifest.SetAdmissionAnchor != expectedAnchor) {
                AddDefect(
                    authorityDefects,
                    "ManifestWitnessIdentityMismatch",
                    "Manifest witness identity does not match its "
                    + "exact directory."
                );
                return new RestoreAuthorityRead(
                    null,
                    Array.AsReadOnly(authorityDefects.ToArray())
                );
            }
            return new RestoreAuthorityRead(
                new RestoreAuthorityCapture(
                    PublishedRestoreAuthorityKind.ManifestWitness,
                    publicationStateToken,
                    manifest,
                    null
                ),
                Array.Empty<RecapStructuralDefect>()
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException
                  or IOException
                  or UnauthorizedAccessException) {
            AddDefect(
                authorityDefects,
                "ManifestWitnessUnavailable",
                exception.Message
            );
            return new RestoreAuthorityRead(
                null,
                Array.AsReadOnly(authorityDefects.ToArray())
            );
        }
    }

    private async ValueTask<FrozenRecapInputHealth>
        InspectPublishedFrozenInputAsync(
        string publishedPath,
        RecapBlockPlan plan,
        IReadOnlyDictionary<EventAddress, int>? lineage,
        CancellationToken cancellationToken
    ) {
        string? expectedHash = GetExpectedInputHash(plan);
        if (expectedHash is null) {
            return new FrozenRecapInputHealth.NotRequired(
                "not-required"
            );
        }
        string path = GetBlockFilePath(
            Path.Combine(publishedPath, "inputs"),
            plan.RecapBlockId
        );
        if (!File.Exists(path)) {
            return new FrozenRecapInputHealth.Missing(
                MissingStateToken
            );
        }
        byte[] bytes;
        try {
            bytes = await _fileSystem.ReadBoundedAsync(
                    path,
                    MaxFrozenInputBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException
                  or DirectoryNotFoundException) {
            return new FrozenRecapInputHealth.Missing(
                MissingStateToken
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or IOException
                  or UnauthorizedAccessException) {
            return new FrozenRecapInputHealth.Damaged(
                [
                    new RecapStructuralDefect(
                        "FrozenInputDamaged",
                        exception.Message
                    )
                ],
                "damaged:unreadable"
            );
        }
        try {
            DerivedRecapFrozenInput input =
                DerivedRecapCodec.DecodeFrozenInput(bytes);
            if (input.RecapBlockId != plan.RecapBlockId
                || input.Target != plan.Target
                || !string.Equals(
                    input.PayloadSha256,
                    expectedHash,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    "Frozen input does not match its exact block plan."
                );
            }
            if (lineage is not null) {
                var semanticDefects =
                    new List<RecapStructuralDefect>();
                var index = new Dictionary<
                    RecapBlockId,
                    DerivedRecapFrozenInput
                > {
                    [plan.RecapBlockId] = input
                };
                EventAddress? sourceAnchor = plan switch {
                    InheritRecapBlockPlan inherit =>
                        inherit.SourceSetAnchor,
                    MaintainRecapBlockPlan {
                        Source: ExistingRecapMaintainSource existing
                    } => existing.SourceSetAnchor,
                    _ => null
                };
                ValidateFrozenSourceCursor(
                    sourceAnchor,
                    plan,
                    index,
                    lineage,
                    semanticDefects
                );
                if (plan is MaintainRecapBlockPlan maintain) {
                    ValidateExistingMaintainRoute(
                        maintain,
                        plan,
                        index,
                        lineage,
                        semanticDefects
                    );
                }
                if (semanticDefects.Count != 0) {
                    return new FrozenRecapInputHealth.Damaged(
                        Array.AsReadOnly(
                            semanticDefects.ToArray()
                        ),
                        $"damaged:{input.PayloadSha256}"
                    );
                }
            }
            return new FrozenRecapInputHealth.Healthy(
                input,
                HealthyStateToken(input.PayloadSha256)
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException) {
            return new FrozenRecapInputHealth.Damaged(
                [
                    new RecapStructuralDefect(
                        "FrozenInputDamaged",
                        exception.Message
                    )
                ],
                DamagedStateToken(bytes)
            );
        }
    }

    private async ValueTask<PublishedFinalInspection>
        InspectPublishedFinalAsync(
        string publishedPath,
        DerivedRecapSetManifest manifest,
        RecapBlockPlan plan,
        FrozenRecapInputHealth inputHealth,
        RecapBlockCommitment? commitment,
        IReadOnlyDictionary<EventAddress, int>? lineage,
        CancellationToken cancellationToken
    ) {
        string path = GetBlockFilePath(
            Path.Combine(publishedPath, "blocks"),
            plan.RecapBlockId
        );
        if (!File.Exists(path)) {
            return new PublishedFinalInspection(
                new FinalRecapBlockHealth.Missing(
                    MissingStateToken
                ),
                IsCommitted: false
            );
        }
        byte[] bytes;
        try {
            bytes = await _fileSystem.ReadBoundedAsync(
                    path,
                    MaxBlockBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException
                  or DirectoryNotFoundException) {
            return new PublishedFinalInspection(
                new FinalRecapBlockHealth.Missing(
                    MissingStateToken
                ),
                IsCommitted: false
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or IOException
                  or UnauthorizedAccessException) {
            return DamagedPublishedFinal(
                "FinalBlockDamaged",
                exception.Message,
                "damaged:unreadable"
            );
        }
        try {
            DerivedRecapBlock block =
                DerivedRecapCodec.DecodeBlock(bytes);
            ValidateBlockAgainstPlan(plan, block);
            bool isCommitted = commitment is not null
                && MatchesCommitment(block, commitment);
            if (isCommitted) {
                ValidateCommittedPublishedFinal(
                    manifest,
                    plan,
                    block,
                    lineage
                );
            }
            else {
                DerivedRecapFrozenInput? input =
                    inputHealth is FrozenRecapInputHealth.Healthy
                        healthy
                        ? healthy.Input
                        : null;
                ValidateFinalCandidate(
                    manifest,
                    plan,
                    input,
                    block
                );
            }
            return new PublishedFinalInspection(
                new FinalRecapBlockHealth.Healthy(
                    block,
                    HealthyStateToken(block)
                ),
                isCommitted
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException) {
            return DamagedPublishedFinal(
                "FinalBlockDamaged",
                exception.Message,
                DamagedStateToken(bytes)
            );
        }
    }

    private static PublishedFinalInspection DamagedPublishedFinal(
        string code,
        string detail,
        string stateToken
    ) => new(
        new FinalRecapBlockHealth.Damaged(
            [new RecapStructuralDefect(code, detail)],
            stateToken
        ),
        IsCommitted: false
    );

    private static bool MatchesCommitment(
        DerivedRecapBlock block,
        RecapBlockCommitment commitment
    ) => block.RecapBlockId == commitment.RecapBlockId
         && block.Target == commitment.Target
         && block.AbsorbedThrough == commitment.AbsorbedThrough
         && string.Equals(
             block.PayloadSha256,
             commitment.PayloadSha256,
             StringComparison.Ordinal
         );

    private static void ValidateCommittedPublishedFinal(
        DerivedRecapSetManifest manifest,
        RecapBlockPlan plan,
        DerivedRecapBlock block,
        IReadOnlyDictionary<EventAddress, int>? lineage
    ) {
        switch (plan) {
            case MaintainRecapBlockPlan
                when block.AbsorbedThrough
                    != manifest.SetAdmissionAnchor:
                throw new InvalidDataException(
                    "Committed Maintain block is not mode-final."
                );
            case InheritRecapBlockPlan inherit
                when lineage is not null:
                if (!lineage.TryGetValue(
                        inherit.SourceSetAnchor,
                        out int sourceIndex
                    )
                    || !lineage.TryGetValue(
                        block.AbsorbedThrough,
                        out int absorbedIndex
                    )
                    || absorbedIndex < sourceIndex) {
                    throw new InvalidDataException(
                        "Committed Inherit block cursor is invalid."
                    );
                }
                break;
        }
    }

    private static PublishedBlockRestoreCapability
        ClassifyPublishedRestoreCapability(
        RecapBlockPlan plan,
        FrozenRecapInputHealth input,
        PublishedFinalInspection final,
        RollingRecapCheckpointHealth checkpoint
    ) {
        if (final.Health is FinalRecapBlockHealth.Healthy) {
            return final.IsCommitted
                ? new PublishedBlockRestoreCapability.KeepCommitted()
                : new PublishedBlockRestoreCapability.AdoptPending();
        }
        if (plan is InheritRecapBlockPlan) {
            return input is FrozenRecapInputHealth.Healthy
                ? new PublishedBlockRestoreCapability.ReplayBlock()
                : UnavailableRestoreCapability(
                    plan,
                    input,
                    final.Health
                );
        }

        var maintain = (MaintainRecapBlockPlan)plan;
        if (checkpoint
                is RollingRecapCheckpointHealth.Healthy healthy) {
            return healthy.EndpointIndex
                    == maintain.CatchUpThrough.Count - 1
                ? new PublishedBlockRestoreCapability
                    .InstallFinalCheckpoint()
                : new PublishedBlockRestoreCapability.ResumeSuffix(
                    healthy.EndpointIndex + 1
                );
        }
        if (maintain.Source is EmptyRecapMaintainSource
            || input is FrozenRecapInputHealth.Healthy) {
            return new PublishedBlockRestoreCapability.ReplayBlock();
        }
        return UnavailableRestoreCapability(
            plan,
            input,
            final.Health
        );
    }

    private static PublishedBlockRestoreCapability.Unavailable
        UnavailableRestoreCapability(
        RecapBlockPlan plan,
        FrozenRecapInputHealth input,
        FinalRecapBlockHealth final
    ) {
        var defects = new List<RecapStructuralDefect>();
        if (final is FinalRecapBlockHealth.Damaged damagedFinal) {
            defects.AddRange(damagedFinal.Defects);
        }
        if (input is FrozenRecapInputHealth.Damaged damagedInput) {
            defects.AddRange(damagedInput.Defects);
        }
        if (input is FrozenRecapInputHealth.Missing) {
            AddDefect(
                defects,
                "RestoreDependencyMissing",
                $"Block '{plan.RecapBlockId}' required frozen input "
                + "is missing."
            );
        }
        if (defects.Count == 0) {
            AddDefect(
                defects,
                "RestoreDependencyUnavailable",
                $"Block '{plan.RecapBlockId}' cannot be restored from "
                + "its available exact dependencies."
            );
        }
        return new PublishedBlockRestoreCapability.Unavailable(
            Array.AsReadOnly(defects.ToArray())
        );
    }

    private static IReadOnlyList<RecapStructuralDefect>
        RestoreDependencyDefects(
        RecapBlockPlan plan,
        FrozenRecapInputHealth input
    ) {
        if (input is FrozenRecapInputHealth.Damaged damaged) {
            return damaged.Defects;
        }
        return [
            new RecapStructuralDefect(
                "RestoreDependencyMissing",
                $"Block '{plan.RecapBlockId}' required frozen input "
                + "is missing."
            )
        ];
    }

    private static PublishedRestoreInspectionResult.Unavailable
        RestoreUnavailable(
        EventAddress admissionAnchor,
        string code,
        string detail
    ) => new(
        admissionAnchor,
        [new RecapStructuralDefect(code, detail)]
    );

    private static PublishedEnvelopeCommitResult.Unavailable
        EnvelopeUnavailable(
        string code,
        string detail
    ) => new([new RecapStructuralDefect(code, detail)]);

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

    private async ValueTask<BuildingReadResult> ReadBuildingCoreAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken
    ) {
        string buildPath = GetBuildingPath(admissionAnchor);
        if (!Directory.Exists(buildPath)) {
            return new BuildingReadResult.Missing();
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
                throw new InvalidDataException(
                    "Building manifest identity does not match its path."
                );
            }
            IReadOnlyList<DerivedRecapFrozenInput> inputs =
                await ReadExpectedInputsAsync(
                        buildPath,
                        manifest,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            FrozenInputIndex index =
                ValidateAndIndexInputs(manifest, inputs);
            return new BuildingReadResult.Available(
                new BuildingSnapshot(
                    new BuildingDescriptor(
                        RefId,
                        admissionAnchor,
                        manifest.ManifestPayloadSha256
                    ),
                    manifest,
                    index.ById.ToImmutableDictionary()
                )
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException
                  or IOException
                  or UnauthorizedAccessException) {
            return new BuildingReadResult.Invalid([
                new RecapStructuralDefect(
                    "BuildingInvalid",
                    exception.Message
                )
            ]);
        }
    }

    private async ValueTask<BuildingSnapshot>
        ReadExactBuildingRequiredAsync(
        BuildingDescriptor descriptor,
        CancellationToken cancellationToken
    ) {
        if (descriptor.RefId != RefId) {
            throw new ArgumentException(
                "Building descriptor belongs to another RefId.",
                nameof(descriptor)
            );
        }
        DerivedRecapCodec.ValidateSha256(
            descriptor.ManifestPayloadSha256,
            "building.manifestPayloadSha256"
        );
        BuildingReadResult result = await ReadBuildingCoreAsync(
                descriptor.SetAdmissionAnchor,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (result is not BuildingReadResult.Available available) {
            throw new InvalidDataException(
                result is BuildingReadResult.Invalid invalid
                    ? "Building is invalid: "
                      + string.Join(
                          "; ",
                          invalid.Defects.Select(
                              static defect =>
                                  $"{defect.Code}: {defect.Detail}"
                          )
                      )
                    : "Exact Building is missing."
            );
        }
        if (!string.Equals(
                available.Snapshot.Descriptor
                    .ManifestPayloadSha256,
                descriptor.ManifestPayloadSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Building descriptor no longer matches its manifest."
            );
        }
        return available.Snapshot;
    }

    private async ValueTask<BuildingBlockInspection>
        InspectBuildingBlockCoreAsync(
        BuildingSnapshot snapshot,
        RecapBlockId blockId,
        CancellationToken cancellationToken
    ) {
        RecapBlockPlan plan =
            snapshot.Manifest.Blocks.SingleOrDefault(
                candidate => candidate.RecapBlockId == blockId
            ) ?? throw new KeyNotFoundException(
                $"Recap block '{blockId}' is not in the Building."
            );
        snapshot.FrozenInputs.TryGetValue(
            blockId,
            out DerivedRecapFrozenInput? input
        );
        string buildPath =
            GetBuildingPath(snapshot.Descriptor.SetAdmissionAnchor);
        FinalRecapBlockHealth final =
            await InspectFinalBlockHealthAsync(
                    snapshot,
                    plan,
                    input,
                    GetBlockFilePath(
                        Path.Combine(buildPath, "blocks"),
                        blockId
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        RollingRecapCheckpointHealth checkpoint =
            await InspectCheckpointHealthAsync(
                    plan,
                    GetBlockFilePath(
                        Path.Combine(buildPath, "work"),
                        blockId
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        return new BuildingBlockInspection(
            snapshot.Descriptor,
            plan,
            input,
            final,
            checkpoint
        );
    }

    private async ValueTask<FinalRecapBlockHealth>
        InspectFinalBlockHealthAsync(
        BuildingSnapshot snapshot,
        RecapBlockPlan plan,
        DerivedRecapFrozenInput? input,
        string path,
        CancellationToken cancellationToken
    ) {
        if (!File.Exists(path)) {
            return new FinalRecapBlockHealth.Missing(
                MissingStateToken
            );
        }
        byte[] bytes;
        try {
            bytes = await _fileSystem.ReadBoundedAsync(
                    path,
                    MaxBlockBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException
                  or DirectoryNotFoundException) {
            return new FinalRecapBlockHealth.Missing(
                MissingStateToken
            );
        }
        catch (InvalidDataException exception) {
            return new FinalRecapBlockHealth.Damaged(
                [
                    new RecapStructuralDefect(
                        "FinalBlockDamaged",
                        exception.Message
                    )
                ],
                $"damaged:{_fileSystem.ComputeFileSha256(path)}"
            );
        }
        try {
            DerivedRecapBlock block =
                DerivedRecapCodec.DecodeBlock(bytes);
            ValidateFinalCandidate(
                snapshot.Manifest,
                plan,
                input,
                block
            );
            return new FinalRecapBlockHealth.Healthy(
                block,
                HealthyStateToken(block)
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException) {
            return new FinalRecapBlockHealth.Damaged(
                [
                    new RecapStructuralDefect(
                        "FinalBlockDamaged",
                        exception.Message
                    )
                ],
                DamagedStateToken(bytes)
            );
        }
    }

    private async ValueTask<RollingRecapCheckpointHealth>
        InspectCheckpointHealthAsync(
        RecapBlockPlan plan,
        string path,
        CancellationToken cancellationToken
    ) {
        if (!File.Exists(path)) {
            return new RollingRecapCheckpointHealth.Missing(
                MissingStateToken
            );
        }
        byte[] bytes;
        try {
            bytes = await _fileSystem.ReadBoundedAsync(
                    path,
                    MaxBlockBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException
                  or DirectoryNotFoundException) {
            return new RollingRecapCheckpointHealth.Missing(
                MissingStateToken
            );
        }
        catch (InvalidDataException exception) {
            return new RollingRecapCheckpointHealth.Unusable(
                [
                    new RecapStructuralDefect(
                        "CheckpointUnusable",
                        exception.Message
                    )
                ],
                $"damaged:{_fileSystem.ComputeFileSha256(path)}"
            );
        }
        try {
            if (plan is not MaintainRecapBlockPlan maintain) {
                throw new InvalidDataException(
                    "Inherit blocks must not have rolling checkpoints."
                );
            }
            DerivedRecapBlock block =
                DerivedRecapCodec.DecodeBlock(bytes);
            int endpointIndex =
                ValidateCheckpointCandidate(maintain, block);
            return new RollingRecapCheckpointHealth.Healthy(
                block,
                endpointIndex,
                HealthyStateToken(block)
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException) {
            return new RollingRecapCheckpointHealth.Unusable(
                [
                    new RecapStructuralDefect(
                        "CheckpointUnusable",
                        exception.Message
                    )
                ],
                DamagedStateToken(bytes)
            );
        }
    }

    private static int ValidateCheckpointCandidate(
        MaintainRecapBlockPlan plan,
        DerivedRecapBlock candidate
    ) {
        ValidateBlockAgainstPlan(plan, candidate);
        int endpointIndex = -1;
        for (int index = 0;
             index < plan.CatchUpThrough.Count;
             index++) {
            if (plan.CatchUpThrough[index]
                == candidate.AbsorbedThrough) {
                endpointIndex = index;
                break;
            }
        }
        if (endpointIndex < 0) {
            throw new InvalidDataException(
                "Rolling checkpoint cursor is outside the frozen "
                + "catch-up route."
            );
        }
        return endpointIndex;
    }

    private static void ValidateFinalCandidate(
        DerivedRecapSetManifest manifest,
        RecapBlockPlan plan,
        DerivedRecapFrozenInput? input,
        DerivedRecapBlock candidate
    ) {
        ValidateBlockAgainstPlan(plan, candidate);
        switch (plan) {
            case MaintainRecapBlockPlan:
                if (candidate.AbsorbedThrough
                    != manifest.SetAdmissionAnchor) {
                    throw new InvalidDataException(
                        "Maintain final block must absorb through "
                        + "SetAdmissionAnchor."
                    );
                }
                break;
            case InheritRecapBlockPlan:
                if (input is null
                    || candidate.AbsorbedThrough
                        != input.AbsorbedThrough
                    || !string.Equals(
                        candidate.Content,
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

    private static void ValidateBlockAgainstPlan(
        RecapBlockPlan plan,
        DerivedRecapBlock block
    ) {
        DerivedRecapCodec.ValidateBlock(block);
        if (block.RecapBlockId != plan.RecapBlockId
            || block.Target != plan.Target
            || !string.Equals(
                block.BlockPlanSha256,
                DerivedRecapCodec.ComputeBlockPlanSha256(plan),
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Recap block does not match its frozen block plan."
            );
        }
        EnsureContentWithinPlanLimit(block.Content, plan);
    }

    private const string MissingStateToken = "missing";

    private static string HealthyStateToken(
        DerivedRecapBlock block
    ) => HealthyStateToken(block.PayloadSha256);

    private static string HealthyStateToken(
        string payloadSha256
    ) => $"healthy:{payloadSha256}";

    private static string DamagedStateToken(
        ReadOnlySpan<byte> bytes
    ) => $"damaged:{DerivedRecapCodec.Sha256Hex(bytes)}";

    private async ValueTask<SourceCaptureResult>
        CapturePublishedSourceAsync(
        PublishedRecapDescriptor source,
        IReadOnlyList<RecapBlockId> requiredBlocks,
        CancellationToken cancellationToken
    ) {
        string publishedPath =
            GetPublishedPath(source.SetAdmissionAnchor);
        if (!Directory.Exists(publishedPath)) {
            return new SourceCaptureResult.Unavailable(
                source.SetAdmissionAnchor,
                [
                    new RecapStructuralDefect(
                        "SourcePublishedSetMissing",
                        $"Published source '{source.SetAdmissionAnchor}' "
                        + "is missing."
                    )
                ]);
        }
        try {
            string publicationPath =
                Path.Combine(publishedPath, "publication.json");
            byte[] canonicalEnvelope =
                await _fileSystem.ReadBoundedAsync(
                        publicationPath,
                        MaxPublicationBytes,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            PublishedRecapSet publication =
                DerivedRecapCodec.DecodePublication(canonicalEnvelope);
            if (publication.RefId != RefId
                || publication.SetAdmissionAnchor
                    != source.SetAdmissionAnchor) {
                return new SourceCaptureResult.Unavailable(
                    source.SetAdmissionAnchor,
                    [
                        new RecapStructuralDefect(
                            "SourceIdentityMismatch",
                            "Published source identity does not match "
                            + "its descriptor."
                        )
                    ]);
            }
            if (!string.Equals(
                    publication.EnvelopeSha256,
                    source.EnvelopeSha256,
                    StringComparison.Ordinal
                )) {
                return new SourceCaptureResult.Changed(
                    source.EnvelopeSha256,
                    publication.EnvelopeSha256
                );
            }
            byte[] encoded =
                DerivedRecapCodec.EncodePublication(publication);
            if (!canonicalEnvelope.SequenceEqual(encoded)) {
                return new SourceCaptureResult.Unavailable(
                    source.SetAdmissionAnchor,
                    [
                        new RecapStructuralDefect(
                            "SourceEnvelopeNonCanonical",
                            "Published source envelope is not canonical."
                        )
                    ]);
            }

            var seen = new HashSet<RecapBlockId>();
            var inputs = new List<DerivedRecapFrozenInput>(
                requiredBlocks.Count
            );
            foreach (RecapBlockId blockId in requiredBlocks) {
                ArgumentNullException.ThrowIfNull(blockId);
                if (!seen.Add(blockId)) {
                    return new SourceCaptureResult.Unavailable(
                        source.SetAdmissionAnchor,
                        [
                            new RecapStructuralDefect(
                                "SourceBlockRequestedTwice",
                                $"Source block '{blockId}' was requested twice."
                            )
                        ]);
                }
                RecapBlockPlan? sourcePlan =
                    publication.FrozenPlanSnapshot.Blocks
                        .SingleOrDefault(
                            candidate =>
                                candidate.RecapBlockId == blockId
                        );
                RecapBlockCommitment? commitment =
                    publication.BlockCommitments.SingleOrDefault(
                        candidate =>
                            candidate.RecapBlockId == blockId
                    );
                if (sourcePlan is null || commitment is null) {
                    return new SourceCaptureResult.Unavailable(
                        source.SetAdmissionAnchor,
                        [
                            new RecapStructuralDefect(
                                "SourceBlockMissing",
                                $"Published source does not contain block "
                                + $"'{blockId}'."
                            )
                        ]);
                }
                DerivedRecapBlock block =
                    await ReadBlockRequiredAsync(
                            GetBlockFilePath(
                                Path.Combine(publishedPath, "blocks"),
                                blockId
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (block.RecapBlockId != commitment.RecapBlockId
                    || block.Target != commitment.Target
                    || commitment.Target != sourcePlan.Target
                    || block.AbsorbedThrough
                        != commitment.AbsorbedThrough
                    || !string.Equals(
                        block.PayloadSha256,
                        commitment.PayloadSha256,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        block.BlockPlanSha256,
                        DerivedRecapCodec
                            .ComputeBlockPlanSha256(sourcePlan),
                        StringComparison.Ordinal
                    )) {
                    return new SourceCaptureResult.Unavailable(
                        source.SetAdmissionAnchor,
                        [
                            new RecapStructuralDefect(
                                "SourceBlockCommitmentMismatch",
                                $"Published source block '{blockId}' does "
                                + "not match its envelope commitment."
                            )
                        ]);
                }
                inputs.Add(DerivedRecapCodec.CreateFrozenInput(
                    block.RecapBlockId,
                    block.Target,
                    block.AbsorbedThrough,
                    block.Content
                ));
            }
            return new SourceCaptureResult.Available(
                new PublishedSourceCapture(
                    source,
                    publication,
                    canonicalEnvelope,
                    Array.AsReadOnly(inputs.ToArray())
                )
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException
                  or IOException
                  or UnauthorizedAccessException
                  or InvalidOperationException) {
            return new SourceCaptureResult.Unavailable(
                source.SetAdmissionAnchor,
                [
                    new RecapStructuralDefect(
                        "SourcePublishedSetInvalid",
                        exception.Message
                    )
                ]);
        }
    }

    private async ValueTask<PublicationRecheck>
        RecheckPublicationAsync(
        EventAddress sourceSetAnchor,
        byte[] expectedCanonicalEnvelope,
        CancellationToken cancellationToken
    ) {
        try {
            byte[] observedBytes =
                await _fileSystem.ReadBoundedAsync(
                        Path.Combine(
                            GetPublishedPath(sourceSetAnchor),
                            "publication.json"
                        ),
                        MaxPublicationBytes,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            PublishedRecapSet observed =
                DerivedRecapCodec.DecodePublication(observedBytes);
            return new PublicationRecheck(
                observedBytes.SequenceEqual(
                    expectedCanonicalEnvelope
                ),
                observed.EnvelopeSha256
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or IOException
                  or UnauthorizedAccessException
                  or FileNotFoundException
                  or DirectoryNotFoundException) {
            return new PublicationRecheck(
                IsExact: false,
                ObservedEnvelopeSha256: null
            );
        }
    }

    private static PublishedRecapSourceReadResult
        ToPublicSourceResult(SourceCaptureResult result)
        => result switch {
            SourceCaptureResult.Changed changed =>
                new PublishedRecapSourceReadResult
                    .SnapshotTokenMismatch(
                        changed.ExpectedEnvelopeSha256,
                        changed.ObservedEnvelopeSha256
                    ),
            SourceCaptureResult.Unavailable unavailable
                when unavailable.Defects.Any(
                    static defect =>
                        defect.Code == "SourcePublishedSetMissing"
                ) =>
                new PublishedRecapSourceReadResult.Missing(
                    unavailable.SourceSetAnchor
                ),
            SourceCaptureResult.Unavailable unavailable =>
                new PublishedRecapSourceReadResult.Invalid(
                    unavailable.Defects
                ),
            _ => throw new InvalidOperationException(
                "Available source result requires a snapshot."
            )
        };

    private static IReadOnlyList<SourceRequest> GetSourceRequests(
        DerivedRecapSetManifest manifest
    ) {
        var byAnchor = new Dictionary<
            EventAddress,
            (PublishedRecapDescriptor Descriptor,
                List<RecapBlockId> BlockIds,
                Dictionary<RecapBlockId, string> ExpectedHashes)
        >();
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            PublishedRecapDescriptor? descriptor = plan switch {
                InheritRecapBlockPlan inherit => new(
                    manifest.RefId,
                    inherit.SourceSetAnchor,
                    inherit.SourcePublicationEnvelopeSha256
                ),
                MaintainRecapBlockPlan {
                    Source: ExistingRecapMaintainSource existing
                } => new(
                    manifest.RefId,
                    existing.SourceSetAnchor,
                    existing.SourcePublicationEnvelopeSha256
                ),
                _ => null
            };
            if (descriptor is null) {
                continue;
            }
            ValidateSourceDescriptor(descriptor);
            if (byAnchor.TryGetValue(
                    descriptor.SetAdmissionAnchor,
                    out var group)) {
                if (!string.Equals(
                        group.Descriptor.EnvelopeSha256,
                        descriptor.EnvelopeSha256,
                        StringComparison.Ordinal
                    )) {
                    throw new InvalidDataException(
                        "One manifest names conflicting source "
                        + "snapshot tokens for the same source set."
                    );
                }
                group.BlockIds.Add(plan.RecapBlockId);
                group.ExpectedHashes.Add(
                    plan.RecapBlockId,
                    GetExpectedInputHash(plan)!
                );
            }
            else {
                byAnchor.Add(
                    descriptor.SetAdmissionAnchor,
                    (
                        descriptor,
                        [plan.RecapBlockId],
                        new Dictionary<RecapBlockId, string> {
                            [plan.RecapBlockId] =
                                GetExpectedInputHash(plan)!
                        }
                    )
                );
            }
        }
        return Array.AsReadOnly(
            byAnchor.Values.Select(
                static group => new SourceRequest(
                    group.Descriptor,
                    Array.AsReadOnly(group.BlockIds.ToArray()),
                    group.ExpectedHashes.ToImmutableDictionary()
                )
            ).ToArray()
        );
    }

    private static void ValidateSourceDescriptor(
        PublishedRecapDescriptor source
    ) {
        if (source.RefId.IsDefault) {
            throw new ArgumentException(
                "Published source descriptor has a default RefId.",
                nameof(source)
            );
        }
        DerivedRecapCodec.ValidateSha256(
            source.EnvelopeSha256,
            "source.envelopeSha256"
        );
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

    private sealed record RestoreAuthorityCapture(
        PublishedRestoreAuthorityKind Kind,
        string AuthorityStateToken,
        DerivedRecapSetManifest Manifest,
        PublishedRecapSet? Publication
    );

    private sealed record RestoreAuthorityRead(
        RestoreAuthorityCapture? Capture,
        IReadOnlyList<RecapStructuralDefect> Defects
    );

    private sealed record RestoreHandleRead(
        RestoreAuthorityCapture? Capture,
        bool IsStale,
        IReadOnlyList<RecapStructuralDefect> Defects
    );

    private sealed record PublishedFinalInspection(
        FinalRecapBlockHealth Health,
        bool IsCommitted
    );

    private sealed class RestoreRawHeadChangedException
        : InvalidOperationException {
        public RestoreRawHeadChangedException(
            EventAddress expected,
            EventAddress? observed
        ) : base(
            "Raw SessionJournal head changed before Published Recap "
            + $"envelope replacement. Expected '{expected}', observed "
            + $"'{observed}'."
        ) {
        }
    }

    private sealed record FrozenInputIndex(
        IReadOnlyDictionary<RecapBlockId, DerivedRecapFrozenInput>
            ById,
        IReadOnlyList<DerivedRecapFrozenInput> Ordered
    );

    private sealed record SourceRequest(
        PublishedRecapDescriptor Descriptor,
        IReadOnlyList<RecapBlockId> BlockIds,
        IReadOnlyDictionary<RecapBlockId, string>
            ExpectedPayloadSha256
    );

    private sealed record PublishedSourceCapture(
        PublishedRecapDescriptor Descriptor,
        PublishedRecapSet Publication,
        byte[] CanonicalEnvelope,
        IReadOnlyList<DerivedRecapFrozenInput> FrozenInputs
    );

    private sealed record PublicationRecheck(
        bool IsExact,
        string? ObservedEnvelopeSha256
    );

    private abstract record SourceCaptureResult {
        private SourceCaptureResult() {
        }

        public sealed record Available(
            PublishedSourceCapture Capture
        ) : SourceCaptureResult;

        public sealed record Changed(
            string ExpectedEnvelopeSha256,
            string? ObservedEnvelopeSha256
        ) : SourceCaptureResult;

        public sealed record Unavailable(
            EventAddress SourceSetAnchor,
            IReadOnlyList<RecapStructuralDefect> Defects
        ) : SourceCaptureResult;
    }

}
