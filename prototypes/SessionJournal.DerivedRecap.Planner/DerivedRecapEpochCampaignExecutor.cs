using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Store;
using System.Text;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Serial, complete-roster execution owner for bounded online shared epochs.
/// It deliberately has no per-member cursor, route, inherit, or checkpoint
/// state. Every newly admitted epoch freezes one input and invokes every
/// roster member exactly once unless a healthy direct final already exists.
/// </summary>
public sealed partial class DerivedRecapEpochCampaignExecutor {
    private readonly SessionJournalReadView _engine;
    private readonly DerivedRecapEpochStore _store;
    private readonly Lazy<RecapEpochActiveConfiguration>
        _activeConfiguration;
    private readonly RecapEpochOperationLimits _limits;
    private readonly IRecapBlockMaintainerRegistry _maintainers;

    public DerivedRecapEpochCampaignExecutor(
        SessionJournalReadView engine,
        DerivedRecapEpochStore store,
        RecapEpochPlanningConfiguration configuration,
        RecapEpochOperationLimits limits,
        IRecapBlockMaintainerRegistry maintainers
    ) : this(
        engine,
        store,
        () => new RecapEpochActiveConfiguration(
            configuration,
            limits,
            store.Limits
        ),
        limits,
        maintainers
    ) {
        ArgumentNullException.ThrowIfNull(configuration);
    }

    public DerivedRecapEpochCampaignExecutor(
        SessionJournalReadView engine,
        DerivedRecapEpochStore store,
        Func<RecapEpochPlanningConfiguration> configurationFactory,
        RecapEpochOperationLimits limits,
        IRecapBlockMaintainerRegistry maintainers
    ) : this(
        engine,
        store,
        () => new RecapEpochActiveConfiguration(
            configurationFactory(),
            limits,
            store.Limits
        ),
        limits,
        maintainers
    ) {
        ArgumentNullException.ThrowIfNull(configurationFactory);
    }

    public DerivedRecapEpochCampaignExecutor(
        SessionJournalReadView engine,
        DerivedRecapEpochStore store,
        Func<RecapEpochActiveConfiguration> configurationFactory,
        RecapEpochOperationLimits recoveryLimits,
        IRecapBlockMaintainerRegistry maintainers
    ) {
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        _store = store
            ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(configurationFactory);
        _activeConfiguration = new Lazy<RecapEpochActiveConfiguration>(
            () => configurationFactory()
                ?? throw new InvalidDataException(
                    "Active recap epoch configuration factory returned null."
                ),
            LazyThreadSafetyMode.ExecutionAndPublication
        );
        _limits = recoveryLimits
            ?? throw new ArgumentNullException(nameof(recoveryLimits));
        _maintainers = maintainers
            ?? throw new ArgumentNullException(nameof(maintainers));
        RequireSameBinding(engine, store);
    }

    public async ValueTask<DerivedRecapEpochOperationResult>
        RunOnlineAsync(
        CancellationToken cancellationToken = default
    ) {
        int epochsPublished = 0;
        int maintainerCalls = 0;

        EventAddress? rawHead = _engine.ReadCurrentHead();
        if (rawHead is null) {
            if (_engine.ReadCurrentHead() is not null) {
                return Unavailable(
                    "RawHeadChanged",
                    "Raw head appeared while checking an empty SessionJournal."
                );
            }
            return new DerivedRecapEpochOperationResult.Fresh(
                null,
                0,
                0,
                "SessionJournal is empty."
            );
        }

        SessionCurrentLineagePrefix prefix = CapturePrefix(
            cancellationToken
        );
        if (_engine.ReadCurrentHead() != prefix.CapturedHead) {
            return Unavailable(
                "RawHeadChanged",
                "Raw head changed while capturing the resume authority."
            );
        }
        rawHead = prefix.CapturedHead;
        RecapEpochBuildingSelectionResult buildingSelection;
        try {
            buildingSelection = await _store.SelectBuildingAsync(
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAvailability(exception)) {
            return Unavailable("StoreUnavailable", exception.Message);
        }

        if (buildingSelection
            is RecapEpochBuildingSelectionResult.Invalid invalidBuilding) {
            return Unavailable("BuildingInvalid", invalidBuilding.Detail);
        }
        if (buildingSelection
            is RecapEpochBuildingSelectionResult.Selected selectedBuilding) {
            DerivedRecapEpochOperationResult? validation =
                ValidateSnapshotAuthority(
                    selectedBuilding.Snapshot,
                    prefix,
                    rawHead.Value,
                    cancellationToken
                );
            if (validation is not null) {
                return validation;
            }
            DerivedRecapEpochOperationResult? budget = CheckBudget(
                selectedBuilding.Snapshot,
                epochsPublished,
                maintainerCalls
            );
            if (budget is not null) {
                return budget;
            }
            SerialEpochKernelResult resumed = await ExecuteSnapshotAsync(
                    selectedBuilding.Snapshot,
                    maintainerCalls,
                    cancellationToken
                )
                .ConfigureAwait(false);
            maintainerCalls = checked(
                maintainerCalls + resumed.StartedCallCount
            );
            if (!resumed.Succeeded) {
                return Failed(
                    selectedBuilding.Snapshot,
                    resumed.PrimaryFailure!
                );
            }
            PublishRecapEpochResult resumePublish =
                await _store.PublishBuildingAsync(
                        selectedBuilding.Snapshot.Descriptor,
                        prefix.CapturedHead,
                        _engine.ReadCurrentHead,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);
            if (resumePublish is not (
                    PublishRecapEpochResult.Published
                    or PublishRecapEpochResult.AlreadyPublished
                )) {
                return Unavailable(
                    "PublicationUnavailable",
                    DescribePublish(resumePublish)
                );
            }
            epochsPublished = checked(epochsPublished + 1);
        }

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            rawHead = _engine.ReadCurrentHead();
            if (rawHead is null) {
                return Unavailable(
                    "RawHeadChanged",
                    "SessionJournal became empty during recap execution."
                );
            }
            prefix = CapturePrefix(cancellationToken);
            if (prefix.CapturedHead != rawHead.Value) {
                continue;
            }

            RecapEpochSelectionResult selection;
            try {
                selection = await _store.SelectLatestAsync(
                        prefix.HeadToOldest
                            .Select(static item => item.Address)
                            .ToArray(),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsAvailability(exception)) {
                return Unavailable("StoreUnavailable", exception.Message);
            }

            if (selection is RecapEpochSelectionResult.Invalid invalid) {
                RepairAttempt repair =
                    await RepairPublishedAsync(
                            invalid.AdmissionAnchor,
                            prefix,
                            rawHead.Value,
                            epochsPublished,
                            maintainerCalls,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (repair is RepairAttempt.Completed completed) {
                    maintainerCalls = checked(
                        maintainerCalls + completed.MaintainerCalls
                    );
                    continue;
                }
                return ((RepairAttempt.Terminal)repair).Result;
            }

            PublishedRecapEpochDescriptor? latest = selection switch {
                RecapEpochSelectionResult.Selected selected =>
                    selected.Descriptor,
                RecapEpochSelectionResult.Empty => null,
                _ => throw new InvalidOperationException(
                    "Unknown shared-epoch selection result."
                )
            };
            if (latest is null && !prefix.IsComplete) {
                return FullRebuild(
                    RecapEpochFullRebuildReason
                        .BoundedRawAuthorityInsufficient,
                    prefix.CapturedHead,
                    "No Published recap or bootstrap boundary was proven inside the bounded online prefix."
                );
            }

            SourceFacts source;
            try {
                source = await ReadSourceFactsAsync(
                        latest,
                        prefix,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (TopologyChangedException exception) {
                return FullRebuild(
                    RecapEpochFullRebuildReason.TopologyChanged,
                    prefix.CapturedHead,
                    exception.Message
                );
            }
            catch (BoundedAuthorityException exception) {
                return FullRebuild(
                    RecapEpochFullRebuildReason
                        .BoundedRawAuthorityInsufficient,
                    prefix.CapturedHead,
                    exception.Message
                );
            }
            catch (Exception exception) when (IsAvailability(exception)) {
                return Unavailable(
                    "PublishedSourceUnavailable",
                    exception.Message
                );
            }

            if (source.StartSeed.Address == prefix.CapturedHead) {
                if (_engine.ReadCurrentHead() != prefix.CapturedHead) {
                    continue;
                }
                return new DerivedRecapEpochOperationResult.Fresh(
                    latest,
                    epochsPublished,
                    maintainerCalls,
                    RecapPlanReasons.BelowCadenceThreshold
                );
            }
            RecapEpochOperationLimits activeLimits = ActiveLimits;
            SessionHistoryPlanningWindowReadResult rawRead =
                _engine.ReadHistoryPlanningWindowAtBounded(
                    prefix.CapturedHead,
                    source.StartSeed,
                    activeLimits.MaxRawGrowthEventCount,
                    cancellationToken
                );
            if (rawRead
                is SessionHistoryPlanningWindowReadResult.BeyondPrefix) {
                return FullRebuild(
                    RecapEpochFullRebuildReason.RawGrowthLimitExceeded,
                    prefix.CapturedHead,
                    $"Raw growth exceeds the bounded online cap {activeLimits.MaxRawGrowthEventCount}."
                );
            }
            SessionHistoryPlanningWindow allRaw =
                ((SessionHistoryPlanningWindowReadResult.Available)
                    rawRead).Window;
            RecapHistoryLoadMeasurement measurement;
            try {
                measurement = RecapHistoryLoadProjector.Measure(
                    allRaw,
                    source.StartSeed.Address,
                    Configuration.HistoryUnitLoadEstimator
                );
            }
            catch (Exception exception) when (IsAvailability(exception)) {
                return Unavailable(
                    "HistoryLoadUnavailable",
                    exception.Message
                );
            }
            RecapEpochPlanningDecision decision;
            try {
                decision = Configuration.Policy.Decide(
                    new RecapEpochPlanningFacts(
                        allRaw,
                        measurement,
                        Configuration.Cadence,
                        activeLimits.MaxRawEventsPerEpoch
                    )
                ) ?? throw new InvalidDataException(
                    "Epoch planning policy returned null."
                );
            }
            catch (Exception exception) when (IsAvailability(exception)) {
                return Unavailable("PolicyUnavailable", exception.Message);
            }
            if (decision is RecapEpochPlanningDecision.NoBuild noBuild) {
                if (_engine.ReadCurrentHead() != prefix.CapturedHead) {
                    continue;
                }
                return new DerivedRecapEpochOperationResult.Fresh(
                    latest,
                    epochsPublished,
                    maintainerCalls,
                    noBuild.Reason
                );
            }

            if (Configuration.OrderedCatalog.Count
                > activeLimits.MaxMaintainerCallsPerOperation) {
                return new DerivedRecapEpochOperationResult
                    .ConfigurationLimit(
                        "A complete epoch roster cannot fit any operation budget."
                    );
            }
            if (epochsPublished >= activeLimits.MaxEpochsPerOperation
                || checked(
                    maintainerCalls
                    + Configuration.OrderedCatalog.Count
                ) > activeLimits.MaxMaintainerCallsPerOperation) {
                if (latest is null) {
                    return new DerivedRecapEpochOperationResult
                        .ConfigurationLimit(
                            "A complete pending epoch roster does not fit the per-operation budget."
                        );
                }
                return new DerivedRecapEpochOperationResult
                    .MoreWorkPending(
                        latest,
                        epochsPublished,
                        maintainerCalls
                    );
            }
            if (Configuration.OrderedCatalog.Count
                > activeLimits.MaxMaintainerCallsPerEpoch) {
                return new DerivedRecapEpochOperationResult
                    .ConfigurationLimit(
                        "Complete roster exceeds MaxMaintainerCallsPerEpoch."
                    );
            }

            EventAddress admission =
                ((RecapEpochPlanningDecision.Build)decision)
                    .AdmissionBoundary;
            if (!allRaw.ReplaySafeBoundarySetups.TryGetValue(
                    admission,
                    out SessionContextAnchorSetupReferences?
                        admissionSetups
                )) {
                return Unavailable(
                    "PolicyInvalid",
                    "Epoch admission is not an exact replay-safe boundary."
                );
            }
            SessionHistoryPlanningWindowReadResult slabRead =
                _engine.ReadHistoryPlanningWindowAtBounded(
                    admission,
                    source.StartSeed,
                    activeLimits.MaxRawEventsPerEpoch,
                    cancellationToken
                );
            if (slabRead
                is not SessionHistoryPlanningWindowReadResult.Available
                    slabAvailable) {
                return Unavailable(
                    "PolicyInvalid",
                    "Selected epoch admission exceeds its exact raw-event cap."
                );
            }
            SessionHistoryPlanningWindow slab = slabAvailable.Window;
            if (slab.EndSetups != admissionSetups
                || slab.RawAddresses.Count == 0
                || string.IsNullOrEmpty(slab.RawRangeSha256)) {
                return Unavailable(
                    "RawPlanningUnavailable",
                    "Materialized epoch slab does not match its selected boundary."
                );
            }

            IReadOnlyList<IHistoryMessage> frozenHistory;
            try {
                frozenHistory = FreezeHistory(slab);
            }
            catch (Exception exception) when (IsAvailability(exception)) {
                return Unavailable(
                    "HistoryProjectionUnavailable",
                    exception.Message
                );
            }
            DerivedRecapEpochInput epochInput =
                DerivedRecapV8Codec.CreateEpochInput(
                    new RecapEpochBoundary(
                        source.StartSeed.Address,
                        source.StartSeed.Setups
                    ),
                    new RecapEpochBoundary(
                        admission,
                        admissionSetups
                    ),
                    slab.RawAddresses.Count,
                    slab.RawRangeSha256,
                    frozenHistory,
                    source.Previous
                );
            DerivedRecapEpochManifest manifest =
                DerivedRecapV8Codec.CreateManifest(
                    _store.RefId,
                    admission,
                    epochInput.PayloadSha256,
                    CreateRoster()
                );
            InstallRecapEpochBuildingResult installed =
                await _store.InstallBuildingAsync(
                        manifest,
                        epochInput,
                        prefix.CapturedHead,
                        _engine.ReadCurrentHead,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (installed
                is InstallRecapEpochBuildingResult.RawHeadChanged) {
                continue;
            }
            if (installed
                is not InstallRecapEpochBuildingResult.Installed
                    buildingInstalled) {
                return Unavailable(
                    "BuildingInstallUnavailable",
                    DescribeInstall(installed)
                );
            }
            RecapEpochStoreReadResult buildingRead =
                await _store.ReadBuildingAsync(
                        admission,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (buildingRead
                is not RecapEpochStoreReadResult.Available
                    buildingAvailable) {
                return Unavailable(
                    "BuildingInvalid",
                    "Installed Building cannot be reopened."
                );
            }
            SerialEpochKernelResult execution =
                await ExecuteSnapshotAsync(
                        buildingAvailable.Snapshot,
                        maintainerCalls,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            maintainerCalls = checked(
                maintainerCalls + execution.StartedCallCount
            );
            if (!execution.Succeeded) {
                return Failed(
                    buildingAvailable.Snapshot,
                    execution.PrimaryFailure!
                );
            }
            PublishRecapEpochResult published =
                await _store.PublishBuildingAsync(
                        buildingInstalled.Descriptor,
                        prefix.CapturedHead,
                        _engine.ReadCurrentHead,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (published
                is PublishRecapEpochResult.RawHeadChanged) {
                return Unavailable(
                    "RawHeadChanged",
                    "Raw head changed after epoch execution; the self-contained Building remains resumable."
                );
            }
            if (published is not (
                    PublishRecapEpochResult.Published
                    or PublishRecapEpochResult.AlreadyPublished
                )) {
                return Unavailable(
                    "PublicationUnavailable",
                    DescribePublish(published)
                );
            }
            epochsPublished = checked(epochsPublished + 1);
        }
    }

    private async ValueTask<RepairAttempt>
        RepairPublishedAsync(
        EventAddress admission,
        SessionCurrentLineagePrefix prefix,
        EventAddress capturedRawHead,
        int epochsPublished,
        int priorCalls,
        CancellationToken cancellationToken
    ) {
        RecapEpochStoreReadResult read =
            await _store.ReadPublishedForRepairAsync(
                    admission,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (read is not RecapEpochStoreReadResult.Available available) {
            return new RepairAttempt.Terminal(Unavailable(
                "RestoreUnavailable",
                read is RecapEpochStoreReadResult.Invalid invalid
                    ? invalid.Detail
                    : "Published repair target disappeared."
            ));
        }
        DerivedRecapEpochOperationResult? validation =
            ValidateSnapshotAuthority(
                available.Snapshot,
                prefix,
                capturedRawHead,
                cancellationToken
            );
        if (validation is not null) {
            return new RepairAttempt.Terminal(validation);
        }
        DerivedRecapEpochOperationResult? budget = CheckBudget(
            available.Snapshot,
            epochsPublished,
            priorCalls
        );
        if (budget is not null) {
            return new RepairAttempt.Terminal(budget);
        }
        SerialEpochKernelResult execution = await ExecuteSnapshotAsync(
                available.Snapshot,
                priorCalls,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!execution.Succeeded) {
            return new RepairAttempt.Terminal(Failed(
                available.Snapshot,
                execution.PrimaryFailure!
            ));
        }
        PublishRecapEpochResult resealed =
            await _store.ResealPublishedAsync(
                    available.Snapshot.PublishedRepairAuthority
                        ?? throw new InvalidDataException(
                            "Published repair snapshot has no authority."
                        ),
                    capturedRawHead,
                    _engine.ReadCurrentHead,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return resealed is PublishRecapEpochResult.Published
            ? new RepairAttempt.Completed(execution.StartedCallCount)
            : new RepairAttempt.Terminal(Unavailable(
                "RestoreUnavailable",
                DescribePublish(resealed)
            ));
    }

    private DerivedRecapEpochOperationResult? ValidateSnapshotAuthority(
        RecapEpochStoreSnapshot snapshot,
        SessionCurrentLineagePrefix prefix,
        EventAddress capturedRawHead,
        CancellationToken cancellationToken
    ) {
        foreach (EventAddress boundary in new[] {
                     snapshot.EpochInput.StartBoundary.Address,
                     snapshot.EpochInput.AdmissionBoundary.Address
                 }) {
            switch (prefix.Lookup(boundary)) {
                case SessionCurrentLineageAnchorLookup.Found:
                    break;
                case SessionCurrentLineageAnchorLookup.BeyondPrefix:
                    return FullRebuild(
                        RecapEpochFullRebuildReason
                            .BoundedRawAuthorityInsufficient,
                        capturedRawHead,
                        "Frozen epoch boundary lies beyond the bounded online prefix."
                    );
                case SessionCurrentLineageAnchorLookup.OffLineage:
                    return Unavailable(
                        "SourceChanged",
                        "Frozen epoch boundary is outside the selected raw lineage."
                    );
            }
        }
        try {
            var setupProofs = new List<SessionGoverningSetupProof>(2);
            foreach (RecapEpochBoundary boundary in new[] {
                         snapshot.EpochInput.StartBoundary,
                         snapshot.EpochInput.AdmissionBoundary
                     }) {
                SessionGoverningSetupProofResult proof =
                    _engine.ProveGoverningSetupInPrefix(
                        prefix,
                        boundary.Address,
                        boundary.Setups
                    );
                if (proof
                    is SessionGoverningSetupProofResult.BeyondPrefix) {
                    return FullRebuild(
                        RecapEpochFullRebuildReason
                            .BoundedRawAuthorityInsufficient,
                        capturedRawHead,
                        "Frozen epoch setup authority lies beyond the bounded online prefix."
                    );
                }
                setupProofs.Add(
                    ((SessionGoverningSetupProofResult.Available)proof)
                        .Proof
                );
            }
            _engine.ValidateGoverningSetupPayloads(
                setupProofs,
                cancellationToken
            );
            SessionHistoryPlanningSeed seed =
                _engine.CreateHistoryPlanningSeed(
                    snapshot.EpochInput.StartBoundary.Address,
                    snapshot.EpochInput.StartBoundary.Setups,
                    cancellationToken
                );
            SessionHistoryPlanningWindowReadResult read =
                _engine.ReadHistoryPlanningWindowAtBounded(
                    snapshot.EpochInput.AdmissionBoundary.Address,
                    seed,
                    _limits.MaxRawEventsPerEpoch,
                    cancellationToken
                );
            if (read
                is not SessionHistoryPlanningWindowReadResult.Available
                    available) {
                return FullRebuild(
                    RecapEpochFullRebuildReason
                        .BoundedRawAuthorityInsufficient,
                    capturedRawHead,
                    "Frozen epoch raw slab exceeds its bounded proof cap."
                );
            }
            SessionHistoryPlanningWindow window = available.Window;
            DerivedRecapEpochInput observed =
                DerivedRecapV8Codec.CreateEpochInput(
                    snapshot.EpochInput.StartBoundary,
                    new RecapEpochBoundary(
                        window.ObservedRawHead,
                        window.EndSetups
                    ),
                    window.RawAddresses.Count,
                    window.RawRangeSha256,
                    FreezeHistory(window),
                    snapshot.EpochInput.Previous
                );
            if (!string.Equals(
                    observed.PayloadSha256,
                    snapshot.EpochInput.PayloadSha256,
                    StringComparison.Ordinal
                )) {
                return Unavailable(
                    "SourceChanged",
                    "Frozen epoch input no longer matches raw authority."
                );
            }
        }
        catch (Exception exception) when (IsAvailability(exception)) {
            return Unavailable("RawPlanningUnavailable", exception.Message);
        }
        return null;
    }

    private async ValueTask<SourceFacts> ReadSourceFactsAsync(
        PublishedRecapEpochDescriptor? latest,
        SessionCurrentLineagePrefix prefix,
        CancellationToken cancellationToken
    ) {
        if (latest is null) {
            SessionCreatedPlanningSeedReadResult read =
                _engine.ReadSessionCreatedPlanningSeedAtBounded(
                    prefix.CapturedHead,
                    _limits.MaxRawGrowthEventCount,
                    cancellationToken
                );
            if (read
                is SessionCreatedPlanningSeedReadResult.BeyondPrefix) {
                throw new BoundedAuthorityException(
                    "SessionCreated bootstrap lies beyond the bounded online prefix."
                );
            }
            return new SourceFacts(
                ((SessionCreatedPlanningSeedReadResult.Available)read)
                    .Seed,
                RecapEpochPrevious.Empty.Instance
            );
        }

        RecapEpochStoreReadResult sourceRead =
            await _store.ReadPublishedForRepairAsync(
                    latest.AdmissionAnchor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (sourceRead
            is not RecapEpochStoreReadResult.Available available
            || available.Snapshot.Publication is not { } publication) {
            throw new InvalidDataException(
                "Latest Published source is unavailable."
            );
        }
        var observedDescriptor = new PublishedRecapEpochDescriptor(
            publication.RefId,
            publication.AdmissionAnchor,
            publication.EnvelopeSha256
        );
        if (observedDescriptor != latest) {
            throw new InvalidDataException(
                "Latest Published descriptor changed during source capture."
            );
        }
        if (!TopologyMatches(available.Snapshot.Manifest)) {
            throw new TopologyChangedException(
                "Latest Published roster differs from the active complete roster."
            );
        }
        SessionGoverningSetupProofResult setupProof =
            _engine.ProveGoverningSetupInPrefix(
                prefix,
                available.Snapshot.EpochInput.AdmissionBoundary.Address,
                available.Snapshot.EpochInput.AdmissionBoundary.Setups
            );
        if (setupProof is SessionGoverningSetupProofResult.BeyondPrefix) {
            throw new BoundedAuthorityException(
                "Latest Published setup authority lies beyond the bounded online prefix."
            );
        }
        _engine.ValidateGoverningSetupPayloads(
            [
                ((SessionGoverningSetupProofResult.Available)setupProof)
                    .Proof
            ],
            cancellationToken
        );
        PriorRecapPackSnapshot prior = await _store.ReadPriorPackAsync(
                latest,
                cancellationToken
            )
            .ConfigureAwait(false);
        return new SourceFacts(
            _engine.CreateHistoryPlanningSeed(
                available.Snapshot.EpochInput.AdmissionBoundary.Address,
                available.Snapshot.EpochInput.AdmissionBoundary.Setups,
                cancellationToken
            ),
            new RecapEpochPrevious.Prior(prior)
        );
    }

    private async ValueTask<SerialEpochKernelResult>
        ExecuteSnapshotAsync(
        RecapEpochStoreSnapshot snapshot,
        int priorCalls,
        CancellationToken cancellationToken
    ) {
        int remainingOperationCalls = checked(
            _limits.MaxMaintainerCallsPerOperation - priorCalls
        );
        return await DerivedRecapSerialEpochKernel.ExecuteAsync(
            snapshot,
            _maintainers,
            _limits.MaxMaintainerCallsPerEpoch,
            remainingOperationCalls,
            (inspection, block, token) => _store.WriteFinalAsync(
                inspection.WriteAuthority
                    ?? throw new InvalidOperationException(
                        "Kernel attempted a final without write authority."
                    ),
                block,
                token
            ),
            cancellationToken
        )
        .ConfigureAwait(false);
    }

    private DerivedRecapEpochOperationResult? CheckBudget(
        RecapEpochStoreSnapshot snapshot,
        int epochsPublished,
        int priorCalls
    ) {
        int pending = PendingCount(snapshot);
        if (pending > _limits.MaxMaintainerCallsPerEpoch
            || pending > _limits.MaxMaintainerCallsPerOperation) {
            return new DerivedRecapEpochOperationResult.ConfigurationLimit(
                "Complete pending roster does not fit the configured call budget."
            );
        }
        if (checked(priorCalls + pending)
            > _limits.MaxMaintainerCallsPerOperation) {
            PublishedRecapEpochDescriptor? latest =
                TryDescriptor(snapshot);
            return latest is null
                ? new DerivedRecapEpochOperationResult.ConfigurationLimit(
                    "Pending Building cannot fit the operation budget."
                )
                : new DerivedRecapEpochOperationResult.MoreWorkPending(
                    latest,
                    epochsPublished,
                    priorCalls
                );
        }
        return null;
    }

    private IReadOnlyList<RecapEpochBlockDefinition> CreateRoster()
        => Array.AsReadOnly([
            .. Configuration.OrderedCatalog.Select((entry, ordinal) =>
                new RecapEpochBlockDefinition(
                    entry.RecapBlockId,
                    entry.Target,
                    entry.MaintainerId,
                    entry.MaintainerCapabilityFingerprint,
                    entry.MaxContentUtf8Bytes,
                    ordinal
                ))
        ]);

    private bool TopologyMatches(DerivedRecapEpochManifest manifest) {
        if (manifest.Blocks.Count
            != Configuration.OrderedCatalog.Count) {
            return false;
        }
        for (int ordinal = 0;
             ordinal < manifest.Blocks.Count;
             ordinal++) {
            RecapEpochBlockDefinition frozen = manifest.Blocks[ordinal];
            RecapBlockCatalogEntry active =
                Configuration.OrderedCatalog[ordinal];
            if (frozen.Ordinal != ordinal
                || frozen.RecapBlockId != active.RecapBlockId
                || frozen.Target != active.Target
                || !string.Equals(
                    frozen.MaintainerId,
                    active.MaintainerId,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    frozen.MaintainerCapabilityFingerprint,
                    active.MaintainerCapabilityFingerprint,
                    StringComparison.Ordinal
                )
                || frozen.MaxContentUtf8Bytes
                    != active.MaxContentUtf8Bytes) {
                return false;
            }
        }
        return true;
    }

    private SessionCurrentLineagePrefix CapturePrefix(
        CancellationToken cancellationToken
    ) => _engine.ReadCurrentLineagePrefix(
        checked(_limits.MaxRawGrowthEventCount + 1),
        cancellationToken
    );

    private RecapEpochPlanningConfiguration Configuration {
        get {
            RecapEpochActiveConfiguration active =
                _activeConfiguration.Value;
            RecapEpochPlanningConfiguration configuration =
                active.Planning;
            if (configuration.OrderedCatalog.Count
                    > active.OperationLimits.MaxRecapBlockCount
                || configuration.OrderedCatalog.Count
                    > _store.Limits.MaxRecapBlockCount
                || active.StoreLimits != _store.Limits) {
                throw new InvalidDataException(
                    "Active complete roster or Store limits do not match this operation."
                );
            }
            return configuration;
        }
    }

    private RecapEpochOperationLimits ActiveLimits =>
        _activeConfiguration.Value.OperationLimits;

    private static IReadOnlyList<IHistoryMessage> FreezeHistory(
        SessionHistoryPlanningWindow window
    ) => Array.AsReadOnly([
        .. window.Units.Select(static unit => FreezeMessage(unit.Message))
    ]);

    private static IHistoryMessage FreezeMessage(IHistoryMessage message)
        => message switch {
            ToolResultsMessage toolResults => new ToolResultsMessage(
                toolResults.Content,
                toolResults.Results
            ),
            ObservationMessage observation
                when observation.GetType()
                    == typeof(ObservationMessage) =>
                new ObservationMessage(observation.Content),
            ActionMessage action => new ActionMessage([
                .. action.Blocks.Where(static block =>
                    block is ActionBlock.Text
                        or ActionBlock.ToolCall)
            ]),
            _ => throw new InvalidDataException(
                $"History projection cannot freeze message type '{message.GetType().FullName}'."
            )
        };

    private static int PendingCount(RecapEpochStoreSnapshot snapshot)
        => snapshot.Blocks.Count(static block =>
            block.Final is not RecapEpochFinalHealth.Healthy);

    private static PublishedRecapEpochDescriptor? TryDescriptor(
        RecapEpochStoreSnapshot snapshot
    ) => snapshot.Publication is { } publication
        ? new PublishedRecapEpochDescriptor(
            publication.RefId,
            publication.AdmissionAnchor,
            publication.EnvelopeSha256
        )
        : null;

    private static DerivedRecapEpochOperationResult Failed(
        RecapEpochStoreSnapshot snapshot,
        SerialEpochFailure failure
    ) => new DerivedRecapEpochOperationResult.BlockFailed(
        snapshot.Manifest.AdmissionAnchor,
        failure.RecapBlockId,
        failure.Code,
        failure.Detail
    );

    private static DerivedRecapEpochOperationResult.FullRebuildRequired
        FullRebuild(
        RecapEpochFullRebuildReason reason,
        EventAddress head,
        string detail
    ) => new(reason, head, detail);

    private static DerivedRecapEpochOperationResult.Unavailable Unavailable(
        string code,
        string detail
    ) => new(code, detail);

    private static string DescribeInstall(
        InstallRecapEpochBuildingResult result
    ) => result switch {
        InstallRecapEpochBuildingResult.PreviousChanged changed =>
            changed.Detail,
        InstallRecapEpochBuildingResult.RawHeadChanged =>
            "Raw head changed.",
        InstallRecapEpochBuildingResult.Conflict conflict =>
            $"Conflicting Building exists at {conflict.AdmissionAnchor}.",
        InstallRecapEpochBuildingResult.Invalid invalid => invalid.Detail,
        _ => result.GetType().Name
    };

    private static string DescribePublish(PublishRecapEpochResult result)
        => result switch {
            PublishRecapEpochResult.NotPublishable invalid =>
                invalid.Detail,
            PublishRecapEpochResult.Stale stale => stale.Detail,
            PublishRecapEpochResult.RawHeadChanged =>
                "Raw head changed.",
            _ => result.GetType().Name
        };

    private static bool IsAvailability(Exception exception)
        => exception is InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or IOException
            or UnauthorizedAccessException
            or OverflowException
            or EncoderFallbackException
            or HistoryLoadMeasurementException;

    private static void RequireSameBinding(
        SessionJournalReadView engine,
        DerivedRecapEpochStore store
    ) {
        string enginePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(engine.Path)
        );
        string storePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(store.SessionRepositoryPath)
        );
        if (!string.Equals(
                enginePath,
                storePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
            || engine.BranchRefId != store.RefId) {
            throw new ArgumentException(
                "Shared-epoch executor, Store, and raw read view must bind the same repository and RefId."
            );
        }
    }

    private sealed record SourceFacts(
        SessionHistoryPlanningSeed StartSeed,
        RecapEpochPrevious Previous
    );

    private abstract record RepairAttempt {
        private RepairAttempt() {
        }

        internal sealed record Completed(int MaintainerCalls)
            : RepairAttempt;

        internal sealed record Terminal(
            DerivedRecapEpochOperationResult Result
        ) : RepairAttempt;
    }

    private sealed class TopologyChangedException(string message)
        : Exception(message);

    private sealed class BoundedAuthorityException(string message)
        : Exception(message);
}
