using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed partial class DerivedRecapEpochCampaignExecutor {
    /// <summary>
    /// Explicit destructive entry. The sealed raw authority is reopened and
    /// checked before the v8 recap Store is reset; the independent spool is
    /// retained so a crash after reset can resume with
    /// <see cref="RunExplicitRebuildAsync"/>.
    /// </summary>
    public async ValueTask<DerivedRecapEpochOperationResult>
        ResetAndRunExplicitRebuildAsync(
        SessionJournalEngine offlineEngine,
        DerivedRecapRebuildSpoolStore spool,
        string campaignId,
        CancellationToken cancellationToken = default
    ) {
        RequireExplicitBinding(offlineEngine, spool);
        using (SessionSelectedLineageForwardCursor validation =
               await DerivedRecapFullRebuildAuthorityPreparer
                   .OpenForwardCursorAsync(
                       offlineEngine,
                       spool,
                       campaignId,
                       cancellationToken
                   )
                   .ConfigureAwait(false)) {
            _ = validation.Authority;
        }
        await _store.ResetAsync(cancellationToken).ConfigureAwait(false);
        return await RunExplicitRebuildAsync(
                offlineEngine,
                spool,
                campaignId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs or resumes one bounded serial operation over a sealed, exact-head
    /// selected-lineage authority. Cadence decisions remain runtime-only;
    /// only normal v8 Building/Published artifacts persist progress.
    /// </summary>
    public async ValueTask<DerivedRecapEpochOperationResult>
        RunExplicitRebuildAsync(
        SessionJournalEngine offlineEngine,
        DerivedRecapRebuildSpoolStore spool,
        string campaignId,
        CancellationToken cancellationToken = default
    ) {
        RequireExplicitBinding(offlineEngine, spool);
        int epochsPublished = 0;
        int maintainerCalls = 0;
        Func<EventAddress?> readCurrentRawHead =
            offlineEngine.ReadView.ReadCurrentHead;
        RecapEpochBuildingSelectionResult buildingSelection;
        RecapEpochStoreSnapshot? publishedSource = null;
        try {
            buildingSelection = await _store.SelectBuildingAsync(
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (buildingSelection
                is RecapEpochBuildingSelectionResult.Empty) {
                publishedSource = await ReadLatestPublishedMemberAsync(
                        offlineEngine,
                        spool,
                        campaignId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsAvailability(exception)) {
            return Unavailable("StoreUnavailable", exception.Message);
        }
        if (buildingSelection
            is RecapEpochBuildingSelectionResult.Invalid invalidBuilding) {
            return Unavailable("BuildingInvalid", invalidBuilding.Detail);
        }
        SessionSelectedLineageForwardCursor cursor;
        try {
            cursor = await DerivedRecapFullRebuildAuthorityPreparer
                .OpenForwardCursorAsync(
                    offlineEngine,
                    spool,
                    campaignId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAvailability(exception)) {
            return Unavailable("RawAuthorityUnavailable", exception.Message);
        }
        using (cursor) {
            EventAddress capturedHead =
                cursor.Authority.Capture.CapturedHead;
            PublishedRecapEpochDescriptor? latest = null;
            DerivedRecapEpochManifest? latestManifest = null;
            RecapEpochPrevious previous = RecapEpochPrevious.Empty.Instance;
            SessionSelectedLineageForwardRange? pendingRange = null;
            if (buildingSelection
                is RecapEpochBuildingSelectionResult.Selected building) {
                DerivedRecapEpochOperationResult? budget = CheckBudget(
                    building.Snapshot,
                    epochsPublished,
                    maintainerCalls
                );
                if (budget is not null) {
                    return budget;
                }
                try {
                    pendingRange = ConsumeFrozenSnapshot(
                        cursor,
                        building.Snapshot,
                        cancellationToken
                    );
                }
                catch (Exception exception) when (IsAvailability(exception)) {
                    return Unavailable(
                        "RawAuthorityUnavailable",
                        exception.Message
                    );
                }
                SerialEpochKernelResult resumed =
                    await ExecuteSnapshotAsync(
                            building.Snapshot,
                            maintainerCalls,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                maintainerCalls = checked(
                    maintainerCalls + resumed.StartedCallCount
                );
                if (!resumed.Succeeded) {
                    return Failed(
                        building.Snapshot,
                        resumed.PrimaryFailure!
                    );
                }
                PublishRecapEpochResult published =
                    await _store.PublishBuildingAsync(
                            building.Snapshot.Descriptor,
                            capturedHead,
                            readCurrentRawHead,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                latest = DescriptorOrNull(published);
                if (latest is null) {
                    return Unavailable(
                        "PublicationUnavailable",
                        DescribePublish(published)
                    );
                }
                epochsPublished = checked(epochsPublished + 1);
                latestManifest = building.Snapshot.Manifest;
                try {
                    previous = new RecapEpochPrevious.Prior(
                        await _store.ReadPriorPackAsync(
                                latest,
                                cancellationToken
                            )
                            .ConfigureAwait(false)
                    );
                }
                catch (Exception exception) when (IsAvailability(exception)) {
                    return Unavailable(
                        "PublishedSourceUnavailable",
                        exception.Message
                    );
                }
            }
            else {
                RecapEpochStoreSnapshot? source = publishedSource;
                if (source is not null) {
                    bool healthy = source.Publication is not null
                        && source.Blocks.All(static block =>
                            block.Final is RecapEpochFinalHealth.Healthy);
                    if (healthy) {
                        latest = new PublishedRecapEpochDescriptor(
                            source.Publication!.RefId,
                            source.Publication.AdmissionAnchor,
                            source.Publication.EnvelopeSha256
                        );
                        try {
                            pendingRange = ConsumeFrozenSnapshot(
                                cursor,
                                source,
                                cancellationToken
                            );
                            previous = new RecapEpochPrevious.Prior(
                                await _store.ReadPriorPackAsync(
                                        latest,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
                            );
                        }
                        catch (Exception exception)
                            when (IsAvailability(exception)) {
                            return Unavailable(
                                "PublishedSourceUnavailable",
                                exception.Message
                            );
                        }
                    }
                    else {
                        DerivedRecapEpochOperationResult? budget = CheckBudget(
                            source,
                            epochsPublished,
                            maintainerCalls
                        );
                        if (budget is not null) {
                            return budget;
                        }
                        try {
                            pendingRange = ConsumeFrozenSnapshot(
                                cursor,
                                source,
                                cancellationToken
                            );
                        }
                        catch (Exception exception)
                            when (IsAvailability(exception)) {
                            return Unavailable(
                                "RawAuthorityUnavailable",
                                exception.Message
                            );
                        }
                        SerialEpochKernelResult repaired =
                            await ExecuteSnapshotAsync(
                                    source,
                                    maintainerCalls,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                        maintainerCalls = checked(
                            maintainerCalls + repaired.StartedCallCount
                        );
                        if (!repaired.Succeeded) {
                            return Failed(source, repaired.PrimaryFailure!);
                        }
                        PublishRecapEpochResult resealed =
                            await _store.ResealPublishedAsync(
                                    source.PublishedRepairAuthority
                                        ?? throw new InvalidDataException(
                                            "Published repair has no Store authority."
                                        ),
                                    capturedHead,
                                    readCurrentRawHead,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                        latest = DescriptorOrNull(resealed);
                        if (latest is null) {
                            return Unavailable(
                                "RestoreUnavailable",
                                DescribePublish(resealed)
                            );
                        }
                        try {
                            previous = new RecapEpochPrevious.Prior(
                                await _store.ReadPriorPackAsync(
                                        latest,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
                            );
                        }
                        catch (Exception exception)
                            when (IsAvailability(exception)) {
                            return Unavailable(
                                "PublishedSourceUnavailable",
                                exception.Message
                            );
                        }
                    }
                    latestManifest = source.Manifest;
                }
            }

            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                RecapEpochOperationLimits activeLimits;
                try {
                    activeLimits = ActiveLimits;
                    if (latestManifest is not null
                        && !TopologyMatches(latestManifest)) {
                        return FullRebuild(
                            RecapEpochFullRebuildReason.TopologyChanged,
                            capturedHead,
                            "Latest frozen roster differs from the active complete roster."
                        );
                    }
                }
                catch (Exception exception) when (IsAvailability(exception)) {
                    return Unavailable(
                        "RebuildPlanningUnavailable",
                        exception.Message
                    );
                }
                SessionSelectedLineageForwardRange? range = pendingRange
                    ?? cursor.ReadNextRange(
                        activeLimits.MaxRebuildForwardRangeEventCount,
                        cancellationToken
                    );
                pendingRange = null;
                if (range is null) {
                    if (readCurrentRawHead() != capturedHead) {
                        return Unavailable(
                            "RawHeadChanged",
                            "Raw head changed before explicit rebuild freshness was established."
                        );
                    }
                    return new DerivedRecapEpochOperationResult.Fresh(
                        latest,
                        epochsPublished,
                        maintainerCalls,
                        RecapPlanReasons.BelowCadenceThreshold
                    );
                }

                SessionHistoryPlanningWindow preview;
                HistoryLoadProjection measurement;
                RecapEpochPlanningDecision decision;
                try {
                    preview = cursor.Preview(range, cancellationToken);
                    measurement = HistoryLoadProjector.Measure(
                        preview,
                        range.StartExclusive,
                        Configuration.HistoryUnitLoadEstimator
                    );
                    decision = Configuration.Policy.Decide(
                        new RecapEpochPlanningFacts(
                            preview,
                            measurement,
                            Configuration.Cadence,
                            activeLimits.MaxRawEventsPerEpoch
                        )
                    ) ?? throw new InvalidDataException(
                        "Epoch planning policy returned null."
                    );
                }
                catch (Exception exception) when (IsAvailability(exception)) {
                    return Unavailable(
                        "RebuildPlanningUnavailable",
                        exception.Message
                    );
                }
                if (decision is RecapEpochPlanningDecision.NoBuild noBuild) {
                    if (!range.IsFinal) {
                        return new DerivedRecapEpochOperationResult
                            .ConfigurationLimit(
                                "No replay-safe epoch admission was found inside one bounded rebuild range."
                            );
                    }
                    if (readCurrentRawHead() != capturedHead) {
                        return Unavailable(
                            "RawHeadChanged",
                            "Raw head changed before explicit rebuild freshness was established."
                        );
                    }
                    return new DerivedRecapEpochOperationResult.Fresh(
                        latest,
                        epochsPublished,
                        maintainerCalls,
                        noBuild.Reason
                    );
                }

                DerivedRecapEpochOperationResult? operationBudget =
                    CheckNewEpochOperationBudget(
                        latest,
                        epochsPublished,
                        maintainerCalls
                    );
                if (operationBudget is not null) {
                    return operationBudget;
                }
                EventAddress admission =
                    ((RecapEpochPlanningDecision.Build)decision)
                        .AdmissionBoundary;
                SessionSelectedLineageForwardConsumption consumed;
                try {
                    consumed = cursor.ConsumePreviewedPrefix(
                        range,
                        admission,
                        cancellationToken
                    );
                }
                catch (Exception exception) when (IsAvailability(exception)) {
                    return Unavailable(
                        "RebuildPlanningUnavailable",
                        exception.Message
                    );
                }
                SessionHistoryPlanningWindow slab = consumed.Window;
                if (slab.RawAddresses.Count
                        > activeLimits.MaxRawEventsPerEpoch
                    || slab.RawAddresses.Count == 0
                    || string.IsNullOrEmpty(slab.RawRangeSha256)) {
                    return Unavailable(
                        "PolicyInvalid",
                        "Selected explicit epoch exceeds its exact raw-event cap."
                    );
                }
                DerivedRecapEpochInput input =
                    DerivedRecapV8Codec.CreateEpochInput(
                        new RecapEpochBoundary(
                            slab.StartExclusive,
                            slab.StartSetups
                        ),
                        new RecapEpochBoundary(
                            slab.ObservedRawHead,
                            slab.EndSetups
                        ),
                        slab.RawAddresses.Count,
                        slab.RawRangeSha256,
                        FreezeHistory(slab),
                        previous
                    );
                DerivedRecapEpochManifest manifest =
                    DerivedRecapV8Codec.CreateManifest(
                        _store.RefId,
                        admission,
                        input.PayloadSha256,
                        CreateRoster()
                    );
                InstallRecapEpochBuildingResult installed =
                    await _store.InstallBuildingAsync(
                            manifest,
                            input,
                            capturedHead,
                            readCurrentRawHead,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (installed
                    is not InstallRecapEpochBuildingResult.Installed
                        installedBuilding) {
                    return Unavailable(
                        "BuildingInstallUnavailable",
                        DescribeInstall(installed)
                    );
                }
                RecapEpochStoreSnapshot snapshot = AssertAvailableBuilding(
                    await _store.ReadBuildingAsync(
                            admission,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                );
                SerialEpochKernelResult execution =
                    await ExecuteSnapshotAsync(
                            snapshot,
                            maintainerCalls,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                maintainerCalls = checked(
                    maintainerCalls + execution.StartedCallCount
                );
                if (!execution.Succeeded) {
                    return Failed(snapshot, execution.PrimaryFailure!);
                }
                PublishRecapEpochResult publish =
                    await _store.PublishBuildingAsync(
                            installedBuilding.Descriptor,
                            capturedHead,
                            readCurrentRawHead,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                latest = DescriptorOrNull(publish);
                if (latest is null) {
                    return Unavailable(
                        "PublicationUnavailable",
                        DescribePublish(publish)
                    );
                }
                epochsPublished = checked(epochsPublished + 1);
                latestManifest = manifest;
                try {
                    previous = new RecapEpochPrevious.Prior(
                        await _store.ReadPriorPackAsync(
                                latest,
                                cancellationToken
                            )
                            .ConfigureAwait(false)
                    );
                }
                catch (Exception exception) when (IsAvailability(exception)) {
                    return Unavailable(
                        "PublishedSourceUnavailable",
                        exception.Message
                    );
                }
                pendingRange = consumed.RemainingRange;
            }
        }
    }

    private SessionSelectedLineageForwardRange? ConsumeFrozenSnapshot(
        SessionSelectedLineageForwardCursor cursor,
        RecapEpochStoreSnapshot snapshot,
        CancellationToken cancellationToken
    ) {
        cursor.SeekToBoundary(
            snapshot.EpochInput.StartBoundary.Address,
            snapshot.EpochInput.StartBoundary.Setups,
            cancellationToken
        );
        SessionSelectedLineageForwardRange range = cursor.ReadNextRange(
                snapshot.EpochInput.RawEventCount,
                cancellationToken
            )
            ?? throw new InvalidDataException(
                "Frozen epoch admission is beyond the audited raw head."
            );
        _ = cursor.Preview(range, cancellationToken);
        SessionSelectedLineageForwardConsumption consumed =
            cursor.ConsumePreviewedPrefix(
                range,
                snapshot.EpochInput.AdmissionBoundary.Address,
                cancellationToken
            );
        SessionHistoryPlanningWindow window = consumed.Window;
        if (window.EndSetups
            != snapshot.EpochInput.AdmissionBoundary.Setups) {
            throw new InvalidDataException(
                "Frozen epoch admission setup is not governing on the audited lineage."
            );
        }
        DerivedRecapEpochInput observed = DerivedRecapV8Codec
            .CreateEpochInput(
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
            throw new InvalidDataException(
                "Frozen epoch input differs from authoritative raw history."
            );
        }
        return consumed.RemainingRange;
    }

    private async ValueTask<RecapEpochStoreSnapshot?>
        ReadLatestPublishedMemberAsync(
        SessionJournalEngine offlineEngine,
        DerivedRecapRebuildSpoolStore spool,
        string campaignId,
        CancellationToken cancellationToken
    ) {
        IReadOnlyList<EventAddress> anchors =
            await _store.ListPublishedAnchorsAsync(cancellationToken)
                .ConfigureAwait(false);
        if (anchors.Count == 0) {
            return null;
        }
        using SessionSelectedLineageForwardCursor membership =
            await DerivedRecapFullRebuildAuthorityPreparer
                .OpenForwardCursorAsync(
                    offlineEngine,
                    spool,
                    campaignId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        EventAddress? latestAnchor = membership
            .FindLatestMatchingBoundary(
                anchors.ToHashSet(),
                cancellationToken
            );
        if (latestAnchor is null) {
            throw new InvalidDataException(
                "Published rebuild inventory has no member on the captured selected lineage."
            );
        }
        RecapEpochStoreReadResult latest =
            await _store.ReadPublishedForRepairAsync(
                    latestAnchor.Value,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return latest is RecapEpochStoreReadResult.Available available
            ? available.Snapshot
            : throw new InvalidDataException(
                "Latest selected Published epoch is unavailable."
            );
    }

    private DerivedRecapEpochOperationResult? CheckNewEpochOperationBudget(
        PublishedRecapEpochDescriptor? latest,
        int epochsPublished,
        int maintainerCalls
    ) {
        int rosterCount = Configuration.OrderedCatalog.Count;
        RecapEpochOperationLimits activeLimits = ActiveLimits;
        if (rosterCount > activeLimits.MaxMaintainerCallsPerEpoch
            || rosterCount > activeLimits.MaxMaintainerCallsPerOperation) {
            return new DerivedRecapEpochOperationResult.ConfigurationLimit(
                "A complete epoch roster cannot fit the configured call budget."
            );
        }
        if (epochsPublished >= activeLimits.MaxEpochsPerOperation
            || checked(maintainerCalls + rosterCount)
                > activeLimits.MaxMaintainerCallsPerOperation) {
            return latest is null
                ? new DerivedRecapEpochOperationResult.ConfigurationLimit(
                    "The first explicit epoch cannot fit this operation budget."
                )
                : new DerivedRecapEpochOperationResult.MoreWorkPending(
                    latest,
                    epochsPublished,
                    maintainerCalls
                );
        }
        return null;
    }

    private static RecapEpochStoreSnapshot AssertAvailableBuilding(
        RecapEpochStoreReadResult read
    ) => read is RecapEpochStoreReadResult.Available available
        ? available.Snapshot
        : throw new InvalidDataException(
            "Installed explicit-rebuild Building cannot be reopened."
        );

    private static PublishedRecapEpochDescriptor? DescriptorOrNull(
        PublishRecapEpochResult result
    ) => result switch {
        PublishRecapEpochResult.Published published =>
            published.Descriptor,
        PublishRecapEpochResult.AlreadyPublished published =>
            published.Descriptor,
        _ => null
    };

    private void RequireExplicitBinding(
        SessionJournalEngine offlineEngine,
        DerivedRecapRebuildSpoolStore spool
    ) {
        ArgumentNullException.ThrowIfNull(offlineEngine);
        ArgumentNullException.ThrowIfNull(spool);
        if (!offlineEngine.IsReadOnly) {
            throw new ArgumentException(
                "Explicit rebuild requires a read-only SessionJournalEngine.",
                nameof(offlineEngine)
            );
        }
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string rawPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(offlineEngine.Path)
        );
        if (offlineEngine.BranchRefId != _store.RefId
            || offlineEngine.BranchRefId != spool.RefId
            || !string.Equals(
                rawPath,
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(_store.SessionRepositoryPath)
                ),
                comparison
            )
            || !string.Equals(
                rawPath,
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(spool.SessionRepositoryPath)
                ),
                comparison
            )) {
            throw new ArgumentException(
                "Explicit rebuild engine, spool, and recap Store must bind the same repository and RefId."
            );
        }
    }
}
