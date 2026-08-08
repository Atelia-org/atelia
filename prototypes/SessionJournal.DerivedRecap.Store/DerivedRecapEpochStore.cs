using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

internal sealed record RecapEpochStoreTestHooks(
    Action? BeforePreviousRecheck = null,
    Action? BeforeRawHeadRecheck = null,
    Action? BeforeBuildingPromotion = null,
    Action? BeforeFinalReplace = null,
    Action? AfterFinalReplace = null,
    Action? BeforePublicationInstall = null,
    Action? BeforePublishedPromotion = null
);

/// <summary>
/// R3B v8 Store candidate. It is intentionally disconnected from the v4
/// production facade until Planner and Store can switch atomically.
/// </summary>
public sealed class DerivedRecapEpochStore {
    private const int MaxInventoryEntries = 1024;
    private const int MaxStoreHeaderBytes = 16 * 1024;

    private readonly RecapDurableFileSystem _fileSystem;
    private readonly RecapEpochStoreTestHooks _testHooks;
    private readonly string _v8Root;
    private readonly string _locksRoot;
    private readonly string _refsRoot;
    private readonly string _quarantineRoot;
    private readonly string _lockPath;
    private readonly string _storeRoot;
    private readonly string _buildingRoot;
    private readonly string _publishedRoot;
    private readonly string _storeHeaderPath;

    private DerivedRecapEpochStore(
        string repositoryPath,
        RefId refId,
        DerivedRecapEpochStoreLimits limits,
        RecapEpochStoreTestHooks? testHooks
    ) {
        SessionRepositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryPath)
        );
        RefId = refId;
        Limits = limits;
        _testHooks = testHooks ?? new RecapEpochStoreTestHooks();
        _fileSystem = new RecapDurableFileSystem(SessionRepositoryPath);
        _v8Root = Path.Combine(
            SessionRepositoryPath,
            "derived",
            "recap",
            "v8"
        );
        _locksRoot = Path.Combine(_v8Root, "locks");
        _refsRoot = Path.Combine(_v8Root, "refs");
        _quarantineRoot = Path.Combine(_v8Root, "quarantine");
        string refToken = refId.ToHexString();
        _lockPath = Path.Combine(_locksRoot, $"{refToken}.lock");
        _storeRoot = Path.Combine(_refsRoot, refToken);
        _buildingRoot = Path.Combine(_storeRoot, "building");
        _publishedRoot = Path.Combine(_storeRoot, "published");
        _storeHeaderPath = Path.Combine(_storeRoot, "store.json");
    }

    public string SessionRepositoryPath { get; }
    public RefId RefId { get; }
    public DerivedRecapEpochStoreLimits Limits { get; }

    public static DerivedRecapEpochStore Open(
        string repositoryPath,
        RefId refId,
        DerivedRecapEpochStoreLimits? limits = null
    ) => OpenCore(repositoryPath, refId, limits, null);

    internal static DerivedRecapEpochStore OpenForTest(
        string repositoryPath,
        RefId refId,
        DerivedRecapEpochStoreLimits? limits,
        RecapEpochStoreTestHooks testHooks
    ) => OpenCore(repositoryPath, refId, limits, testHooks);

    private static DerivedRecapEpochStore OpenCore(
        string repositoryPath,
        RefId refId,
        DerivedRecapEpochStoreLimits? limits,
        RecapEpochStoreTestHooks? testHooks
    ) {
        if (string.IsNullOrWhiteSpace(repositoryPath)) {
            throw new ArgumentException(
                "Session repository path cannot be empty.",
                nameof(repositoryPath)
            );
        }
        string fullPath = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(fullPath)) {
            throw new DirectoryNotFoundException(
                $"Session repository does not exist: {fullPath}"
            );
        }
        if (refId.IsDefault) {
            throw new ArgumentException(
                "DerivedRecap Store RefId cannot be default.",
                nameof(refId)
            );
        }
        return new DerivedRecapEpochStore(
            fullPath,
            refId,
            limits ?? new DerivedRecapEpochStoreLimits(),
            testHooks
        );
    }

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
        RecoverRootStaging();
        if (PathEntryExists(_storeRoot)) {
            throw new IOException(
                $"DerivedRecap v8 Store already exists for RefId {RefId}."
            );
        }
        await CreateRootCoreAsync(cancellationToken).ConfigureAwait(false);
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
        RecoverRootStaging();
        string? quarantine = null;
        if (PathEntryExists(_storeRoot)) {
            if (!Directory.Exists(_storeRoot)) {
                throw new InvalidDataException(
                    "DerivedRecap v8 Store root is not a directory."
                );
            }
            _fileSystem.EnsureDirectoryDurable(_quarantineRoot);
            quarantine = Path.Combine(
                _quarantineRoot,
                $"{RefId.ToHexString()}.{Guid.NewGuid():N}"
            );
            _fileSystem.MoveDirectoryCreateNew(_storeRoot, quarantine);
            _fileSystem.FlushDirectory(_refsRoot);
        }
        await CreateRootCoreAsync(cancellationToken).ConfigureAwait(false);
        if (quarantine is not null) {
            _fileSystem.DeleteDirectoryTree(quarantine);
        }
    }

    public async ValueTask<InstallRecapEpochBuildingResult>
        InstallBuildingAsync(
        DerivedRecapEpochManifest manifest,
        DerivedRecapEpochInput epochInput,
        EventAddress? expectedRawHead = null,
        Func<EventAddress?>? readCurrentRawHead = null,
        CancellationToken cancellationToken = default
    ) {
        if ((expectedRawHead is null) != (readCurrentRawHead is null)) {
            throw new ArgumentException(
                "Expected raw head and reader must be supplied together."
            );
        }
        byte[] manifestBytes;
        byte[] inputBytes;
        try {
            DerivedRecapV8Codec.ValidateEpochSet(manifest, epochInput);
            if (manifest.RefId != RefId) {
                throw new InvalidDataException(
                    "Manifest belongs to a different RefId."
                );
            }
            ValidateRosterLimit(manifest);
            manifestBytes = DerivedRecapV8Codec.EncodeManifest(manifest);
            inputBytes = DerivedRecapV8Codec.EncodeEpochInput(epochInput);
            RequireEncodedLimit(
                manifestBytes,
                Limits.MaxManifestBytes,
                "manifest"
            );
            RequireEncodedLimit(
                inputBytes,
                Limits.MaxEpochInputBytes,
                "epoch input"
            );
            if (epochInput.Previous is RecapEpochPrevious.Prior prior) {
                ValidatePriorPackLimits(prior.Pack);
            }
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or OverflowException
                  or EncoderFallbackException) {
            return new InstallRecapEpochBuildingResult.Invalid(
                exception.Message
            );
        }

        await using FileStream writeLock =
            await AcquireReadyWriteLockAsync(cancellationToken)
                .ConfigureAwait(false);
        RecoverBuildingStaging();
        string anchorToken = EventAddressFileNameCodec.Format(
            manifest.AdmissionAnchor
        );
        string buildingPath = Path.Combine(_buildingRoot, anchorToken);
        string publishedPath = Path.Combine(_publishedRoot, anchorToken);
        if (PathEntryExists(buildingPath) || PathEntryExists(publishedPath)) {
            return new InstallRecapEpochBuildingResult.Conflict(
                manifest.AdmissionAnchor
            );
        }
        EventAddress? conflicting = FindOtherBuilding(
            manifest.AdmissionAnchor
        );
        if (conflicting is not null) {
            return new InstallRecapEpochBuildingResult.Conflict(
                conflicting.Value
            );
        }

        PriorCapture? firstPrior;
        try {
            firstPrior = await CaptureExpectedPriorAsync(
                    epochInput.Previous,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (InvalidDataException exception) {
            return new InstallRecapEpochBuildingResult.PreviousChanged(
                exception.Message
            );
        }

        string stagingPath = Path.Combine(
            _buildingRoot,
            $".{anchorToken}.create.{Guid.NewGuid():N}"
        );
        try {
            _fileSystem.EnsureDirectoryDurable(stagingPath);
            string blocksPath = Path.Combine(stagingPath, "blocks");
            _fileSystem.EnsureDirectoryDurable(blocksPath);
            await _fileSystem.WriteFileCreateNewAsync(
                    Path.Combine(stagingPath, "epoch-input.json"),
                    inputBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);

            _testHooks.BeforePreviousRecheck?.Invoke();
            if (firstPrior is not null) {
                PriorCapture secondPrior =
                    await CapturePriorAsync(
                            firstPrior.Descriptor.AdmissionAnchor,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (firstPrior.Descriptor != secondPrior.Descriptor
                    || !firstPrior.CanonicalPack.AsSpan().SequenceEqual(
                        secondPrior.CanonicalPack
                    )) {
                    return new InstallRecapEpochBuildingResult
                        .PreviousChanged(
                            "Previous Published recap changed during Building install."
                        );
                }
            }

            if (expectedRawHead is EventAddress expected) {
                _testHooks.BeforeRawHeadRecheck?.Invoke();
                EventAddress? observed = readCurrentRawHead!();
                if (observed != expected) {
                    return new InstallRecapEpochBuildingResult.RawHeadChanged(
                        expected,
                        observed
                    );
                }
            }

            await _fileSystem.WriteFileCreateNewAsync(
                    Path.Combine(stagingPath, "manifest.json"),
                    manifestBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
            _fileSystem.FlushDirectory(blocksPath);
            _fileSystem.FlushDirectory(stagingPath);
            _testHooks.BeforeBuildingPromotion?.Invoke();
            _fileSystem.MoveDirectoryCreateNew(stagingPath, buildingPath);
            _fileSystem.FlushDirectory(_buildingRoot);
            return new InstallRecapEpochBuildingResult.Installed(
                Descriptor(manifest)
            );
        }
        finally {
            if (Directory.Exists(stagingPath)) {
                _fileSystem.DeleteDirectoryTree(stagingPath);
            }
        }
    }

    public ValueTask<RecapEpochStoreReadResult> ReadBuildingAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken = default
    ) => ReadStageAsync(
        RecapEpochFinalStage.Building,
        admissionAnchor,
        requireCommittedFinals: false,
        allowManifestWitness: false,
        cancellationToken
    );

    public ValueTask<RecapEpochStoreReadResult> ReadPublishedForRepairAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken = default
    ) => ReadStageAsync(
        RecapEpochFinalStage.Published,
        admissionAnchor,
        requireCommittedFinals: false,
        allowManifestWitness: true,
        cancellationToken
    );

    public async ValueTask<WriteRecapEpochFinalResult> WriteFinalAsync(
        RecapEpochFinalWriteAuthority authority,
        DerivedRecapFinalBlock candidate,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(
                authority.OwnerPath,
                _storeRoot,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "Final write authority belongs to a different Store.",
                nameof(authority)
            );
        }
        if (candidate.RecapBlockId != authority.RecapBlockId) {
            return new WriteRecapEpochFinalResult.Invalid(
                "Final candidate targets a different block."
            );
        }
        byte[] candidateBytes;
        try {
            candidateBytes = DerivedRecapV8Codec.EncodeFinalBlock(candidate);
            RequireEncodedLimit(
                candidateBytes,
                Limits.MaxFinalBlockBytes,
                "final block"
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or EncoderFallbackException) {
            return new WriteRecapEpochFinalResult.Invalid(
                exception.Message
            );
        }

        await using FileStream writeLock =
            await AcquireReadyWriteLockAsync(cancellationToken)
                .ConfigureAwait(false);
        RecapEpochStoreReadResult read = await ReadStageCoreAsync(
                authority.Stage,
                authority.Building.AdmissionAnchor,
                requireCommittedFinals: false,
                allowManifestWitness:
                    authority.Stage == RecapEpochFinalStage.Published,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (read is not RecapEpochStoreReadResult.Available available
            || available.Snapshot.Descriptor != authority.Building) {
            return new WriteRecapEpochFinalResult.Stale("stage-changed");
        }
        if (authority.Stage == RecapEpochFinalStage.Published
            && !string.Equals(
                available.Snapshot.PublishedRepairAuthority?.StateToken,
                authority.PublishedAuthorityStateToken,
                StringComparison.Ordinal
            )) {
            return new WriteRecapEpochFinalResult.Stale(
                available.Snapshot.PublishedRepairAuthority?.StateToken
                    ?? "published-authority-missing"
            );
        }
        RecapEpochBlockInspection inspection = available.Snapshot.Blocks
            .Single(block =>
                block.Definition.RecapBlockId == authority.RecapBlockId);
        if (!string.Equals(
                inspection.Final.StateToken,
                authority.StateToken,
                StringComparison.Ordinal
            )) {
            return new WriteRecapEpochFinalResult.Stale(
                inspection.Final.StateToken
            );
        }
        try {
            DerivedRecapV8Codec.ValidateFinalForManifest(
                available.Snapshot.Manifest,
                candidate
            );
        }
        catch (InvalidDataException exception) {
            return new WriteRecapEpochFinalResult.Invalid(
                exception.Message
            );
        }
        if (inspection.Final is RecapEpochFinalHealth.Healthy healthy) {
            return string.Equals(
                    healthy.Block.PayloadSha256,
                    candidate.PayloadSha256,
                    StringComparison.Ordinal
                )
                ? new WriteRecapEpochFinalResult.AlreadyHealthy(
                    healthy.Block
                )
                : new WriteRecapEpochFinalResult.HealthyConflict(
                    healthy.Block
                );
        }

        string stagePath = StagePath(
            authority.Stage,
            authority.Building.AdmissionAnchor
        );
        string finalPath = Path.Combine(
            stagePath,
            "blocks",
            $"{candidate.RecapBlockId.Value}.json"
        );
        if (inspection.Final is RecapEpochFinalHealth.Missing) {
            await _fileSystem.WriteFileCreateNewAsync(
                    finalPath,
                    candidateBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else {
            await _fileSystem.WriteFileAtomicReplaceAsync(
                    finalPath,
                    candidateBytes,
                    _testHooks.BeforeFinalReplace,
                    _testHooks.AfterFinalReplace,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        return new WriteRecapEpochFinalResult.Installed(
            BytesStateToken(candidateBytes)
        );
    }

    public async ValueTask<PublishRecapEpochResult> PublishBuildingAsync(
        RecapEpochBuildingDescriptor descriptor,
        EventAddress? expectedRawHead = null,
        Func<EventAddress?>? readCurrentRawHead = null,
        CancellationToken cancellationToken = default
    ) {
        if ((expectedRawHead is null) != (readCurrentRawHead is null)) {
            throw new ArgumentException(
                "Expected raw head and reader must be supplied together."
            );
        }
        await using FileStream writeLock =
            await AcquireReadyWriteLockAsync(cancellationToken)
                .ConfigureAwait(false);
        string publishedPath = StagePath(
            RecapEpochFinalStage.Published,
            descriptor.AdmissionAnchor
        );
        if (Directory.Exists(publishedPath)) {
            try {
                CommittedPublication committed =
                    await ReadCommittedPublicationCoreAsync(
                            descriptor.AdmissionAnchor,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                return committed.Publication.FrozenManifest.RefId
                        == descriptor.RefId
                    && committed.Publication.FrozenManifest.AdmissionAnchor
                        == descriptor.AdmissionAnchor
                    && string.Equals(
                        committed.Publication.FrozenManifest
                            .ManifestPayloadSha256,
                        descriptor.ManifestPayloadSha256,
                        StringComparison.Ordinal
                    )
                    ? new PublishRecapEpochResult.AlreadyPublished(
                        committed.Descriptor
                    )
                    : new PublishRecapEpochResult.Stale(
                        "Published recap at the anchor belongs to a different Building descriptor."
                    );
            }
            catch (InvalidDataException exception) {
                return new PublishRecapEpochResult.NotPublishable(
                    exception.Message
                );
            }
        }
        RecapEpochStoreReadResult read = await ReadStageCoreAsync(
                RecapEpochFinalStage.Building,
                descriptor.AdmissionAnchor,
                requireCommittedFinals: false,
                allowManifestWitness: false,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (read is not RecapEpochStoreReadResult.Available available
            || available.Snapshot.Descriptor != descriptor) {
            return new PublishRecapEpochResult.Stale(
                "Building descriptor changed or is unavailable."
            );
        }
        DerivedRecapFinalBlock[] finals;
        try {
            finals = RequireHealthyFinals(available.Snapshot);
        }
        catch (InvalidDataException exception) {
            return new PublishRecapEpochResult.NotPublishable(
                exception.Message
            );
        }
        PublishedRecapEpoch publication;
        byte[] publicationBytes;
        try {
            publication = DerivedRecapV8Codec.CreatePublication(
                available.Snapshot.Manifest,
                finals
            );
            publicationBytes = DerivedRecapV8Codec.EncodePublication(
                publication
            );
            ValidatePublishedAggregateLimits(publication, finals);
            RequireEncodedLimit(
                publicationBytes,
                Limits.MaxPublicationBytes,
                "publication"
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or OverflowException
                  or EncoderFallbackException) {
            return new PublishRecapEpochResult.NotPublishable(
                exception.Message
            );
        }
        if (expectedRawHead is EventAddress expected) {
            _testHooks.BeforeRawHeadRecheck?.Invoke();
            EventAddress? observed = readCurrentRawHead!();
            if (observed != expected) {
                return new PublishRecapEpochResult.RawHeadChanged(
                    expected,
                    observed
                );
            }
        }
        string buildingPath = StagePath(
            RecapEpochFinalStage.Building,
            descriptor.AdmissionAnchor
        );
        string publicationPath = Path.Combine(
            buildingPath,
            "publication.json"
        );
        if (!File.Exists(publicationPath)) {
            _testHooks.BeforePublicationInstall?.Invoke();
            await _fileSystem.WriteFileCreateNewAsync(
                    publicationPath,
                    publicationBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else {
            byte[] existing = await _fileSystem.ReadBoundedAsync(
                    publicationPath,
                    Limits.MaxPublicationBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(publicationBytes)) {
                return new PublishRecapEpochResult.NotPublishable(
                    "Building contains a conflicting publication seal."
                );
            }
        }
        _fileSystem.FlushDirectory(buildingPath);
        _testHooks.BeforePublishedPromotion?.Invoke();
        _fileSystem.MoveDirectoryCreateNew(buildingPath, publishedPath);
        _fileSystem.FlushDirectory(_buildingRoot);
        _fileSystem.FlushDirectory(_publishedRoot);
        return new PublishRecapEpochResult.Published(
            new PublishedRecapEpochDescriptor(
                publication.RefId,
                publication.AdmissionAnchor,
                publication.EnvelopeSha256
            )
        );
    }

    public async ValueTask<PublishRecapEpochResult> ResealPublishedAsync(
        RecapEpochPublishedRepairAuthority authority,
        EventAddress? expectedRawHead = null,
        Func<EventAddress?>? readCurrentRawHead = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(authority);
        if (!string.Equals(
                authority.OwnerPath,
                _storeRoot,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "Published repair authority belongs to a different Store.",
                nameof(authority)
            );
        }
        if ((expectedRawHead is null) != (readCurrentRawHead is null)) {
            throw new ArgumentException(
                "Expected raw head and reader must be supplied together."
            );
        }
        await using FileStream writeLock =
            await AcquireReadyWriteLockAsync(cancellationToken)
                .ConfigureAwait(false);
        RecapEpochStoreReadResult read = await ReadStageCoreAsync(
                RecapEpochFinalStage.Published,
                authority.AdmissionAnchor,
                requireCommittedFinals: false,
                allowManifestWitness: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (read is not RecapEpochStoreReadResult.Available available
            || available.Snapshot.Manifest.ManifestPayloadSha256
                != authority.ManifestPayloadSha256
            || available.Snapshot.PublishedRepairAuthority?.Kind
                != authority.Kind
            || !string.Equals(
                available.Snapshot.PublishedRepairAuthority.StateToken,
                authority.StateToken,
                StringComparison.Ordinal
            )) {
            return new PublishRecapEpochResult.Stale(
                "Published recap descriptor changed during repair."
            );
        }
        DerivedRecapFinalBlock[] finals;
        try {
            finals = RequireHealthyFinals(available.Snapshot);
        }
        catch (InvalidDataException exception) {
            return new PublishRecapEpochResult.NotPublishable(
                exception.Message
            );
        }
        PublishedRecapEpoch publication;
        byte[] publicationBytes;
        try {
            publication = DerivedRecapV8Codec.CreatePublication(
                available.Snapshot.Manifest,
                finals
            );
            publicationBytes = DerivedRecapV8Codec.EncodePublication(
                publication
            );
            ValidatePublishedAggregateLimits(publication, finals);
            RequireEncodedLimit(
                publicationBytes,
                Limits.MaxPublicationBytes,
                "publication"
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or OverflowException
                  or EncoderFallbackException) {
            return new PublishRecapEpochResult.NotPublishable(
                exception.Message
            );
        }
        if (expectedRawHead is EventAddress expectedHead) {
            _testHooks.BeforeRawHeadRecheck?.Invoke();
            EventAddress? observed = readCurrentRawHead!();
            if (observed != expectedHead) {
                return new PublishRecapEpochResult.RawHeadChanged(
                    expectedHead,
                    observed
                );
            }
        }
        string publicationPath = Path.Combine(
            StagePath(
                RecapEpochFinalStage.Published,
                authority.AdmissionAnchor
            ),
            "publication.json"
        );
        if (File.Exists(publicationPath)) {
            await _fileSystem.WriteFileAtomicReplaceAsync(
                    publicationPath,
                    publicationBytes,
                    _testHooks.BeforePublicationInstall,
                    afterReplace: null,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else {
            _testHooks.BeforePublicationInstall?.Invoke();
            await _fileSystem.WriteFileCreateNewAsync(
                    publicationPath,
                    publicationBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        return new PublishRecapEpochResult.Published(
            new PublishedRecapEpochDescriptor(
                publication.RefId,
                publication.AdmissionAnchor,
                publication.EnvelopeSha256
            )
        );
    }

    public async ValueTask<RecapEpochSelectionResult> SelectLatestAsync(
        IReadOnlyList<EventAddress> headToRoot,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(headToRoot);
        await using FileStream readLock =
            await AcquireReadyReadLockAsync(cancellationToken)
                .ConfigureAwait(false);
        foreach (EventAddress address in headToRoot) {
            string path = StagePath(
                RecapEpochFinalStage.Published,
                address
            );
            if (!Directory.Exists(path)) {
                continue;
            }
            try {
                CommittedPublication committed =
                    await ReadCommittedPublicationCoreAsync(
                            address,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                return new RecapEpochSelectionResult.Selected(
                    committed.Descriptor
                );
            }
            catch (InvalidDataException exception) {
                return new RecapEpochSelectionResult.Invalid(
                    address,
                    exception.Message
                );
            }
        }
        return new RecapEpochSelectionResult.Empty();
    }

    public async ValueTask<DerivedRecapMaterialization> MaterializeAsync(
        PublishedRecapEpochDescriptor descriptor,
        CancellationToken cancellationToken = default
    ) {
        await using FileStream readLock =
            await AcquireReadyReadLockAsync(cancellationToken)
                .ConfigureAwait(false);
        CommittedPublication committed =
            await ReadCommittedPublicationCoreAsync(
                    descriptor.AdmissionAnchor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (committed.Descriptor != descriptor) {
            throw new InvalidDataException(
                "Published recap descriptor changed before materialization."
            );
        }
        var contributions = new List<SessionContextContribution>(
            committed.Finals.Length
        );
        foreach (DerivedRecapFinalBlock block in committed.Finals) {
            contributions.Add(new SessionContextContribution(
                block.Target,
                block.Content,
                SessionContextContributionHasher.CodecId,
                block.ContentSha256,
                committed.EpochInput.AdmissionBoundary.Address
            ));
        }
        ImmutableArray<SessionContextContribution> normalized =
            SessionContextContributionContract.ValidateAndNormalize(
                contributions
            );
        if (!contributions.Select(static item => item.Target).SequenceEqual(
                normalized.Select(static item => item.Target)
            )) {
            throw new InvalidDataException(
                "Published recap roster is not in canonical context order."
            );
        }
        return new DerivedRecapMaterialization(
            descriptor.AdmissionAnchor,
            normalized
        );
    }

    public async ValueTask<PriorRecapPackSnapshot> ReadPriorPackAsync(
        PublishedRecapEpochDescriptor descriptor,
        CancellationToken cancellationToken = default
    ) {
        await using FileStream readLock =
            await AcquireReadyReadLockAsync(cancellationToken)
                .ConfigureAwait(false);
        PriorCapture capture = await CapturePriorAsync(
                descriptor.AdmissionAnchor,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (capture.Descriptor != descriptor) {
            throw new InvalidDataException(
                "Published recap descriptor changed while freezing prior pack."
            );
        }
        return capture.Pack;
    }

    private async ValueTask<RecapEpochStoreReadResult> ReadStageAsync(
        RecapEpochFinalStage stage,
        EventAddress admissionAnchor,
        bool requireCommittedFinals,
        bool allowManifestWitness,
        CancellationToken cancellationToken
    ) {
        await using FileStream readLock =
            await AcquireReadyReadLockAsync(cancellationToken)
                .ConfigureAwait(false);
        return await ReadStageCoreAsync(
                stage,
                admissionAnchor,
                requireCommittedFinals,
                allowManifestWitness,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<RecapEpochStoreReadResult> ReadStageCoreAsync(
        RecapEpochFinalStage stage,
        EventAddress admissionAnchor,
        bool requireCommittedFinals,
        bool allowManifestWitness,
        CancellationToken cancellationToken
    ) {
        string stagePath = StagePath(stage, admissionAnchor);
        if (!Directory.Exists(stagePath)) {
            return new RecapEpochStoreReadResult.Missing(admissionAnchor);
        }
        try {
            DerivedRecapEpochManifest manifest =
                DerivedRecapV8Codec.DecodeManifest(
                    await _fileSystem.ReadBoundedAsync(
                            Path.Combine(stagePath, "manifest.json"),
                            Limits.MaxManifestBytes,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                );
            DerivedRecapEpochInput epochInput =
                DerivedRecapV8Codec.DecodeEpochInput(
                    await _fileSystem.ReadBoundedAsync(
                            Path.Combine(stagePath, "epoch-input.json"),
                            Limits.MaxEpochInputBytes,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                );
            DerivedRecapV8Codec.ValidateEpochSet(manifest, epochInput);
            if (manifest.RefId != RefId
                || manifest.AdmissionAnchor != admissionAnchor) {
                throw new InvalidDataException(
                    "Epoch stage identity differs from its path."
                );
            }
            ValidateRosterLimit(manifest);
            PublishedRecapEpoch? publication = null;
            RecapEpochPublishedRepairAuthority? repairAuthority = null;
            if (stage == RecapEpochFinalStage.Published) {
                string publicationPath = Path.Combine(
                    stagePath,
                    "publication.json"
                );
                string publicationStateToken;
                if (!File.Exists(publicationPath)) {
                    if (!allowManifestWitness) {
                        throw new InvalidDataException(
                            "Published recap has no publication envelope."
                        );
                    }
                    publicationStateToken = "missing";
                }
                else {
                    byte[] publicationBytes =
                        await _fileSystem.ReadBoundedAsync(
                                publicationPath,
                                Limits.MaxPublicationBytes,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    try {
                        publication = DerivedRecapV8Codec.DecodePublication(
                            publicationBytes
                        );
                    }
                    catch (Exception exception)
                        when (allowManifestWitness
                              && exception is (
                                  InvalidDataException
                                  or ArgumentException
                                  or OverflowException
                              )) {
                        publication = null;
                    }
                    publicationStateToken = BytesStateToken(
                        publicationBytes
                    );
                }
                if (publication is not null) {
                    if (publication.RefId != RefId
                        || publication.AdmissionAnchor != admissionAnchor
                        || !DerivedRecapV8Codec.EncodeManifest(
                                publication.FrozenManifest
                            )
                            .AsSpan()
                            .SequenceEqual(
                                DerivedRecapV8Codec.EncodeManifest(manifest)
                            )) {
                        throw new InvalidDataException(
                            "Published envelope does not bind the stage manifest."
                        );
                    }
                }
                if (publication is null && !allowManifestWitness) {
                    throw new InvalidDataException(
                        "Published recap has no healthy publication envelope."
                    );
                }
                repairAuthority = new RecapEpochPublishedRepairAuthority(
                    _storeRoot,
                    admissionAnchor,
                    manifest.ManifestPayloadSha256,
                    publication is null
                        ? RecapEpochPublishedAuthorityKind.ManifestWitness
                        : RecapEpochPublishedAuthorityKind.Publication,
                    publicationStateToken
                );
            }

            var blocks = new List<RecapEpochBlockInspection>(
                manifest.Blocks.Count
            );
            for (int ordinal = 0; ordinal < manifest.Blocks.Count; ordinal++) {
                RecapEpochBlockDefinition definition = manifest.Blocks[ordinal];
                RecapEpochBlockCommitment? commitment = publication?
                    .BlockCommitments[ordinal];
                RecapEpochFinalHealth health =
                    await InspectFinalCoreAsync(
                            stagePath,
                            manifest,
                            definition,
                            requireCommittedFinals ? commitment : null,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                blocks.Add(new RecapEpochBlockInspection(
                    definition,
                    health,
                    health is RecapEpochFinalHealth.Unavailable
                        ? null
                        : new RecapEpochFinalWriteAuthority(
                            _storeRoot,
                            stage,
                            Descriptor(manifest),
                            definition.RecapBlockId,
                            health.StateToken,
                            repairAuthority?.StateToken
                        )
                ));
            }
            return new RecapEpochStoreReadResult.Available(
                new RecapEpochStoreSnapshot(
                    stage,
                    Descriptor(manifest),
                    manifest,
                    epochInput,
                    Array.AsReadOnly(blocks.ToArray()),
                    publication,
                    repairAuthority
                )
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or IOException
                  or UnauthorizedAccessException
                  or OverflowException) {
            return new RecapEpochStoreReadResult.Invalid(
                admissionAnchor,
                exception.Message
            );
        }
    }

    private async ValueTask<RecapEpochFinalHealth> InspectFinalCoreAsync(
        string stagePath,
        DerivedRecapEpochManifest manifest,
        RecapEpochBlockDefinition definition,
        RecapEpochBlockCommitment? commitment,
        CancellationToken cancellationToken
    ) {
        string path = Path.Combine(
            stagePath,
            "blocks",
            $"{definition.RecapBlockId.Value}.json"
        );
        if (!File.Exists(path)) {
            return new RecapEpochFinalHealth.Missing("missing");
        }
        byte[] bytes;
        try {
            bytes = await _fileSystem.ReadBoundedAsync(
                    path,
                    Limits.MaxFinalBlockBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or IOException
                  or UnauthorizedAccessException) {
            return new RecapEpochFinalHealth.Unavailable(
                exception.Message
            );
        }
        string stateToken = BytesStateToken(bytes);
        try {
            DerivedRecapFinalBlock block =
                DerivedRecapV8Codec.DecodeFinalBlock(bytes);
            DerivedRecapV8Codec.ValidateFinalForManifest(manifest, block);
            if (commitment is not null
                && (commitment.RecapBlockId != block.RecapBlockId
                    || commitment.Target != block.Target
                    || !string.Equals(
                        commitment.EpochBlockExecutionSha256,
                        block.EpochBlockExecutionSha256,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        commitment.PayloadSha256,
                        block.PayloadSha256,
                        StringComparison.Ordinal
                    ))) {
                throw new InvalidDataException(
                    "Final block differs from publication commitment."
                );
            }
            return new RecapEpochFinalHealth.Healthy(block, stateToken);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or OverflowException) {
            return new RecapEpochFinalHealth.Damaged(
                exception.Message,
                stateToken
            );
        }
    }

    private async ValueTask<PriorCapture?> CaptureExpectedPriorAsync(
        RecapEpochPrevious previous,
        CancellationToken cancellationToken
    ) {
        if (previous is RecapEpochPrevious.Empty) {
            return null;
        }
        var prior = (RecapEpochPrevious.Prior)previous;
        PriorCapture capture = await CapturePriorAsync(
                prior.Pack.Source.AdmissionAnchor,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (capture.Descriptor != prior.Pack.Source
            || !capture.CanonicalPack.AsSpan().SequenceEqual(
                DerivedRecapV8Codec.EncodePriorPack(prior.Pack)
            )) {
            throw new InvalidDataException(
                "Frozen prior pack differs from exact Published source."
            );
        }
        return capture;
    }

    private async ValueTask<PriorCapture> CapturePriorAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken
    ) {
        CommittedPublication committed =
            await ReadCommittedPublicationCoreAsync(
                    admissionAnchor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        PriorRecapBlockSnapshot[] blocks = [
            .. committed.Finals.Select(block =>
                DerivedRecapV8Codec.CreatePriorBlock(
                    block.RecapBlockId,
                    block.Target,
                    block.Content,
                    block.EpochBlockExecutionSha256,
                    block.PayloadSha256
                ))
        ];
        PriorRecapPackSnapshot pack = DerivedRecapV8Codec.CreatePriorPack(
            committed.Descriptor,
            blocks
        );
        ValidatePriorPackLimits(pack);
        return new PriorCapture(
            committed.Descriptor,
            pack,
            DerivedRecapV8Codec.EncodePriorPack(pack)
        );
    }

    private async ValueTask<CommittedPublication>
        ReadCommittedPublicationCoreAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken
    ) {
        RecapEpochStoreReadResult read = await ReadStageCoreAsync(
                RecapEpochFinalStage.Published,
                admissionAnchor,
                requireCommittedFinals: true,
                allowManifestWitness: false,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (read is not RecapEpochStoreReadResult.Available available) {
            throw new InvalidDataException(
                read is RecapEpochStoreReadResult.Invalid invalid
                    ? invalid.Detail
                    : "Published recap is missing."
            );
        }
        DerivedRecapFinalBlock[] finals =
            RequireHealthyFinals(available.Snapshot);
        PublishedRecapEpoch publication = available.Snapshot.Publication!;
        return new CommittedPublication(
            new PublishedRecapEpochDescriptor(
                publication.RefId,
                publication.AdmissionAnchor,
                publication.EnvelopeSha256
            ),
            publication,
            available.Snapshot.EpochInput,
            finals
        );
    }

    private void ValidatePublishedAggregateLimits(
        PublishedRecapEpoch publication,
        IReadOnlyList<DerivedRecapFinalBlock> finals
    ) {
        PriorRecapBlockSnapshot[] priorBlocks = [
            .. finals.Select(block =>
                DerivedRecapV8Codec.CreatePriorBlock(
                    block.RecapBlockId,
                    block.Target,
                    block.Content,
                    block.EpochBlockExecutionSha256,
                    block.PayloadSha256
                ))
        ];
        var descriptor = new PublishedRecapEpochDescriptor(
            publication.RefId,
            publication.AdmissionAnchor,
            publication.EnvelopeSha256
        );
        PriorRecapPackSnapshot pack = DerivedRecapV8Codec.CreatePriorPack(
            descriptor,
            priorBlocks
        );
        ValidatePriorPackLimits(pack);
    }

    private void ValidatePriorPackLimits(PriorRecapPackSnapshot pack) {
        int totalUtf8 = DerivedRecapV8Codec.GetTotalRecapPackUtf8Bytes(
            pack.Blocks
        );
        if (totalUtf8 > Limits.MaxTotalRecapPackUtf8Bytes) {
            throw new InvalidDataException(
                $"Recap pack content is {totalUtf8} UTF-8 bytes; limit is "
                + $"{Limits.MaxTotalRecapPackUtf8Bytes}."
            );
        }
        int canonicalBytes =
            DerivedRecapV8Codec.GetCanonicalPriorPackByteCount(pack);
        if (canonicalBytes > Limits.MaxCanonicalPriorPackBytes) {
            throw new InvalidDataException(
                $"Canonical prior pack is {canonicalBytes} bytes; limit is "
                + $"{Limits.MaxCanonicalPriorPackBytes}."
            );
        }
    }

    private void ValidateRosterLimit(DerivedRecapEpochManifest manifest) {
        if (manifest.Blocks.Count > Limits.MaxRecapBlockCount) {
            throw new InvalidDataException(
                $"Manifest roster has {manifest.Blocks.Count} blocks; limit "
                + $"is {Limits.MaxRecapBlockCount}."
            );
        }
    }

    private static DerivedRecapFinalBlock[] RequireHealthyFinals(
        RecapEpochStoreSnapshot snapshot
    ) {
        var finals = new DerivedRecapFinalBlock[snapshot.Blocks.Count];
        for (int ordinal = 0; ordinal < snapshot.Blocks.Count; ordinal++) {
            if (snapshot.Blocks[ordinal].Final
                is not RecapEpochFinalHealth.Healthy healthy) {
                throw new InvalidDataException(
                    $"Block '{snapshot.Blocks[ordinal].Definition.RecapBlockId}' has no healthy final."
                );
            }
            finals[ordinal] = healthy.Block;
        }
        return finals;
    }

    private EventAddress? FindOtherBuilding(EventAddress target) {
        int count = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     _buildingRoot
                 )) {
            if (++count > MaxInventoryEntries) {
                throw new InvalidDataException(
                    "Building inventory exceeds the Store bound."
                );
            }
            string name = Path.GetFileName(entry);
            if (name.StartsWith(".", StringComparison.Ordinal)) {
                continue;
            }
            if (!Directory.Exists(entry)
                || !EventAddressFileNameCodec.TryParse(
                    name,
                    out EventAddress address
                )) {
                throw new InvalidDataException(
                    "Building inventory contains an invalid entry."
                );
            }
            if (address != target) {
                return address;
            }
        }
        return null;
    }

    private async ValueTask<FileStream> AcquireReadyReadLockAsync(
        CancellationToken cancellationToken
    ) {
        FileStream stream =
            await _fileSystem.AcquireExistingExclusiveReadLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        try {
            await RequireReadyAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<FileStream> AcquireReadyWriteLockAsync(
        CancellationToken cancellationToken
    ) {
        FileStream stream =
            await _fileSystem.AcquireExistingExclusiveWriteLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        try {
            await RequireReadyAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask RequireReadyAsync(
        CancellationToken cancellationToken
    ) {
        if (!Directory.Exists(_storeRoot)
            || !Directory.Exists(_buildingRoot)
            || !Directory.Exists(_publishedRoot)) {
            throw new InvalidDataException(
                "DerivedRecap v8 Store is not initialized."
            );
        }
        RefId decoded = DerivedRecapV8Codec.DecodeStoreHeader(
            await _fileSystem.ReadBoundedAsync(
                    _storeHeaderPath,
                    MaxStoreHeaderBytes,
                    cancellationToken
                )
                .ConfigureAwait(false)
        );
        if (decoded != RefId) {
            throw new InvalidDataException(
                "DerivedRecap v8 Store header RefId mismatch."
            );
        }
    }

    private async ValueTask CreateRootCoreAsync(
        CancellationToken cancellationToken
    ) {
        string staging = Path.Combine(
            _refsRoot,
            $".{RefId.ToHexString()}.create.{Guid.NewGuid():N}"
        );
        try {
            _fileSystem.EnsureDirectoryDurable(staging);
            _fileSystem.EnsureDirectoryDurable(
                Path.Combine(staging, "building")
            );
            _fileSystem.EnsureDirectoryDurable(
                Path.Combine(staging, "published")
            );
            await _fileSystem.WriteFileCreateNewAsync(
                    Path.Combine(staging, "store.json"),
                    DerivedRecapV8Codec.EncodeStoreHeader(RefId),
                    cancellationToken
                )
                .ConfigureAwait(false);
            _fileSystem.FlushDirectory(staging);
            _fileSystem.MoveDirectoryCreateNew(staging, _storeRoot);
            _fileSystem.FlushDirectory(_refsRoot);
        }
        finally {
            if (Directory.Exists(staging)) {
                _fileSystem.DeleteDirectoryTree(staging);
            }
        }
    }

    private void RecoverRootStaging() {
        if (!Directory.Exists(_refsRoot)) {
            return;
        }
        string prefix = $".{RefId.ToHexString()}.create.";
        int count = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     _refsRoot
                 )) {
            if (++count > MaxInventoryEntries) {
                throw new InvalidDataException(
                    "Recap root staging inventory exceeds the Store bound."
                );
            }
            string name = Path.GetFileName(entry);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) {
                continue;
            }
            string suffix = name[prefix.Length..];
            if (suffix.Length != 32 || !IsLowerHex(suffix)) {
                throw new InvalidDataException(
                    "Recap root staging entry has an invalid name."
                );
            }
            if (!Directory.Exists(entry)) {
                throw new InvalidDataException(
                    "Recap root staging entry is not a directory."
                );
            }
            _fileSystem.DeleteDirectoryTree(entry);
        }
    }

    private void RecoverBuildingStaging() {
        int count = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     _buildingRoot
                 )) {
            if (++count > MaxInventoryEntries) {
                throw new InvalidDataException(
                    "Building staging inventory exceeds the Store bound."
                );
            }
            string name = Path.GetFileName(entry);
            if (!name.StartsWith(".", StringComparison.Ordinal)) {
                continue;
            }
            int marker = name.LastIndexOf(
                ".create.",
                StringComparison.Ordinal
            );
            if (marker <= 1) {
                throw new InvalidDataException(
                    "Building staging entry has an invalid name."
                );
            }
            string anchorToken = name[1..marker];
            string nonce = name[(marker + ".create.".Length)..];
            if (!EventAddressFileNameCodec.TryParse(anchorToken, out _)
                || nonce.Length != 32
                || !IsLowerHex(nonce)) {
                throw new InvalidDataException(
                    "Building staging entry has an invalid name."
                );
            }
            if (!Directory.Exists(entry)) {
                throw new InvalidDataException(
                    "Building staging entry is not a directory."
                );
            }
            _fileSystem.DeleteDirectoryTree(entry);
        }
    }

    private void EnsureScaffolding() {
        _fileSystem.EnsureDirectoryDurable(_v8Root);
        _fileSystem.EnsureDirectoryDurable(_locksRoot);
        _fileSystem.EnsureDirectoryDurable(_refsRoot);
    }

    private string StagePath(
        RecapEpochFinalStage stage,
        EventAddress admissionAnchor
    ) => Path.Combine(
        stage == RecapEpochFinalStage.Building
            ? _buildingRoot
            : _publishedRoot,
        EventAddressFileNameCodec.Format(admissionAnchor)
    );

    private static RecapEpochBuildingDescriptor Descriptor(
        DerivedRecapEpochManifest manifest
    ) => new(
        manifest.RefId,
        manifest.AdmissionAnchor,
        manifest.ManifestPayloadSha256
    );

    private static void RequireEncodedLimit(
        byte[] bytes,
        int maximum,
        string name
    ) {
        if (bytes.Length > maximum) {
            throw new InvalidDataException(
                $"Canonical {name} is {bytes.Length} bytes; limit is {maximum}."
            );
        }
    }

    private static string BytesStateToken(ReadOnlySpan<byte> bytes)
        => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static string FileStateToken(string path) {
        var info = new FileInfo(path);
        return $"file:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private static string PathStateToken(string path)
        => File.Exists(path) ? FileStateToken(path) : "missing";

    private static bool IsLowerHex(string value)
        => value.Length != 0
            && value.AsSpan().ContainsAnyExcept(
                "0123456789abcdef"
            ) is false;

    private static bool PathEntryExists(string path)
        => File.Exists(path) || Directory.Exists(path);

    private sealed record PriorCapture(
        PublishedRecapEpochDescriptor Descriptor,
        PriorRecapPackSnapshot Pack,
        byte[] CanonicalPack
    );

    private sealed record CommittedPublication(
        PublishedRecapEpochDescriptor Descriptor,
        PublishedRecapEpoch Publication,
        DerivedRecapEpochInput EpochInput,
        DerivedRecapFinalBlock[] Finals
    );
}
