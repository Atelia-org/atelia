using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Engine-bound planning, durable execution recovery, and publication for one
/// raw SessionJournal branch and its DerivedRecap Store.
/// </summary>
public sealed class DerivedRecapPlannerExecutor {
    private readonly SessionJournalEngine _engine;
    private readonly DerivedRecapStore _store;
    private readonly RecapPlannerConfig _config;
    private readonly IRecapPlanningPolicy _policy;
    private readonly IRecapBlockMaintainerRegistry _maintainers;
    private readonly DerivedRecapBuildingInstaller _installer;
    private readonly DerivedRecapPublisher _publisher;

    public DerivedRecapPlannerExecutor(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        RecapPlannerConfig config,
        IRecapPlanningPolicy policy,
        IRecapBlockMaintainerRegistry maintainers
    ) {
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        _store = store
            ?? throw new ArgumentNullException(nameof(store));
        _config = config
            ?? throw new ArgumentNullException(nameof(config));
        _policy = policy
            ?? throw new ArgumentNullException(nameof(policy));
        _maintainers = maintainers
            ?? throw new ArgumentNullException(nameof(maintainers));
        RequireSameBinding(store, engine);
        foreach (RecapBlockCatalogEntry entry in config.Catalog) {
            if (!maintainers.TryResolve(
                    entry.MaintainerId,
                    entry.Target,
                    out IRecapBlockMaintainer? maintainer
                )
                || !string.Equals(
                    maintainer.Id,
                    entry.MaintainerId,
                    StringComparison.Ordinal
                )
                || maintainer.Target != entry.Target) {
                throw new ArgumentException(
                    "Maintainer registry cannot resolve the exact "
                    + $"catalog binding for '{entry.RecapBlockId}'.",
                    nameof(maintainers)
                );
            }
        }
        _installer = new DerivedRecapBuildingInstaller(store, engine);
        _publisher = new DerivedRecapPublisher(store, engine);
    }

    public async ValueTask<DerivedRecapExecutionResult> RunAsync(
        CancellationToken cancellationToken = default
    ) {
        SessionCurrentLineageSnapshot lineage;
        DerivedRecapSelection selection;
        try {
            lineage = _engine.ReadCurrentLineageHeaders(
                cancellationToken
            );
            selection = await _store.SelectNthPreviousAsync(
                    lineage,
                    nthPrevious: 0,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.StoreUnavailable,
                exception.Message
            );
        }

        PublishedRecapDescriptor? latest;
        switch (selection) {
            case DerivedRecapSelection.Selected selected:
                latest = selected.Descriptor;
                break;
            case DerivedRecapSelection.EmptyLineage:
                latest = null;
                break;
            case DerivedRecapSelection.ExactPublishedSetInvalid
                selectedInvalid:
                return Unavailable(selectedInvalid.Defects);
            case DerivedRecapSelection.StoreUnavailable unavailable:
                return Unavailable(
                    DerivedRecapExecutionDefectCodes.StoreUnavailable,
                    unavailable.Reason
                );
            case DerivedRecapSelection.OrdinalUnavailable:
                return Unavailable(
                    DerivedRecapExecutionDefectCodes.StoreUnavailable,
                    "Latest strict Published ordinal is unavailable."
                );
            default:
                throw new InvalidOperationException(
                    "Unknown DerivedRecap selection result."
                );
        }

        // This first gate is deliberately header-only. A below-trigger
        // decision performs no source snapshot read, policy call, raw payload
        // replay, or Maintainer call.
        RecapSchedulingResult initialSchedule =
            RecapPlanEvaluator.EvaluateSchedule(
                _config,
                new RecapSchedulingFacts(
                    lineage.CapturedHead,
                    lineage.HeadToRoot,
                    [],
                    latest?.SetAdmissionAnchor
                )
            );
        switch (initialSchedule) {
            case RecapSchedulingResult.NoBuild noBuild:
                return new DerivedRecapExecutionResult.NoBuild(
                    noBuild.Reason
                );
            case RecapSchedulingResult.Unavailable unavailable:
                return Unavailable(unavailable.Defects);
        }

        PublishedRecapSourceSnapshot? sourceSnapshot = null;
        RecapPolicyFacts policyFacts;
        if (latest is null) {
            policyFacts = new RecapPolicyFacts([]);
        }
        else {
            PublishedRecapSourceReadResult sourceRead;
            try {
                sourceRead = await _store.ReadPublishedSourceAsync(
                        latest,
                        _config.Catalog
                            .Select(static item => item.RecapBlockId)
                            .ToArray(),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (IsAvailabilityException(exception)) {
                return Unavailable(
                    DerivedRecapExecutionDefectCodes
                        .PublishedSourceUnavailable,
                    exception.Message
                );
            }

            if (sourceRead
                is not PublishedRecapSourceReadResult.Available available) {
                return SourceReadUnavailable(sourceRead);
            }
            sourceSnapshot = available.Snapshot;
            var inputById = sourceSnapshot.FrozenInputs.ToDictionary(
                static input => input.RecapBlockId
            );
            foreach (RecapBlockCatalogEntry entry in _config.Catalog) {
                if (!inputById.TryGetValue(
                        entry.RecapBlockId,
                        out DerivedRecapFrozenInput? input
                    )
                    || input.Target != entry.Target) {
                    return Unavailable(
                        DerivedRecapExecutionDefectCodes
                            .PublishedSourceUnavailable,
                        $"Published source block '{entry.RecapBlockId}' "
                        + "does not match the active catalog target."
                    );
                }
            }
            var sourceIntent = new RecapSourceIntent(
                sourceSnapshot.Source.SetAdmissionAnchor,
                sourceSnapshot.Source.EnvelopeSha256
            );
            policyFacts = new RecapPolicyFacts([
                .. _config.Catalog.Select(entry =>
                    new RecapBlockSourceIntent(
                        entry.RecapBlockId,
                        sourceIntent
                    )
                )
            ]);
        }

        RecapSchedulingResult.Ready schedule;
        try {
            EventAddress? earliestCursor =
                FindEarliestSourceCursor(lineage, sourceSnapshot);
            SessionHistoryPlanningWindow allRelevantRaw =
                _engine.ReadHistoryPlanningWindowAt(
                    lineage.CapturedHead,
                    earliestCursor,
                    cancellationToken
                );
            EventAddress[] boundaries = [
                allRelevantRaw.StartExclusive,
                .. allRelevantRaw.ReplaySafeBoundaries.Select(
                    static item => item.Address
                )
            ];
            RecapSchedulingResult exactSchedule =
                RecapPlanEvaluator.EvaluateSchedule(
                    _config,
                    new RecapSchedulingFacts(
                        lineage.CapturedHead,
                        lineage.HeadToRoot,
                        boundaries.Distinct().ToArray(),
                        latest?.SetAdmissionAnchor
                    )
                );
            if (exactSchedule
                is not RecapSchedulingResult.Ready ready) {
                return exactSchedule switch {
                    RecapSchedulingResult.NoBuild noBuild =>
                        new DerivedRecapExecutionResult.NoBuild(
                            noBuild.Reason
                        ),
                    RecapSchedulingResult.Unavailable unavailable =>
                        Unavailable(unavailable.Defects),
                    _ => throw new InvalidOperationException(
                        "Unknown exact scheduling result."
                    )
                };
            }
            schedule = ready;
        }
        catch (Exception exception) when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.RawPlanningUnavailable,
                exception.Message
            );
        }

        RecapPlanIntentResult intentResult =
            RecapPlanEvaluator.EvaluateIntent(
                schedule,
                policyFacts,
                _policy
            );
        switch (intentResult) {
            case RecapPlanIntentResult.NoBuild noBuild:
                return new DerivedRecapExecutionResult.NoBuild(
                    noBuild.Reason
                );
            case RecapPlanIntentResult.Unavailable unavailable:
                return Unavailable(unavailable.Defects);
        }
        var intentReady =
            (RecapPlanIntentResult.IntentReady)intentResult;

        if (_engine.ReadCurrentHead()
            != intentReady.Schedule.Facts.CapturedHead) {
            return RetryableRawHead(
                intentReady.Schedule.Facts.CapturedHead
            );
        }

        PreparedIntent prepared;
        try {
            prepared = PrepareIntent(
                intentReady,
                sourceSnapshot,
                cancellationToken
            );
        }
        catch (Exception exception) when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.RawPlanningUnavailable,
                exception.Message
            );
        }
        if (prepared.PlanResult
            is RecapPlanResult.Unavailable planUnavailable) {
            return Unavailable(planUnavailable.Defects);
        }

        if (_engine.ReadCurrentHead() != lineage.CapturedHead) {
            return RetryableRawHead(lineage.CapturedHead);
        }

        EventAddress admission =
            intentReady.Intent.SetAdmissionAnchor;
        BuildingReadResult existing;
        try {
            existing = await _store.ReadBuildingAsync(
                    admission,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.StoreUnavailable,
                exception.Message
            );
        }
        if (existing is BuildingReadResult.Invalid invalid) {
            return Unavailable(invalid.Defects);
        }
        if (existing is BuildingReadResult.Available alreadyBuilding) {
            return await ExecuteAndPublishAsync(
                    alreadyBuilding.Snapshot,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        DerivedRecapSetManifest manifest = CreateManifest(
            intentReady.Intent,
            sourceSnapshot
        );
        CreateBuildingResult created;
        try {
            created = await _installer.InstallAsync(
                    manifest,
                    lineage.CapturedHead,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (IOException exception) {
            return new DerivedRecapExecutionResult.Retryable(
                DerivedRecapExecutionDefectCodes.BuildingRace,
                exception.Message
            );
        }
        catch (Exception exception) when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.StoreUnavailable,
                exception.Message
            );
        }
        switch (created) {
            case CreateBuildingResult.SourceChanged changed:
                return new DerivedRecapExecutionResult.Retryable(
                    DerivedRecapExecutionDefectCodes.SourceChanged,
                    $"Published source '{changed.Source.SetAdmissionAnchor}' "
                    + "changed before Building creation."
                );
            case CreateBuildingResult.SourceUnavailable unavailable:
                return Unavailable(unavailable.Defects);
            case CreateBuildingResult.RawHeadChanged changed:
                return new DerivedRecapExecutionResult.Retryable(
                    DerivedRecapExecutionDefectCodes.RawHeadChanged,
                    $"Raw SessionJournal head changed before Building "
                    + $"installation. Expected '{changed.Expected}', "
                    + $"observed '{changed.Observed}'."
                );
        }

        // Do not execute from in-memory intent/source objects. The installed
        // Building is now the only recovery authority.
        return await ResumeAsync(admission, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<DerivedRecapExecutionResult> ResumeAsync(
        EventAddress setAdmissionAnchor,
        CancellationToken cancellationToken = default
    ) {
        BuildingReadResult read;
        try {
            read = await _store.ReadBuildingAsync(
                    setAdmissionAnchor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.StoreUnavailable,
                exception.Message
            );
        }
        return read switch {
            BuildingReadResult.Available available =>
                await ExecuteAndPublishAsync(
                        available.Snapshot,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
            BuildingReadResult.Invalid invalid =>
                Unavailable(invalid.Defects),
            BuildingReadResult.Missing => Unavailable(
                DerivedRecapExecutionDefectCodes.BuildingInvalid,
                $"Building '{setAdmissionAnchor}' does not exist."
            ),
            _ => throw new InvalidOperationException(
                "Unknown Building read result."
            )
        };
    }

    private PreparedIntent PrepareIntent(
        RecapPlanIntentResult.IntentReady ready,
        PublishedRecapSourceSnapshot? sourceSnapshot,
        CancellationToken cancellationToken
    ) {
        var sourceFacts = new List<RecapSourceReplayFact>();
        Dictionary<RecapBlockId, DerivedRecapFrozenInput> inputs =
            sourceSnapshot?.FrozenInputs.ToDictionary(
                static input => input.RecapBlockId
            ) ?? [];
        var starts = new List<EventAddress>();
        foreach (RecapBlockPlanningDecision decision
                 in ready.Intent.Blocks) {
            switch (decision) {
                case RecapBlockPlanningDecision.Inherit inherit:
                    sourceFacts.Add(SourceFact(
                        inherit.RecapBlockId,
                        inherit.Source
                    ));
                    break;
                case RecapBlockPlanningDecision.Maintain {
                    Source: RecapPlanningMaintainSource.Existing existing
                } maintain:
                    RecapSourceReplayFact fact = SourceFact(
                        maintain.RecapBlockId,
                        existing.Source
                    );
                    sourceFacts.Add(fact);
                    AddStepStarts(
                        fact.AbsorbedThrough,
                        maintain.CatchUpThrough,
                        starts
                    );
                    break;
                case RecapBlockPlanningDecision.Maintain {
                    Source: RecapPlanningMaintainSource.Empty empty
                } maintain:
                    AddStepStarts(
                        empty.ReplayStartExclusive,
                        maintain.CatchUpThrough,
                        starts
                    );
                    break;
            }
        }

        SessionHistoryPlanningSeedBatch seedBatch =
            _engine.ReadHistoryPlanningSeeds(
                starts.Distinct(),
                cancellationToken
            );
        if (seedBatch.Lineage.CapturedHead
            != ready.Schedule.Facts.CapturedHead) {
            throw new InvalidDataException(
                "Raw head changed while planning exact replay seeds."
            );
        }
        Dictionary<EventAddress, SessionHistoryPlanningSeed> seeds =
            seedBatch.Seeds.ToDictionary(static seed => seed.Address);
        var costs = new List<RecapPlannedStepCost>();
        foreach (RecapBlockPlanningDecision.Maintain maintain
                 in ready.Intent.Blocks
                     .OfType<RecapBlockPlanningDecision.Maintain>()) {
            EventAddress previous = maintain.Source switch {
                RecapPlanningMaintainSource.Existing existing =>
                    inputs[maintain.RecapBlockId].AbsorbedThrough,
                RecapPlanningMaintainSource.Empty empty =>
                    empty.ReplayStartExclusive,
                _ => throw new InvalidDataException(
                    "Unsupported Maintain source intent."
                )
            };
            foreach (EventAddress endpoint in maintain.CatchUpThrough) {
                SessionHistoryPlanningWindow window =
                    ReadExactStepWindow(
                        endpoint,
                        seeds[previous],
                        cancellationToken
                    );
                costs.Add(new RecapPlannedStepCost(
                    maintain.RecapBlockId,
                    previous,
                    endpoint,
                    window.RawAddresses.Count
                ));
                previous = endpoint;
            }
        }
        RecapPlanResult planResult = RecapPlanEvaluator.ValidatePlan(
            ready,
            new RecapPlanPreflightFacts(sourceFacts, costs)
        );
        return new PreparedIntent(planResult);

        RecapSourceReplayFact SourceFact(
            RecapBlockId blockId,
            RecapSourceIntent source
        ) {
            if (!inputs.TryGetValue(
                    blockId,
                    out DerivedRecapFrozenInput? input
                )
                || sourceSnapshot is null
                || source.SourceSetAnchor
                    != sourceSnapshot.Source.SetAdmissionAnchor
                || !string.Equals(
                    source.SourcePublicationEnvelopeSha256,
                    sourceSnapshot.Source.EnvelopeSha256,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Exact frozen source for block '{blockId}' "
                    + "is unavailable."
                );
            }
            return new RecapSourceReplayFact(
                blockId,
                source,
                input.AbsorbedThrough
            );
        }
    }

    private async ValueTask<DerivedRecapExecutionResult>
        ExecuteAndPublishAsync(
        BuildingSnapshot building,
        CancellationToken cancellationToken
    ) {
        PreparedBuilding prepared;
        try {
            prepared = await PrepareBuildingAsync(
                    building,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes
                    .RawPlanningUnavailable,
                exception.Message
            );
        }
        if (prepared.Defects.Count != 0) {
            return new DerivedRecapExecutionResult.Unavailable(
                prepared.Defects
            );
        }

        foreach (RecapBlockPlan plan in building.Manifest.Blocks) {
            DerivedRecapExecutionResult? blockResult;
            try {
                blockResult = await EnsureBlockAsync(
                            building,
                            plan,
                            prepared.Inspections[plan.RecapBlockId],
                            prepared.Windows,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (IsAvailabilityException(exception)) {
                return Unavailable(
                    DerivedRecapExecutionDefectCodes.BuildingInvalid,
                    exception.Message
                );
            }
            if (blockResult is not null) {
                return blockResult;
            }
        }

        try {
            RecapPublishability publishability =
                await _publisher.CanPublishAsync(
                        building.Manifest.SetAdmissionAnchor,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (!publishability.IsPublishable) {
                return Unavailable(publishability.Defects);
            }
            PublishedRecapDescriptor descriptor =
                await _publisher.PublishAsync(
                        building.Manifest.SetAdmissionAnchor,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            return new DerivedRecapExecutionResult.Published(descriptor);
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains(
                "Raw SessionJournal head changed",
                StringComparison.Ordinal)) {
            return new DerivedRecapExecutionResult.Retryable(
                DerivedRecapExecutionDefectCodes.RawHeadChanged,
                exception.Message
            );
        }
        catch (Exception exception) when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes
                    .PublicationUnavailable,
                exception.Message
            );
        }
    }

    private async ValueTask<PreparedBuilding> PrepareBuildingAsync(
        BuildingSnapshot building,
        CancellationToken cancellationToken
    ) {
        var defects = new List<DerivedRecapExecutionDefect>();
        var emptyInspections =
            new Dictionary<RecapBlockId, BuildingBlockInspection>();
        var emptyWindows = new Dictionary<
            (RecapBlockId, int),
            SessionHistoryPlanningWindow
        >();
        if (building.Manifest.RefId != _store.RefId
            || building.Manifest.SetAdmissionAnchor
                != building.Descriptor.SetAdmissionAnchor
            || building.Manifest.Blocks.Count != _config.Catalog.Count) {
            AddConfigDefect(
                defects,
                "Building manifest does not match the bound RefId, "
                + "anchor, or catalog size."
            );
            return new PreparedBuilding(
                defects,
                emptyInspections,
                emptyWindows
            );
        }

        SessionCurrentLineageSnapshot lineage =
            _engine.ReadCurrentLineageHeaders(cancellationToken);
        Dictionary<EventAddress, int> lineageIndex =
            lineage.HeadToRoot
                .Select((node, index) => (node.Address, index))
                .ToDictionary(
                    static pair => pair.Address,
                    static pair => pair.index
                );
        if (!lineageIndex.TryGetValue(
                building.Manifest.SetAdmissionAnchor,
                out int admissionIndex
            )) {
            AddBuildingDefect(
                defects,
                "Building admission anchor is outside current raw lineage."
            );
            return new PreparedBuilding(
                defects,
                emptyInspections,
                emptyWindows
            );
        }

        long calls = 0;
        for (int index = 0;
             index < building.Manifest.Blocks.Count;
             index++) {
            RecapBlockPlan plan = building.Manifest.Blocks[index];
            RecapBlockCatalogEntry entry = _config.Catalog[index];
            if (plan.RecapBlockId != entry.RecapBlockId
                || plan.Target != entry.Target
                || plan.MaxContentUtf8Bytes
                    != entry.MaxContentUtf8Bytes
                || plan is MaintainRecapBlockPlan maintain
                   && !string.Equals(
                       maintain.MaintainerId,
                       entry.MaintainerId,
                       StringComparison.Ordinal
                   )) {
                AddConfigDefect(
                    defects,
                    $"Building block '{plan.RecapBlockId}' differs "
                    + "from the active catalog binding."
                );
                continue;
            }
            ValidateBuildingPlanRawSemantics(
                building,
                plan,
                lineageIndex,
                admissionIndex,
                defects
            );
            if (plan is MaintainRecapBlockPlan maintainPlan) {
                calls += maintainPlan.CatchUpThrough.Count;
                if (maintainPlan.CatchUpThrough.Count
                    > _config.MaxRouteEndpointsPerBlock) {
                    AddConfigDefect(
                        defects,
                        $"Block '{plan.RecapBlockId}' exceeds the "
                        + "route limit."
                    );
                }
            }
        }
        if (calls > _config.MaxMaintainerCallsPerBuild) {
            AddConfigDefect(
                defects,
                $"Building requires {calls} Maintainer calls; limit is "
                + $"{_config.MaxMaintainerCallsPerBuild}."
            );
        }
        if (defects.Count != 0) {
            return new PreparedBuilding(
                defects,
                emptyInspections,
                emptyWindows
            );
        }

        var inspections =
            new Dictionary<RecapBlockId, BuildingBlockInspection>();
        var starts = new List<EventAddress>();
        var pending = new Dictionary<RecapBlockId, int>();
        foreach (RecapBlockPlan plan in building.Manifest.Blocks) {
            BuildingBlockInspection inspection =
                await _store.InspectBuildingBlockAsync(
                        building.Descriptor,
                        plan.RecapBlockId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (!string.Equals(
                    DerivedRecapCodec.ComputeBlockPlanSha256(
                        inspection.Plan
                    ),
                    DerivedRecapCodec.ComputeBlockPlanSha256(plan),
                    StringComparison.Ordinal
                )) {
                AddBuildingDefect(
                    defects,
                    $"Building block '{plan.RecapBlockId}' plan changed."
                );
                continue;
            }
            inspections.Add(plan.RecapBlockId, inspection);
            if (plan is not MaintainRecapBlockPlan maintain
                || inspection.Final
                    is FinalRecapBlockHealth.Healthy) {
                continue;
            }
            int next = inspection.Checkpoint
                is RollingRecapCheckpointHealth.Healthy checkpoint
                    ? checkpoint.EndpointIndex + 1
                    : 0;
            pending.Add(plan.RecapBlockId, next);
            EventAddress previous = next == 0
                ? GetMaintainStart(building, maintain)
                : maintain.CatchUpThrough[next - 1];
            for (int index = next;
                 index < maintain.CatchUpThrough.Count;
                 index++) {
                starts.Add(previous);
                previous = maintain.CatchUpThrough[index];
            }
        }
        if (defects.Count != 0) {
            return new PreparedBuilding(
                defects,
                inspections,
                emptyWindows
            );
        }

        var windows = new Dictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        >();
        if (starts.Count == 0) {
            return new PreparedBuilding(defects, inspections, windows);
        }
        SessionHistoryPlanningSeedBatch seedBatch =
            _engine.ReadHistoryPlanningSeeds(
                starts.Distinct(),
                cancellationToken
            );
        if (seedBatch.Lineage.CapturedHead != lineage.CapturedHead) {
            throw new InvalidDataException(
                "Raw head changed while freezing Building replay windows."
            );
        }
        Dictionary<EventAddress, SessionHistoryPlanningSeed> seeds =
            seedBatch.Seeds.ToDictionary(static seed => seed.Address);
        long rawEvents = 0;
        foreach (MaintainRecapBlockPlan plan
                 in building.Manifest.Blocks
                     .OfType<MaintainRecapBlockPlan>()) {
            if (!pending.TryGetValue(
                    plan.RecapBlockId,
                    out int next
                )) {
                continue;
            }
            EventAddress previous = next == 0
                ? GetMaintainStart(building, plan)
                : plan.CatchUpThrough[next - 1];
            for (int index = next;
                 index < plan.CatchUpThrough.Count;
                 index++) {
                EventAddress endpoint = plan.CatchUpThrough[index];
                SessionHistoryPlanningWindow window =
                    ReadExactStepWindow(
                        endpoint,
                        seeds[previous],
                        cancellationToken
                    );
                if (window.RawAddresses.Count
                    > _config.MaxRawEventsPerStep) {
                    AddConfigDefect(
                        defects,
                        $"Block '{plan.RecapBlockId}' step "
                        + $"{index} exceeds the raw step limit."
                    );
                }
                rawEvents += window.RawAddresses.Count;
                windows.Add((plan.RecapBlockId, index), window);
                previous = endpoint;
            }
        }
        if (rawEvents > _config.MaxRawEventsPerBuild) {
            AddConfigDefect(
                defects,
                $"Building requires {rawEvents} raw events; limit is "
                + $"{_config.MaxRawEventsPerBuild}."
            );
        }
        return new PreparedBuilding(defects, inspections, windows);
    }

    private async ValueTask<DerivedRecapExecutionResult?>
        EnsureBlockAsync(
        BuildingSnapshot building,
        RecapBlockPlan plan,
        BuildingBlockInspection inspection,
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        > windows,
        CancellationToken cancellationToken
    ) {
        if (inspection.Final is FinalRecapBlockHealth.Healthy) {
            return null;
        }

        if (plan is InheritRecapBlockPlan) {
            if (inspection.FrozenInput is not { } input) {
                return Unavailable(
                    DerivedRecapExecutionDefectCodes.BuildingInvalid,
                    $"Inherit block '{plan.RecapBlockId}' has no "
                    + "frozen input."
                );
            }
            DerivedRecapBlock candidate = DerivedRecapCodec.CreateBlock(
                plan,
                input.AbsorbedThrough,
                input.Content
            );
            return await EnsureFinalAsync(
                    building.Descriptor,
                    inspection,
                    candidate,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var maintain = (MaintainRecapBlockPlan)plan;
        if (!_maintainers.TryResolve(
                maintain.MaintainerId,
                maintain.Target,
                out IRecapBlockMaintainer? maintainer
            )
            || !string.Equals(
                maintainer.Id,
                maintain.MaintainerId,
                StringComparison.Ordinal
            )
            || maintainer.Target != maintain.Target) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes
                    .ManifestConfigMismatch,
                $"Maintainer binding for '{plan.RecapBlockId}' "
                + "is unavailable."
            );
        }

        DerivedRecapBlock? currentBlock = null;
        int nextEndpoint = 0;
        string checkpointToken = inspection.Checkpoint.StateToken;
        if (inspection.Checkpoint
            is RollingRecapCheckpointHealth.Healthy checkpoint) {
            currentBlock = checkpoint.Block;
            nextEndpoint = checkpoint.EndpointIndex + 1;
        }
        else {
            currentBlock = maintain.Source switch {
                ExistingRecapMaintainSource =>
                    inspection.FrozenInput is { } input
                        ? DerivedRecapCodec.CreateBlock(
                            plan,
                            input.AbsorbedThrough,
                            input.Content
                        )
                        : null,
                EmptyRecapMaintainSource => null,
                _ => null
            };
            if (maintain.Source is ExistingRecapMaintainSource
                && currentBlock is null) {
                return Unavailable(
                    DerivedRecapExecutionDefectCodes.BuildingInvalid,
                    $"Maintain block '{plan.RecapBlockId}' has no "
                    + "frozen input."
                );
            }
        }

        while (nextEndpoint < maintain.CatchUpThrough.Count) {
            cancellationToken.ThrowIfCancellationRequested();
            SessionHistoryPlanningWindow window =
                windows[(plan.RecapBlockId, nextEndpoint)];
            RecapBlockMaintenanceResult result;
            try {
                result = await maintainer.MaintainAsync(
                        new RecapBlockMaintenanceRequest(
                            new RecentHistorySlice(
                                GetPriorContext(maintain.PriorContext),
                                window.Units
                                    .Select(static unit => unit.Message)
                                    .ToArray(),
                                $"{window.StartExclusive}.."
                                + $"{window.ObservedRawHead}"
                            ),
                            new ContextHeaderBlock(
                                currentBlock?.Content ?? string.Empty
                            )
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested) {
                throw;
            }
            catch (Exception exception) {
                return new DerivedRecapExecutionResult.BlockFailed(
                    plan.RecapBlockId,
                    DerivedRecapExecutionDefectCodes.MaintainerFailed,
                    exception.Message
                );
            }
            string? invalidResult = ValidateMaintainerResult(
                maintain,
                result
            );
            if (invalidResult is not null) {
                return new DerivedRecapExecutionResult.BlockFailed(
                    plan.RecapBlockId,
                    DerivedRecapExecutionDefectCodes
                        .MaintainerResultInvalid,
                    invalidResult
                );
            }
            DerivedRecapBlock candidate = DerivedRecapCodec.CreateBlock(
                plan,
                maintain.CatchUpThrough[nextEndpoint],
                result.NewBlock.Text
            );
            CheckpointWriteResult write =
                await _store.AdvanceRollingCheckpointAsync(
                        building.Descriptor,
                        plan.RecapBlockId,
                        checkpointToken,
                        candidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            switch (write) {
                case CheckpointWriteResult.Updated updated:
                    checkpointToken = updated.StateToken;
                    currentBlock = candidate;
                    nextEndpoint++;
                    break;
                case CheckpointWriteResult.AlreadyCurrent current:
                    checkpointToken = current.StateToken;
                    currentBlock = candidate;
                    nextEndpoint++;
                    break;
                case CheckpointWriteResult.Stale:
                    BuildingBlockInspection refreshed =
                        await _store.InspectBuildingBlockAsync(
                                building.Descriptor,
                                plan.RecapBlockId,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    if (refreshed.Final
                        is FinalRecapBlockHealth.Healthy) {
                        return null;
                    }
                    if (refreshed.Checkpoint
                        is not RollingRecapCheckpointHealth.Healthy
                            advanced
                        || advanced.EndpointIndex < nextEndpoint) {
                        return new DerivedRecapExecutionResult.Retryable(
                            DerivedRecapExecutionDefectCodes
                                .ConcurrentBuildingChange,
                            $"Checkpoint for '{plan.RecapBlockId}' "
                            + "changed without a usable forward advance."
                        );
                    }
                    checkpointToken = advanced.StateToken;
                    currentBlock = advanced.Block;
                    nextEndpoint = advanced.EndpointIndex + 1;
                    inspection = refreshed;
                    break;
            }
        }

        if (currentBlock is null) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.BuildingInvalid,
                $"Maintain block '{plan.RecapBlockId}' produced no "
                + "final checkpoint."
            );
        }
        BuildingBlockInspection finalInspection =
            await _store.InspectBuildingBlockAsync(
                    building.Descriptor,
                    plan.RecapBlockId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (finalInspection.Final is FinalRecapBlockHealth.Healthy) {
            return null;
        }
        return await EnsureFinalAsync(
                building.Descriptor,
                finalInspection,
                currentBlock,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<DerivedRecapExecutionResult?> EnsureFinalAsync(
        BuildingDescriptor building,
        BuildingBlockInspection inspection,
        DerivedRecapBlock candidate,
        CancellationToken cancellationToken
    ) {
        FinalBlockWriteResult write =
            await _store.EnsureFinalBlockAsync(
                    building,
                    inspection.Plan.RecapBlockId,
                    inspection.Final.StateToken,
                    candidate,
                    cancellationToken
                )
                .ConfigureAwait(false);
        switch (write) {
            case FinalBlockWriteResult.Installed:
            case FinalBlockWriteResult.ReplacedDamaged:
            case FinalBlockWriteResult.AlreadyHealthy:
                return null;
            case FinalBlockWriteResult.HealthyConflict:
                return new DerivedRecapExecutionResult.Retryable(
                    DerivedRecapExecutionDefectCodes
                        .ConcurrentBuildingChange,
                    $"Final block '{inspection.Plan.RecapBlockId}' "
                    + "was concurrently installed with different bytes."
                );
            case FinalBlockWriteResult.Stale:
                BuildingBlockInspection refreshed =
                    await _store.InspectBuildingBlockAsync(
                            building,
                            inspection.Plan.RecapBlockId,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                return refreshed.Final
                    is FinalRecapBlockHealth.Healthy healthy
                    && healthy.Block == candidate
                        ? null
                        : new DerivedRecapExecutionResult.Retryable(
                            DerivedRecapExecutionDefectCodes
                                .ConcurrentBuildingChange,
                            $"Final block '{inspection.Plan.RecapBlockId}' "
                            + "changed concurrently."
                        );
            default:
                throw new InvalidOperationException(
                    "Unknown final block write result."
                );
        }
    }

    private DerivedRecapSetManifest CreateManifest(
        RecapPlanningPolicyDecision.Build intent,
        PublishedRecapSourceSnapshot? sourceSnapshot
    ) {
        Dictionary<RecapBlockId, DerivedRecapFrozenInput> inputs =
            sourceSnapshot?.FrozenInputs.ToDictionary(
                static input => input.RecapBlockId
            ) ?? [];
        RecapBlockPlan[] plans = intent.Blocks
            .Select((decision, index) => decision switch {
                RecapBlockPlanningDecision.Inherit inherit =>
                    (RecapBlockPlan)new InheritRecapBlockPlan(
                        inherit.RecapBlockId,
                        _config.Catalog[index].Target,
                        inherit.Source.SourceSetAnchor,
                        inherit.Source
                            .SourcePublicationEnvelopeSha256,
                        inputs[inherit.RecapBlockId].PayloadSha256,
                        _config.Catalog[index].MaxContentUtf8Bytes
                    ),
                RecapBlockPlanningDecision.Maintain maintain =>
                    (RecapBlockPlan)new MaintainRecapBlockPlan(
                        maintain.RecapBlockId,
                        _config.Catalog[index].Target,
                        _config.Catalog[index].MaintainerId,
                        maintain.Source switch {
                            RecapPlanningMaintainSource.Existing existing =>
                                new ExistingRecapMaintainSource(
                                    existing.Source.SourceSetAnchor,
                                    existing.Source
                                        .SourcePublicationEnvelopeSha256,
                                    inputs[maintain.RecapBlockId]
                                        .PayloadSha256
                                ),
                            RecapPlanningMaintainSource.Empty empty =>
                                new EmptyRecapMaintainSource(
                                    empty.ReplayStartExclusive
                                ),
                            _ => throw new InvalidDataException(
                                "Unsupported Maintain source intent."
                            )
                        },
                        maintain.CatchUpThrough,
                        maintain.PriorContext,
                        _config.Catalog[index].MaxContentUtf8Bytes
                    ),
                _ => throw new InvalidDataException(
                    "Unsupported planning decision."
                )
            })
            .ToArray();
        return DerivedRecapCodec.CreateManifest(
            _store.RefId,
            intent.SetAdmissionAnchor,
            plans
        );
    }

    private SessionHistoryPlanningWindow ReadExactStepWindow(
        EventAddress endpoint,
        SessionHistoryPlanningSeed seed,
        CancellationToken cancellationToken
    ) {
        SessionHistoryPlanningWindow window =
            _engine.ReadHistoryPlanningWindowAt(
                endpoint,
                seed,
                cancellationToken
            );
        if (window.StartExclusive != seed.Address
            || window.ObservedRawHead != endpoint
            || !window.ReplaySafeBoundaries.Any(
                boundary => boundary.Address == endpoint)) {
            throw new InvalidDataException(
                "Raw planning window is not the requested exact "
                + "replay-safe interval."
            );
        }
        return window;
    }

    private static EventAddress? FindEarliestSourceCursor(
        SessionCurrentLineageSnapshot lineage,
        PublishedRecapSourceSnapshot? source
    ) {
        if (source is null) {
            return null;
        }
        if (source.FrozenInputs.Count == 0) {
            throw new InvalidDataException(
                "Published source has no active frozen inputs."
            );
        }
        Dictionary<EventAddress, int> lineageIndex =
            lineage.HeadToRoot
                .Select((node, index) => (node.Address, index))
                .ToDictionary(
                    static pair => pair.Address,
                    static pair => pair.index
                );
        EventAddress earliest = default;
        int earliestIndex = -1;
        foreach (DerivedRecapFrozenInput input in source.FrozenInputs) {
            if (!lineageIndex.TryGetValue(
                    input.AbsorbedThrough,
                    out int inputIndex
                )) {
                throw new InvalidDataException(
                    $"Published source block '{input.RecapBlockId}' "
                    + "cursor is outside the captured raw lineage."
                );
            }
            if (inputIndex > earliestIndex) {
                earliest = input.AbsorbedThrough;
                earliestIndex = inputIndex;
            }
        }
        return earliest;
    }

    private static void ValidateBuildingPlanRawSemantics(
        BuildingSnapshot building,
        RecapBlockPlan plan,
        IReadOnlyDictionary<EventAddress, int> lineage,
        int admissionIndex,
        List<DerivedRecapExecutionDefect> defects
    ) {
        switch (plan) {
            case InheritRecapBlockPlan inherit:
                if (!TryValidateFrozenSource(
                        building,
                        plan,
                        inherit.SourceSetAnchor,
                        lineage,
                        admissionIndex,
                        defects,
                        out DerivedRecapFrozenInput? inheritedInput,
                        out _
                    )) {
                    return;
                }
                if (string.IsNullOrEmpty(inheritedInput.Content)) {
                    AddBuildingDefect(
                        defects,
                        $"Inherit block '{plan.RecapBlockId}' source "
                        + "content is empty."
                    );
                    return;
                }
                try {
                    if (new UTF8Encoding(false, true).GetByteCount(
                            inheritedInput.Content
                        ) > plan.MaxContentUtf8Bytes) {
                        AddBuildingDefect(
                            defects,
                            $"Inherit block '{plan.RecapBlockId}' "
                            + "source content exceeds its frozen limit."
                        );
                    }
                }
                catch (EncoderFallbackException) {
                    AddBuildingDefect(
                        defects,
                        $"Inherit block '{plan.RecapBlockId}' source "
                        + "content is not valid UTF-8."
                    );
                }
                return;

            case MaintainRecapBlockPlan maintain:
                int startIndex;
                switch (maintain.Source) {
                    case EmptyRecapMaintainSource empty:
                        if (!lineage.TryGetValue(
                                empty.ReplayStartExclusive,
                                out startIndex
                            )
                            || startIndex <= admissionIndex) {
                            AddBuildingDefect(
                                defects,
                                $"Maintain block '{plan.RecapBlockId}' "
                                + "empty replay start is not a strict "
                                + "admission ancestor."
                            );
                            return;
                        }
                        break;
                    case ExistingRecapMaintainSource existing:
                        if (!TryValidateFrozenSource(
                                building,
                                plan,
                                existing.SourceSetAnchor,
                                lineage,
                                admissionIndex,
                                defects,
                                out _,
                                out startIndex
                            )) {
                            return;
                        }
                        break;
                    default:
                        AddBuildingDefect(
                            defects,
                            $"Maintain block '{plan.RecapBlockId}' "
                            + "has an unsupported source."
                        );
                        return;
                }

                if (maintain.PriorContext
                        is InlineRecapPriorContext inline
                    && (!lineage.TryGetValue(
                            inline.AdmissionAnchor,
                            out int priorIndex
                        )
                        || priorIndex < startIndex)) {
                    AddBuildingDefect(
                        defects,
                        $"Maintain block '{plan.RecapBlockId}' inline "
                        + "prior context is not an ancestor of its "
                        + "replay start."
                    );
                }

                int previousIndex = startIndex;
                foreach (EventAddress endpoint
                         in maintain.CatchUpThrough) {
                    if (!lineage.TryGetValue(
                            endpoint,
                            out int endpointIndex
                        )
                        || endpointIndex >= previousIndex) {
                        AddBuildingDefect(
                            defects,
                            $"Maintain block '{plan.RecapBlockId}' route "
                            + "is not strictly increasing from its exact "
                            + "source cursor."
                        );
                        return;
                    }
                    previousIndex = endpointIndex;
                }
                if (maintain.CatchUpThrough.Count == 0
                    || maintain.CatchUpThrough[^1]
                        != building.Manifest.SetAdmissionAnchor) {
                    AddBuildingDefect(
                        defects,
                        $"Maintain block '{plan.RecapBlockId}' route "
                        + "does not end at SetAdmissionAnchor."
                    );
                }
                return;

            default:
                AddBuildingDefect(
                    defects,
                    $"Building block '{plan.RecapBlockId}' has an "
                    + "unsupported plan mode."
                );
                return;
        }
    }

    private static bool TryValidateFrozenSource(
        BuildingSnapshot building,
        RecapBlockPlan plan,
        EventAddress sourceSetAnchor,
        IReadOnlyDictionary<EventAddress, int> lineage,
        int admissionIndex,
        List<DerivedRecapExecutionDefect> defects,
        out DerivedRecapFrozenInput input,
        out int cursorIndex
    ) {
        input = null!;
        cursorIndex = -1;
        if (!lineage.TryGetValue(
                sourceSetAnchor,
                out int sourceIndex
            )
            || sourceIndex <= admissionIndex) {
            AddBuildingDefect(
                defects,
                $"Block '{plan.RecapBlockId}' source set is not a "
                + "strict admission ancestor."
            );
            return false;
        }
        if (!building.FrozenInputs.TryGetValue(
                plan.RecapBlockId,
                out DerivedRecapFrozenInput? foundInput
            )
            || foundInput.Target != plan.Target
            || !lineage.TryGetValue(
                foundInput.AbsorbedThrough,
                out cursorIndex
            )
            || cursorIndex < sourceIndex) {
            AddBuildingDefect(
                defects,
                $"Block '{plan.RecapBlockId}' frozen cursor is not "
                + "at or before its source set container."
            );
            return false;
        }
        input = foundInput;
        return true;
    }

    private static EventAddress GetMaintainStart(
        BuildingSnapshot building,
        MaintainRecapBlockPlan plan
    ) => plan.Source switch {
        EmptyRecapMaintainSource empty => empty.ReplayStartExclusive,
        ExistingRecapMaintainSource =>
            building.FrozenInputs.TryGetValue(
                plan.RecapBlockId,
                out DerivedRecapFrozenInput? input
            )
                ? input.AbsorbedThrough
                : throw new InvalidDataException(
                    $"Building block '{plan.RecapBlockId}' is "
                    + "missing its frozen source input."
                ),
        _ => throw new InvalidDataException(
            "Unsupported Maintain source."
        )
    };

    private static void AddStepStarts(
        EventAddress first,
        IReadOnlyList<EventAddress> endpoints,
        List<EventAddress> starts
    ) {
        EventAddress previous = first;
        foreach (EventAddress endpoint in endpoints) {
            starts.Add(previous);
            previous = endpoint;
        }
    }

    private static ContextHeaderSnapshot GetPriorContext(
        RecapPriorContext prior
    ) => prior switch {
        EmptyRecapPriorContext => ContextHeaderSnapshot.Empty,
        InlineRecapPriorContext inline => inline.Snapshot,
        _ => throw new InvalidDataException(
            "Unsupported Recap prior context."
        )
    };

    private static string? ValidateMaintainerResult(
        MaintainRecapBlockPlan plan,
        RecapBlockMaintenanceResult? result
    ) {
        if (result is null) {
            return "Maintainer returned null.";
        }
        if (!string.Equals(
                result.MaintainerId,
                plan.MaintainerId,
                StringComparison.Ordinal
            )
            || result.Target != plan.Target) {
            return "Maintainer result Id or Target does not match "
                + "the frozen block plan.";
        }
        if (result.NewBlock is null
            || string.IsNullOrEmpty(result.NewBlock.Text)) {
            return "Maintainer result content cannot be empty.";
        }
        if (result.Errors is { Count: > 0 }) {
            return "Maintainer returned errors: "
                + string.Join("; ", result.Errors);
        }
        try {
            if (new UTF8Encoding(false, true).GetByteCount(
                    result.NewBlock.Text
                ) > plan.MaxContentUtf8Bytes) {
                return $"Maintainer result exceeds "
                    + $"{plan.MaxContentUtf8Bytes} UTF-8 bytes.";
            }
        }
        catch (EncoderFallbackException) {
            return "Maintainer result content is not valid UTF-8.";
        }
        return null;
    }

    private static DerivedRecapExecutionResult SourceReadUnavailable(
        PublishedRecapSourceReadResult result
    ) => result switch {
        PublishedRecapSourceReadResult.Invalid invalid =>
            Unavailable(invalid.Defects),
        PublishedRecapSourceReadResult.Missing missing => Unavailable(
            DerivedRecapExecutionDefectCodes
                .PublishedSourceUnavailable,
            $"Published source '{missing.SourceSetAnchor}' is missing."
        ),
        PublishedRecapSourceReadResult.SnapshotTokenMismatch mismatch =>
            new DerivedRecapExecutionResult.Retryable(
                DerivedRecapExecutionDefectCodes.SourceChanged,
                $"Published source token changed from "
                + $"'{mismatch.Expected}' to '{mismatch.Observed}'."
            ),
        PublishedRecapSourceReadResult.ChangedDuringRead changed =>
            new DerivedRecapExecutionResult.Retryable(
                DerivedRecapExecutionDefectCodes.SourceChanged,
                $"Published source changed during read from "
                + $"'{changed.Expected}' to '{changed.Observed}'."
            ),
        _ => throw new InvalidOperationException(
            "Unknown Published source read result."
        )
    };

    private static DerivedRecapExecutionResult.Unavailable Unavailable(
        IReadOnlyList<RecapPlanDefect> defects
    ) => new([
        .. defects.Select(defect =>
            new DerivedRecapExecutionDefect(
                defect.Code,
                defect.Detail
            )
        )
    ]);

    private static DerivedRecapExecutionResult.Unavailable Unavailable(
        IReadOnlyList<RecapStructuralDefect> defects
    ) => new([
        .. defects.Select(defect =>
            new DerivedRecapExecutionDefect(
                defect.Code,
                defect.Detail
            )
        )
    ]);

    private static DerivedRecapExecutionResult.Unavailable Unavailable(
        string code,
        string detail
    ) => new([new DerivedRecapExecutionDefect(code, detail)]);

    private static DerivedRecapExecutionResult.Retryable RetryableRawHead(
        EventAddress expected
    ) => new(
        DerivedRecapExecutionDefectCodes.RawHeadChanged,
        $"Raw SessionJournal head changed during planning. Expected "
        + $"'{expected}'."
    );

    private static void AddConfigDefect(
        List<DerivedRecapExecutionDefect> defects,
        string detail
    ) => defects.Add(new(
        DerivedRecapExecutionDefectCodes.ManifestConfigMismatch,
        detail
    ));

    private static void AddBuildingDefect(
        List<DerivedRecapExecutionDefect> defects,
        string detail
    ) => defects.Add(new(
        DerivedRecapExecutionDefectCodes.BuildingInvalid,
        detail
    ));

    private static bool IsAvailabilityException(Exception exception)
        => exception is InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or KeyNotFoundException;

    private static void RequireSameBinding(
        DerivedRecapStore store,
        SessionJournalEngine engine
    ) {
        string storePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(store.SessionRepositoryPath)
        );
        string enginePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(engine.Path)
        );
        if (!string.Equals(
                storePath,
                enginePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
            || store.RefId != engine.BranchRefId) {
            throw new ArgumentException(
                "DerivedRecap Planner, Store, and SessionJournalEngine "
                + "must bind the same repository and RefId."
            );
        }
    }

    private sealed record PreparedIntent(RecapPlanResult PlanResult);

    private sealed record PreparedBuilding(
        IReadOnlyList<DerivedRecapExecutionDefect> Defects,
        IReadOnlyDictionary<
            RecapBlockId,
            BuildingBlockInspection
        > Inspections,
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        > Windows
    );
}
