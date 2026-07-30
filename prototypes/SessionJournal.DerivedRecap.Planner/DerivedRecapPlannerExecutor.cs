using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal sealed record DerivedRecapBuildingExecutorTestHooks(
    Action? BeforePendingWindowFreeze = null
);

/// <summary>
/// Creates a new frozen Building from active planning inputs and repo-owned
/// planning limits, then delegates durable execution to the Building executor.
/// </summary>
public sealed class DerivedRecapPlannerExecutor {
    private readonly SessionJournalEngine _engine;
    private readonly DerivedRecapStore _store;
    private readonly RecapPlanningInputs _inputs;
    private readonly RecapPlanningLimits _limits;
    private readonly DerivedRecapBuildingInstaller _installer;
    private readonly DerivedRecapBuildingExecutor _buildingExecutor;
    private DerivedRecapPlanningDiagnostics? _lastPlanningDiagnostics;

    public DerivedRecapPlannerExecutor(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        RecapPlanningInputs inputs,
        RecapPlanningLimits limits,
        IRecapBlockMaintainerRegistry maintainers
    ) : this(
        engine,
        store,
        inputs,
        limits,
        maintainers,
        RecapProtocolHardCaps.V4,
        new DerivedRecapBuildingExecutorTestHooks()
    ) {
    }

    internal DerivedRecapPlannerExecutor(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        RecapPlanningInputs inputs,
        RecapPlanningLimits limits,
        IRecapBlockMaintainerRegistry maintainers,
        RecapProtocolHardCaps hardCaps,
        DerivedRecapBuildingExecutorTestHooks testHooks
    ) {
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        _store = store
            ?? throw new ArgumentNullException(nameof(store));
        _inputs = inputs
            ?? throw new ArgumentNullException(nameof(inputs));
        _limits = limits
            ?? throw new ArgumentNullException(nameof(limits));
        ArgumentNullException.ThrowIfNull(maintainers);
        ArgumentNullException.ThrowIfNull(hardCaps);
        ArgumentNullException.ThrowIfNull(testHooks);
        hardCaps.ValidatePlanningAuthority(inputs, limits);
        RequireSameBinding(store, engine);
        foreach (RecapBlockCatalogEntry entry
                 in inputs.OrderedCatalog) {
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
        _buildingExecutor = new DerivedRecapBuildingExecutor(
            engine,
            store,
            maintainers,
            hardCaps,
            testHooks
        );
    }

    public DerivedRecapPlanningDiagnostics? LastPlanningDiagnostics =>
        Volatile.Read(ref _lastPlanningDiagnostics);

    public async ValueTask<DerivedRecapExecutionResult> RunAsync(
        CancellationToken cancellationToken = default
    ) {
        Volatile.Write(ref _lastPlanningDiagnostics, null);
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
        DerivedRecapPlanningBaseline baseline;
        try {
            baseline = DerivedRecapPlanningBaseline.FromSelection(
                lineage.CapturedHead,
                selection
            );
        }
        catch (ArgumentException) {
            return SelectionUnavailable(selection);
        }
        return await RunAsync(baseline, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<DerivedRecapExecutionResult> RunAsync(
        DerivedRecapPlanningBaseline baseline,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(baseline);
        Volatile.Write(ref _lastPlanningDiagnostics, null);
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
        if (lineage.CapturedHead != baseline.CapturedRawHead) {
            return RetryableRawHead(baseline.CapturedRawHead);
        }
        DerivedRecapExecutionResult? baselineMismatch =
            MatchPlanningBaseline(baseline, selection);
        if (baselineMismatch is not null) {
            return baselineMismatch;
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

        PublishedRecapSourceSnapshot? sourceSnapshot = null;
        Dictionary<RecapBlockId, DerivedRecapFrozenInput>
            sourceInputsById = [];
        if (latest is not null) {
            PublishedPlanReadResult planRead;
            try {
                planRead = await _store.ReadPublishedPlanAsync(
                        latest,
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
            switch (planRead) {
                case PublishedPlanReadResult.Available planAvailable:
                    DerivedRecapExecutionResult? catalogMismatch =
                        RequireCatalogShape(
                            planAvailable.Snapshot.FrozenPlan.Blocks
                        );
                    if (catalogMismatch is not null) {
                        return catalogMismatch;
                    }
                    break;
                case PublishedPlanReadResult.Changed changed:
                    return new DerivedRecapExecutionResult.Retryable(
                        DerivedRecapExecutionDefectCodes.SourceChanged,
                        $"Latest Published plan changed from "
                        + $"'{changed.Expected}' to "
                        + $"'{changed.Observed}'."
                    );
                case PublishedPlanReadResult.Unavailable unavailable:
                    return Unavailable(unavailable.Defects);
                default:
                    throw new InvalidOperationException(
                        "Unknown Published plan read result."
                    );
            }

            PublishedRecapSourceReadResult sourceRead;
            try {
                sourceRead = await _store.ReadPublishedSourceAsync(
                        latest,
                        _inputs.OrderedCatalog
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
            DerivedRecapExecutionResult? sourceCatalogMismatch =
                RequireCatalogShape(
                    sourceSnapshot.Publication
                        .FrozenPlanSnapshot.Blocks
                );
            if (sourceCatalogMismatch is not null) {
                return sourceCatalogMismatch;
            }
            sourceInputsById = sourceSnapshot.FrozenInputs.ToDictionary(
                static input => input.RecapBlockId
            );
            foreach (RecapBlockCatalogEntry entry
                     in _inputs.OrderedCatalog) {
                if (!sourceInputsById.TryGetValue(
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
        }

        // This gate may only reject work. Existing Published admission gives
        // an exact raw baseline; fresh bootstrap uses the whole lineage only
        // as a conservative upper bound. Exact HistoryUnit facts remain the
        // sole Build and raw-limit authority. Catalog compatibility above is
        // intentionally checked before this cadence fast path.
        RecapHeaderPrefilterResult headerPrefilter =
            RecapPlanEvaluator.EvaluateHeaderPrefilter(
                _inputs,
                lineage,
                latest?.SetAdmissionAnchor
            );
        switch (headerPrefilter) {
            case RecapHeaderPrefilterResult.NoBuild noBuild:
                Volatile.Write(
                    ref _lastPlanningDiagnostics,
                    new DerivedRecapPlanningDiagnostics.HeaderNegative(
                        noBuild.RawGrowthEventUpperBound
                    )
                );
                return new DerivedRecapExecutionResult.NoBuild(
                    noBuild.Reason
                );
            case RecapHeaderPrefilterResult.Unavailable unavailable:
                return Unavailable(unavailable.Defects);
        }

        RecapSchedulingResult.Ready schedule;
        EventAddress emptyReplayStartExclusive;
        try {
            EventAddress? earliestCursor =
                FindEarliestSourceCursor(lineage, sourceSnapshot);
            SessionHistoryPlanningWindow allRelevantRaw =
                _engine.ReadHistoryPlanningWindowAt(
                    lineage.CapturedHead,
                    earliestCursor,
                    cancellationToken
                );
            if (allRelevantRaw.ObservedRawHead
                != lineage.CapturedHead) {
                throw new InvalidDataException(
                    "Exact history window does not match the captured "
                    + "raw head."
                );
            }
            emptyReplayStartExclusive = allRelevantRaw.StartExclusive;
            EventAddress cadenceBaseline =
                latest?.SetAdmissionAnchor
                ?? allRelevantRaw.StartExclusive;
            RecapSchedulingResult exactSchedule =
                RecapPlanEvaluator.EvaluateSchedule(
                    _inputs,
                    _limits,
                    new RecapSchedulingFacts(
                        lineage.CapturedHead,
                        lineage.HeadToRoot,
                        new RecapHistoryWindowFacts(
                            allRelevantRaw.StartExclusive,
                            allRelevantRaw.Units.Count,
                            allRelevantRaw.ReplaySafeBoundaries
                        ),
                        cadenceBaseline,
                        latest?.SetAdmissionAnchor
                    )
                );
            RecapExactScheduleMeasurement? measurement =
                exactSchedule switch {
                    RecapSchedulingResult.Ready measuredReady =>
                        new RecapExactScheduleMeasurement(
                            measuredReady.Cadence
                                .GrowthHistoryUnitCount,
                            measuredReady.Cadence.RawGrowthEventCount
                        ),
                    RecapSchedulingResult.NoBuild noBuild =>
                        noBuild.Measurement,
                    RecapSchedulingResult.Unavailable unavailable =>
                        unavailable.Measurement,
                    _ => null
                };
            if (measurement is not null) {
                Volatile.Write(
                    ref _lastPlanningDiagnostics,
                    new DerivedRecapPlanningDiagnostics.ExactSchedule(
                        measurement.GrowthHistoryUnitCount,
                        measurement.RawGrowthEventCount
                    )
                );
            }
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

        RecapPolicyFacts policyFacts;
        if (sourceSnapshot is null) {
            policyFacts = new RecapPolicyFacts(
                emptyReplayStartExclusive,
                []
            );
        }
        else {
            var sourceIntent = new RecapSourceIntent(
                sourceSnapshot.Source.SetAdmissionAnchor,
                sourceSnapshot.Source.EnvelopeSha256
            );
            policyFacts = new RecapPolicyFacts(
                emptyReplayStartExclusive: null,
                [
                    .. _inputs.OrderedCatalog.Select(entry =>
                        new RecapBlockSourceIntent(
                            entry.RecapBlockId,
                            sourceIntent,
                            sourceInputsById[entry.RecapBlockId]
                                .AbsorbedThrough
                        )
                    )
                ]
            );
        }

        RecapPlanIntentResult intentResult =
            RecapPlanEvaluator.EvaluateIntent(
                schedule,
                policyFacts
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
            return await _buildingExecutor.ResumeAsync(
                    alreadyBuilding.Snapshot
                        .Descriptor.SetAdmissionAnchor,
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
            case CreateBuildingResult.ActiveBuildingConflict conflict:
                return new DerivedRecapExecutionResult.Retryable(
                    DerivedRecapExecutionDefectCodes.BuildingRace,
                    "Another current-lineage Building became active "
                    + "before installation: "
                    + string.Join(
                        ", ",
                        conflict.SetAdmissionAnchors
                    )
                    + "."
                );
        }

        // Do not execute from in-memory intent/source objects. The installed
        // Building is now the only recovery authority.
        return await _buildingExecutor.ResumeAsync(
                admission,
                cancellationToken
            )
            .ConfigureAwait(false);
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
                    RecapPendingWindowPreparer.ReadExactStepWindow(
                        _engine,
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
                        _inputs.OrderedCatalog[index].Target,
                        inherit.Source.SourceSetAnchor,
                        inherit.Source
                            .SourcePublicationEnvelopeSha256,
                        inputs[inherit.RecapBlockId].PayloadSha256,
                        _inputs.OrderedCatalog[index]
                            .MaxContentUtf8Bytes
                    ),
                RecapBlockPlanningDecision.Maintain maintain =>
                    (RecapBlockPlan)new MaintainRecapBlockPlan(
                        maintain.RecapBlockId,
                        _inputs.OrderedCatalog[index].Target,
                        _inputs.OrderedCatalog[index].MaintainerId,
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
                        _inputs.OrderedCatalog[index]
                            .MaxContentUtf8Bytes
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

    private static DerivedRecapExecutionResult? MatchPlanningBaseline(
        DerivedRecapPlanningBaseline baseline,
        DerivedRecapSelection observed
    ) {
        if (observed is DerivedRecapSelection.StoreUnavailable
                unavailable) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.StoreUnavailable,
                unavailable.Reason
            );
        }
        if (baseline.ExpectedLatestAnchor is null) {
            return observed is DerivedRecapSelection.EmptyLineage
                ? null
                : RetryableSource(
                    "Expected no latest Published recap, but the "
                    + $"latest selection is '{observed.GetType().Name}'."
                );
        }
        if (observed
            is not DerivedRecapSelection.Selected selected) {
            return RetryableSource(
                $"Expected latest Published anchor "
                + $"'{baseline.ExpectedLatestAnchor}' to resolve to a "
                + "healthy exact selection after any Restore, but "
                + $"observed '{observed.GetType().Name}'."
            );
        }
        if (selected.Descriptor.SetAdmissionAnchor
            != baseline.ExpectedLatestAnchor) {
            return RetryableSource(
                $"Expected latest Published anchor "
                + $"'{baseline.ExpectedLatestAnchor}', observed "
                + $"'{selected.Descriptor.SetAdmissionAnchor}'."
            );
        }
        if (baseline.ExpectedLatestPublished is { } exact
            && selected.Descriptor != exact) {
            return RetryableSource(
                $"Expected latest Published identity '{exact}', "
                + $"observed '{selected.Descriptor}'."
            );
        }
        return null;
    }

    private DerivedRecapExecutionResult? RequireCatalogShape(
        IReadOnlyList<RecapBlockPlan> frozenBlocks
    ) {
        RecapCatalogShapeComparison comparison =
            RecapCatalogShape.Compare(
                RecapCatalogShape.ProjectActive(
                    _inputs.OrderedCatalog
                ),
                RecapCatalogShape.ProjectFrozen(frozenBlocks)
            );
        return comparison.IsExactMatch
            ? null
            : Unavailable(
                DerivedRecapExecutionDefectCodes
                    .CatalogMigrationRequired,
                comparison.Detail
            );
    }

    private static DerivedRecapExecutionResult SelectionUnavailable(
        DerivedRecapSelection selection
    ) => selection switch {
        DerivedRecapSelection.ExactPublishedSetInvalid invalid =>
            Unavailable(invalid.Defects),
        DerivedRecapSelection.StoreUnavailable unavailable =>
            Unavailable(
                DerivedRecapExecutionDefectCodes.StoreUnavailable,
                unavailable.Reason
            ),
        DerivedRecapSelection.OrdinalUnavailable => Unavailable(
            DerivedRecapExecutionDefectCodes.StoreUnavailable,
            "Latest strict Published ordinal is unavailable."
        ),
        _ => new DerivedRecapExecutionResult.Retryable(
            DerivedRecapExecutionDefectCodes.SourceChanged,
            $"Cannot capture a new-planning baseline from "
            + $"'{selection.GetType().Name}'."
        )
    };

    private static DerivedRecapExecutionResult.Retryable RetryableSource(
        string detail
    ) => new(
        DerivedRecapExecutionDefectCodes.SourceChanged,
        detail
    );

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

    private static bool IsAvailabilityException(Exception exception)
        => exception is RecapRawHeadChangedException
            or InvalidDataException
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
}

/// <summary>
/// Resumes and publishes one frozen Building without consulting active
/// planning inputs or repo-owned planning limits.
/// </summary>
public sealed class DerivedRecapBuildingExecutor {
    private readonly SessionJournalEngine _engine;
    private readonly DerivedRecapStore _store;
    private readonly IRecapBlockMaintainerRegistry _maintainers;
    private readonly RecapProtocolHardCaps _hardCaps;
    private readonly DerivedRecapPublisher _publisher;
    private readonly DerivedRecapBuildingExecutorTestHooks _testHooks;

    public DerivedRecapBuildingExecutor(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        IRecapBlockMaintainerRegistry maintainers
    ) : this(
        engine,
        store,
        maintainers,
        RecapProtocolHardCaps.V4,
        new DerivedRecapBuildingExecutorTestHooks()
    ) {
    }

    internal DerivedRecapBuildingExecutor(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        IRecapBlockMaintainerRegistry maintainers,
        RecapProtocolHardCaps hardCaps,
        DerivedRecapBuildingExecutorTestHooks testHooks
    ) {
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        _store = store
            ?? throw new ArgumentNullException(nameof(store));
        _maintainers = maintainers
            ?? throw new ArgumentNullException(nameof(maintainers));
        _hardCaps = hardCaps
            ?? throw new ArgumentNullException(nameof(hardCaps));
        _testHooks = testHooks
            ?? throw new ArgumentNullException(nameof(testHooks));
        RequireSameBinding(store, engine);
        _publisher = new DerivedRecapPublisher(store, engine);
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
        catch (RecapRawHeadChangedException changed) {
            return RetryableRawHead(changed.Expected, changed.Observed);
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
                != building.Descriptor.SetAdmissionAnchor) {
            AddBuildingDefect(
                defects,
                "Building manifest does not match the bound RefId, "
                + "or descriptor anchor."
            );
            return new PreparedBuilding(
                defects,
                emptyInspections,
                emptyWindows
            );
        }
        if (building.Manifest.Blocks.Count
            > _hardCaps.MaxCatalogEntries
            || building.Manifest.Blocks.Any(plan =>
                plan.MaxContentUtf8Bytes
                    > _hardCaps.MaxContentUtf8Bytes)) {
            AddLimitDefect(
                defects,
                "Building manifest exceeds V4 catalog or content "
                + "protocol hard caps."
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
        DerivedRecapSelection latestSelection =
            await _store.SelectNthPreviousAsync(
                    lineage,
                    nthPrevious: 0,
                    cancellationToken
                )
                .ConfigureAwait(false);
        switch (latestSelection) {
            case DerivedRecapSelection.Selected latest:
                if (!lineageIndex.TryGetValue(
                        latest.Descriptor.SetAdmissionAnchor,
                        out int latestIndex
                    )
                    || admissionIndex >= latestIndex) {
                    AddBuildingDefect(
                        defects,
                        "Building admission is not strictly newer than "
                        + "the latest current-lineage Published set."
                    );
                }
                break;
            case DerivedRecapSelection.EmptyLineage:
                break;
            case DerivedRecapSelection.ExactPublishedSetInvalid invalid:
                foreach (RecapStructuralDefect defect in invalid.Defects) {
                    defects.Add(new DerivedRecapExecutionDefect(
                        defect.Code,
                        defect.Detail
                    ));
                }
                break;
            case DerivedRecapSelection.StoreUnavailable unavailable:
                defects.Add(new DerivedRecapExecutionDefect(
                    DerivedRecapExecutionDefectCodes.StoreUnavailable,
                    unavailable.Reason
                ));
                break;
            case DerivedRecapSelection.OrdinalUnavailable:
                defects.Add(new DerivedRecapExecutionDefect(
                    DerivedRecapExecutionDefectCodes.StoreUnavailable,
                    "Latest strict Published ordinal is unavailable."
                ));
                break;
            default:
                throw new InvalidOperationException(
                    "Unknown latest Published selection result."
                );
        }
        if (defects.Count != 0) {
            return new PreparedBuilding(
                defects,
                emptyInspections,
                emptyWindows
            );
        }

        var maintainPlans = new List<MaintainRecapBlockPlan>();
        foreach (RecapBlockPlan plan in building.Manifest.Blocks) {
            foreach (RecapFrozenPlanRawDefect defect
                     in RecapFrozenPlanRawValidator.ValidateBlock(
                         building.Manifest,
                         building.FrozenInputs,
                         lineage,
                         plan
                     )) {
                AddBuildingDefect(defects, defect.Detail);
            }
            if (plan is MaintainRecapBlockPlan maintainPlan) {
                maintainPlans.Add(maintainPlan);
            }
        }
        foreach (RecapPendingWindowDefect defect
                 in RecapPendingWindowPreparer
                     .ValidateFrozenRouteLimits(
                         maintainPlans,
                         _hardCaps
                     )) {
            AddLimitDefect(
                defects,
                defect.Detail
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
        var pendingRoutes = new List<PendingMaintainRoute>();
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
            pendingRoutes.Add(new PendingMaintainRoute(
                maintain,
                GetMaintainStart(building, maintain),
                next
            ));
        }
        if (defects.Count != 0) {
            return new PreparedBuilding(
                defects,
                inspections,
                emptyWindows
            );
        }

        if (pendingRoutes.Count != 0) {
            _testHooks.BeforePendingWindowFreeze?.Invoke();
        }
        PreparedRecapPendingWindows preparedWindows =
            RecapPendingWindowPreparer.Prepare(
                _engine,
                lineage.CapturedHead,
                pendingRoutes,
                _hardCaps,
                cancellationToken
            );
        foreach (RecapPendingWindowDefect defect
                 in preparedWindows.Defects) {
            AddLimitDefect(defects, defect.Detail);
        }
        return new PreparedBuilding(
            defects,
            inspections,
            preparedWindows.Windows
        );
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
                    .MaintainerUnavailable,
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
            RecapMaintainerStepResult step =
                await RecapMaintainerStepRunner.RunAsync(
                        maintainer,
                        maintain,
                        currentBlock,
                        window,
                        maintain.CatchUpThrough[nextEndpoint],
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (step is RecapMaintainerStepResult.MaintainerFailed
                failed) {
                return new DerivedRecapExecutionResult.BlockFailed(
                    building.Descriptor.SetAdmissionAnchor,
                    plan.RecapBlockId,
                    DerivedRecapExecutionDefectCodes.MaintainerFailed,
                    failed.Detail
                );
            }
            if (step is RecapMaintainerStepResult.ResultInvalid invalid) {
                return new DerivedRecapExecutionResult.BlockFailed(
                    building.Descriptor.SetAdmissionAnchor,
                    plan.RecapBlockId,
                    DerivedRecapExecutionDefectCodes
                        .MaintainerResultInvalid,
                    invalid.Detail
                );
            }
            DerivedRecapBlock candidate =
                ((RecapMaintainerStepResult.Succeeded)step).Candidate;
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
        EventAddress expected,
        EventAddress? observed
    ) => new(
        DerivedRecapExecutionDefectCodes.RawHeadChanged,
        $"Raw SessionJournal head changed during planning. Expected "
        + $"'{expected}', observed "
        + $"'{observed?.ToString() ?? "<none>"}'."
    );

    private static void AddLimitDefect(
        List<DerivedRecapExecutionDefect> defects,
        string detail
    ) => defects.Add(new(
        DerivedRecapExecutionDefectCodes.ExecutionLimitExceeded,
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
        => exception is RecapRawHeadChangedException
            or InvalidDataException
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
