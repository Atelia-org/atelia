using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal sealed record DerivedRecapBuildingExecutorTestHooks(
    Action? BeforePendingWindowFreeze = null,
    Action? AfterPendingWindowMaterialization = null
);

/// <summary>
/// Creates a new frozen Building from active planning inputs and repo-owned
/// planning limits, then delegates durable execution to the Building executor.
/// </summary>
internal sealed class DerivedRecapPlannerExecutor {
    private readonly SessionJournalReadView _engine;
    private readonly DerivedRecapStore _store;
    private readonly RecapPlanningInputs _inputs;
    private readonly RecapPlanningLimits _limits;
    private readonly IRecapBlockMaintainerRegistry _maintainers;
    private readonly DerivedRecapScheduleReader _scheduleReader;
    private readonly DerivedRecapBuildingInstaller _installer;
    private readonly DerivedRecapBuildingExecutor _buildingExecutor;
    private DerivedRecapPlanningDiagnostics? _lastPlanningDiagnostics;

    public DerivedRecapPlannerExecutor(
        SessionJournalReadView engine,
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
        SessionJournalReadView engine,
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
        _maintainers = maintainers
            ?? throw new ArgumentNullException(nameof(maintainers));
        ArgumentNullException.ThrowIfNull(hardCaps);
        ArgumentNullException.ThrowIfNull(testHooks);
        hardCaps.ValidatePlanningAuthority(inputs, limits);
        RequireSameBinding(store, engine);
        _scheduleReader = new DerivedRecapScheduleReader(
            engine,
            store,
            inputs,
            limits,
            hardCaps
        );
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
        SessionCurrentLineagePrefix lineage;
        DerivedRecapSelection selection;
        try {
            DerivedRecapLineageView view =
                DerivedRecapLineageView.Capture(
                    _store,
                    _engine,
                    cancellationToken
                );
            lineage = view.Prefix;
            selection = await view.SelectNthPreviousAsync(
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
        DerivedRecapScheduleReadResult scheduleRead =
            await _scheduleReader.ReadAsync(
                    baseline,
                    cancellationToken
                )
                .ConfigureAwait(false);
        switch (scheduleRead) {
            case DerivedRecapScheduleReadResult.NoBuild noBuild:
                SetExactScheduleDiagnostics(noBuild.Progress);
                return new DerivedRecapExecutionResult.NoBuild(
                    noBuild.Reason
                );
            case DerivedRecapScheduleReadResult.RawSafetyRejected
                rejected:
                Volatile.Write(
                    ref _lastPlanningDiagnostics,
                    new DerivedRecapPlanningDiagnostics
                        .RawSafetyRejected(
                            rejected.RawGrowthEventCount
                        )
                );
                return new DerivedRecapExecutionResult.Unavailable(
                    rejected.Defects
                );
            case DerivedRecapScheduleReadResult.Retryable retryable:
                return new DerivedRecapExecutionResult.Retryable(
                    retryable.Code,
                    retryable.Detail
                );
            case DerivedRecapScheduleReadResult.Unavailable unavailable:
                if (unavailable.Progress is { } progress) {
                    SetExactScheduleDiagnostics(progress);
                }
                return new DerivedRecapExecutionResult.Unavailable(
                    unavailable.Defects
                );
            case DerivedRecapScheduleReadResult.BeyondPrefix beyond:
                return new DerivedRecapExecutionResult.BeyondPrefix(
                    beyond.Stage,
                    beyond.Evidence
                );
        }

        var scheduleReady =
            (DerivedRecapScheduleReadResult.Ready)scheduleRead;
        SetExactScheduleDiagnostics(scheduleReady.Progress);
        SessionCurrentLineagePrefix lineage = scheduleReady.Lineage;
        PublishedRecapDescriptor? latest = scheduleReady.Latest;
        EventAddress emptyReplayStartExclusive =
            scheduleReady.EmptyReplayStartExclusive;
        SessionHistoryPlanningWindow provenPlanningWindow =
            scheduleReady.PlanningWindow;
        RecapSchedulingResult.Ready schedule = scheduleReady.Schedule;
        PublishedRecapSourceSnapshot? sourceSnapshot = null;
        Dictionary<RecapBlockId, DerivedRecapFrozenInput>
            sourceInputsById = [];
        RecapPriorContext sharedPriorContext =
            EmptyRecapPriorContext.Instance;

        if (latest is not null) {
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
            try {
                sharedPriorContext = BuildSharedPriorContext(
                    sourceSnapshot
                );
                sourceInputsById = sourceSnapshot.FrozenInputs
                    .ToDictionary(static input => input.RecapBlockId);
            }
            catch (Exception exception)
                when (IsAvailabilityException(exception)) {
                return Unavailable(
                    DerivedRecapExecutionDefectCodes
                        .PublishedSourceUnavailable,
                    exception.Message
                );
            }
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
                policyFacts,
                sharedPriorContext
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
        RecapCadenceBoundary selectedCadence =
            intentReady.Schedule.Cadence.AdmissionCandidates.Single(
                candidate => candidate.Address
                    == intentReady.Intent.SetAdmissionAnchor
            );
        Volatile.Write(
            ref _lastPlanningDiagnostics,
            new DerivedRecapPlanningDiagnostics.ExactSchedule(
                new RecapExactScheduleMeasurement(
                    intentReady.Schedule.Cadence
                        .HistoryUnitLoadEstimatorId,
                    intentReady.Schedule.Cadence.GrowthHistoryLoad,
                    intentReady.Schedule.Cadence
                        .GrowthHistoryUnitCount,
                    intentReady.Schedule.Cadence.RawGrowthEventCount,
                    selectedCadence.AbsorbedHistoryLoad,
                    selectedCadence.RecentHistoryLoad
                )
            )
        );

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
                provenPlanningWindow!,
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
        BuildingPlanReadResult existing;
        try {
            existing = await _store.ReadBuildingPlanAsync(
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
        if (existing is BuildingPlanReadResult.Invalid invalid) {
            return Unavailable(invalid.Defects);
        }
        if (existing is BuildingPlanReadResult.Available alreadyBuilding) {
            return await _buildingExecutor.ResumeAsync(
                    alreadyBuilding.Snapshot,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        DerivedRecapExecutionResult? maintainerUnavailable;
        try {
            maintainerUnavailable = ValidateMaintainerBindings(
                intentReady.Intent
            );
        }
        catch (Exception exception)
            when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes
                    .MaintainerUnavailable,
                exception.Message
            );
        }
        if (maintainerUnavailable is not null) {
            return maintainerUnavailable;
        }

        DerivedRecapSetManifest manifest = CreateManifest(
            intentReady.Intent,
            sourceSnapshot,
            provenPlanningWindow!
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
        BuildingDescriptor installedDescriptor;
        switch (created) {
            case CreateBuildingResult.Created installed:
                installedDescriptor = installed.Descriptor;
                break;
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
            case CreateBuildingResult.BeyondPrefix beyond:
                return new DerivedRecapExecutionResult.Retryable(
                    DerivedRecapExecutionDefectCodes.SourceChanged,
                    "Building installation lineage authority changed "
                    + "after bounded new-planning proof at "
                    + $"'{beyond.Evidence.RequiredAnchor}'."
                );
            case CreateBuildingResult.StoreUnavailable unavailable:
                return Unavailable(
                    DerivedRecapExecutionDefectCodes.StoreUnavailable,
                    unavailable.Reason
                );
            case CreateBuildingResult.InvalidPlan invalidPlan:
                return Unavailable(invalidPlan.Defects);
            default:
                throw new InvalidOperationException(
                    "Unknown Building creation result."
                );
        }

        // Do not execute from in-memory intent/source objects. The installed
        // Building is now the only recovery authority.
        return await _buildingExecutor.ResumeAsync(
                installedDescriptor,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private PreparedIntent PrepareIntent(
        RecapPlanIntentResult.IntentReady ready,
        PublishedRecapSourceSnapshot? sourceSnapshot,
        SessionHistoryPlanningWindow planningWindow,
        CancellationToken cancellationToken
    ) {
        var sourceFacts = new List<RecapSourceReplayFact>();
        Dictionary<RecapBlockId, DerivedRecapFrozenInput> inputs =
            sourceSnapshot?.FrozenInputs.ToDictionary(
                static input => input.RecapBlockId
            ) ?? [];
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
                    break;
                case RecapBlockPlanningDecision.Maintain {
                    Source: RecapPlanningMaintainSource.Empty empty
                } maintain:
                    break;
            }
        }

        if (planningWindow.ObservedRawHead
            != ready.Schedule.Facts.CapturedHead) {
            throw new InvalidDataException(
                "Raw head changed while planning exact replay seeds."
            );
        }
        var positions = new Dictionary<EventAddress, int> {
            [planningWindow.StartExclusive] = 0
        };
        for (int index = 0;
             index < planningWindow.RawAddresses.Count;
             index++) {
            positions.Add(
                planningWindow.RawAddresses[index],
                index + 1
            );
        }
        var costs = new List<RecapPlannedStepCost>();
        long totalRawEvents = 0;
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
                if (!positions.TryGetValue(previous, out int previousIndex)
                    || !positions.TryGetValue(endpoint, out int endpointIndex)
                    || endpointIndex <= previousIndex) {
                    throw new InvalidDataException(
                        "Planned replay step is outside the proven raw window."
                    );
                }
                int rawEventCount = endpointIndex - previousIndex;
                if (rawEventCount > _limits.MaxRawEventsPerStep) {
                    throw new InvalidDataException(
                        "Planned replay step exceeds its raw-event limit."
                    );
                }
                totalRawEvents = checked(totalRawEvents + rawEventCount);
                costs.Add(new RecapPlannedStepCost(
                    maintain.RecapBlockId,
                    previous,
                    endpoint,
                    rawEventCount
                ));
                previous = endpoint;
            }
        }
        if (totalRawEvents > _limits.MaxRawEventsPerBuild) {
            throw new InvalidDataException(
                "Planned replay routes exceed the per-build raw-event limit."
            );
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

    private static RecapPriorContext BuildSharedPriorContext(
        PublishedRecapSourceSnapshot sourceSnapshot
    ) {
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        IReadOnlyList<RecapBlockPlan> sourcePlans =
            sourceSnapshot.Publication.FrozenPlanSnapshot.Blocks;
        IReadOnlyList<DerivedRecapFrozenInput> sourceInputs =
            sourceSnapshot.FrozenInputs;
        if (sourcePlans.Count != sourceInputs.Count) {
            throw new InvalidDataException(
                "Published source frozen plan and inputs have different "
                + "block counts."
            );
        }

        var draft = new ContextHeaderPackDraft(
            new ContextHeaderPack()
        );
        for (int index = 0; index < sourcePlans.Count; index++) {
            RecapBlockPlan sourcePlan = sourcePlans[index];
            DerivedRecapFrozenInput sourceInput = sourceInputs[index];
            if (sourcePlan.RecapBlockId != sourceInput.RecapBlockId
                || sourcePlan.Target != sourceInput.Target) {
                throw new InvalidDataException(
                    "Published source frozen plan and inputs do not have "
                    + $"the same block at index {index}."
                );
            }
            draft.UpsertBlock(sourcePlan.Target, sourceInput.Content);
        }
        return new InlineRecapPriorContext(
            sourceSnapshot.Source.SetAdmissionAnchor,
            draft.Build().Render()
        );
    }

    private DerivedRecapSetManifest CreateManifest(
        RecapPlanningPolicyDecision.Build intent,
        PublishedRecapSourceSnapshot? sourceSnapshot,
        SessionHistoryPlanningWindow planningWindow
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
                        inputs[inherit.RecapBlockId]
                            .AbsorbedThroughSetups,
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
                        _inputs.OrderedCatalog[index]
                            .MaintainerCapabilityFingerprint,
                        maintain.Source switch {
                            RecapPlanningMaintainSource.Existing existing =>
                                new ExistingRecapMaintainSource(
                                    existing.Source.SourceSetAnchor,
                                    inputs[maintain.RecapBlockId]
                                        .AbsorbedThroughSetups,
                                    existing.Source
                                        .SourcePublicationEnvelopeSha256,
                                    inputs[maintain.RecapBlockId]
                                        .PayloadSha256
                                ),
                            RecapPlanningMaintainSource.Empty empty =>
                                new EmptyRecapMaintainSource(
                                    empty.ReplayStartExclusive,
                                    SetupsAt(
                                        planningWindow,
                                        empty.ReplayStartExclusive
                                    )
                                ),
                            _ => throw new InvalidDataException(
                                "Unsupported Maintain source intent."
                            )
                        },
                        Array.AsReadOnly(
                            maintain.CatchUpThrough
                                .Select(endpoint =>
                                    new RecapReplayBoundary(
                                        endpoint,
                                        SetupsAt(planningWindow, endpoint)
                                    ))
                                .ToArray()
                        ),
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
            SetupsAt(planningWindow, intent.SetAdmissionAnchor),
            plans
        );
    }

    private static SessionContextAnchorSetupReferences SetupsAt(
        SessionHistoryPlanningWindow window,
        EventAddress address
    ) {
        if (window.StartExclusive == address) {
            return window.StartSetups;
        }
        if (window.ReplaySafeBoundarySetups.TryGetValue(
                address,
                out SessionContextAnchorSetupReferences? setups
            )) {
            return setups;
        }
        throw new InvalidDataException(
            $"Raw planning window has no replay-safe setup authority for '{address}'."
        );
    }

    private void SetExactScheduleDiagnostics(
        DerivedRecapPlanningProgressSnapshot progress
    ) => Volatile.Write(
        ref _lastPlanningDiagnostics,
        new DerivedRecapPlanningDiagnostics.ExactSchedule(
            progress.Measurement
        )
    );

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

    private static DerivedRecapExecutionResult SelectionUnavailable(
        DerivedRecapSelection selection
    ) => selection switch {
        DerivedRecapSelection.ExactPublishedSetInvalid invalid =>
            Unavailable(invalid.Defects),
        DerivedRecapSelection.BeyondPrefix beyond =>
            new DerivedRecapExecutionResult.BeyondPrefix(
                DerivedRecapBeyondPrefixStage
                    .NewPlanningSourceAnchor,
                beyond.Evidence
            ),
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

    private DerivedRecapExecutionResult? ValidateMaintainerBindings(
        RecapPlanningPolicyDecision.Build intent
    ) {
        for (int index = 0; index < intent.Blocks.Count; index++) {
            if (intent.Blocks[index]
                is not RecapBlockPlanningDecision.Maintain) {
                continue;
            }
            RecapBlockCatalogEntry entry =
                _inputs.OrderedCatalog[index];
            if (!_maintainers.TryResolve(
                    entry.MaintainerId,
                    entry.Target,
                    entry.MaintainerCapabilityFingerprint,
                    out IRecapBlockMaintainer? maintainer
                )
                || !string.Equals(
                    maintainer.Id,
                    entry.MaintainerId,
                    StringComparison.Ordinal
                )
                || maintainer.Target != entry.Target
                || !string.Equals(
                    maintainer.CapabilityFingerprint,
                    entry.MaintainerCapabilityFingerprint,
                    StringComparison.Ordinal
                )) {
                return Unavailable(
                    DerivedRecapExecutionDefectCodes
                        .MaintainerUnavailable,
                    "Maintainer registry cannot resolve the exact "
                    + "binding for recap block "
                    + $"'{entry.RecapBlockId}'."
                );
            }
        }
        return null;
    }

    private static void RequireSameBinding(
        DerivedRecapStore store,
        SessionJournalReadView engine
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
                "DerivedRecap Planner, Store, and SessionJournalReadView "
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
internal sealed class DerivedRecapBuildingExecutor {
    private readonly SessionJournalReadView _engine;
    private readonly DerivedRecapStore _store;
    private readonly IRecapBlockMaintainerRegistry _maintainers;
    private readonly RecapProtocolHardCaps _hardCaps;
    private readonly DerivedRecapPublisher _publisher;
    private readonly DerivedRecapBuildingExecutorTestHooks _testHooks;

    public DerivedRecapBuildingExecutor(
        SessionJournalReadView engine,
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
        SessionJournalReadView engine,
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
        BuildingPlanReadResult read =
            await _store.ReadBuildingPlanAsync(
                    setAdmissionAnchor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return read switch {
            BuildingPlanReadResult.Available available =>
                await ResumeCoreAsync(
                        available.Snapshot,
                        expectedDescriptor: null,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
            BuildingPlanReadResult.Invalid invalid =>
                Unavailable(invalid.Defects),
            BuildingPlanReadResult.Missing => Unavailable(
                DerivedRecapExecutionDefectCodes.BuildingInvalid,
                $"Building '{setAdmissionAnchor}' does not exist."
            ),
            _ => throw new InvalidOperationException(
                "Unknown Building plan read result."
            )
        };
    }

    public async ValueTask<DerivedRecapExecutionResult> ResumeAsync(
        BuildingDescriptor expectedDescriptor,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedDescriptor);
        if (expectedDescriptor.RefId != _store.RefId) {
            throw new ArgumentException(
                "Building descriptor belongs to another RefId.",
                nameof(expectedDescriptor)
            );
        }
        BuildingPlanReadResult read =
            await _store.ReadBuildingPlanAsync(
                    expectedDescriptor.SetAdmissionAnchor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return read switch {
            BuildingPlanReadResult.Available available =>
                await ResumeCoreAsync(
                        available.Snapshot,
                        expectedDescriptor,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
            BuildingPlanReadResult.Invalid invalid =>
                Unavailable(invalid.Defects),
            BuildingPlanReadResult.Missing =>
                new DerivedRecapExecutionResult.Retryable(
                    DerivedRecapExecutionDefectCodes.SourceChanged,
                    $"Expected Building '{expectedDescriptor}' "
                    + "disappeared before frozen Resume."
                ),
            _ => throw new InvalidOperationException(
                "Unknown Building plan read result."
            )
        };
    }

    public async ValueTask<DerivedRecapExecutionResult> ResumeAsync(
        BuildingPlanSnapshot plan,
        CancellationToken cancellationToken = default
    ) => await ResumeCoreAsync(
            plan ?? throw new ArgumentNullException(nameof(plan)),
            plan.Descriptor,
            cancellationToken
        )
        .ConfigureAwait(false);

    private async ValueTask<DerivedRecapExecutionResult>
        ResumeCoreAsync(
        BuildingPlanSnapshot plan,
        BuildingDescriptor? expectedDescriptor,
        CancellationToken cancellationToken
    ) {
        EventAddress expectedRawHead = _engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Frozen Resume requires a non-empty SessionJournal."
            );
        SessionCurrentLineagePrefix prefix =
            _engine.ReadLineagePrefixAt(
                expectedRawHead,
                RecapFrozenPlanBarrier.ProofPrefixHeaderCount(
                    _hardCaps
                ),
                cancellationToken
            );
        RecapFrozenPlanBarrierResult barrier;
        try {
            barrier = await RecapFrozenPlanBarrier.ProveAsync(
                    _engine,
                    _store,
                    plan.Manifest,
                    prefix,
                    expectedRawHead,
                    _hardCaps,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (RecapRawHeadChangedException changed) {
            return RetryableRawHead(changed.Expected, changed.Observed);
        }
        catch (Exception exception) when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.RawPlanningUnavailable,
                exception.Message
            );
        }
        if (barrier.BeyondPrefix is { } beyond) {
            return new DerivedRecapExecutionResult.BeyondPrefix(
                DerivedRecapBeyondPrefixStage.ResumePendingWindow,
                beyond
            );
        }
        if (barrier.Defects.Count != 0) {
            return new DerivedRecapExecutionResult.Unavailable([
                .. barrier.Defects.Select(defect =>
                    new DerivedRecapExecutionDefect(
                        defect.Kind switch {
                            RecapFrozenPlanBarrierDefectKind
                                .ExecutionLimit =>
                                DerivedRecapExecutionDefectCodes
                                    .ExecutionLimitExceeded,
                            RecapFrozenPlanBarrierDefectKind
                                .StoreUnavailable =>
                                DerivedRecapExecutionDefectCodes
                                    .StoreUnavailable,
                            _ => DerivedRecapExecutionDefectCodes
                                .BuildingInvalid
                        },
                        defect.Detail
                    ))
            ]);
        }
        ResumeLineagePreflight lineagePreflight;
        try {
            lineagePreflight = await ProveResumeLineageAsync(
                    plan.Manifest,
                    expectedRawHead,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (RecapRawHeadChangedException changed) {
            return RetryableRawHead(changed.Expected, changed.Observed);
        }
        catch (Exception exception) when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.RawPlanningUnavailable,
                exception.Message
            );
        }
        if (lineagePreflight
                is ResumeLineagePreflight.Unavailable unavailableLineage) {
            return unavailableLineage.Result;
        }
        SessionCurrentLineagePrefix provenLineage =
            ((ResumeLineagePreflight.Available)lineagePreflight).Lineage;
        PreparedRecapPublication publication;
        try {
            publication = _publisher.Prepare(
                plan.Handle,
                expectedRawHead,
                cancellationToken
            );
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
                DerivedRecapExecutionDefectCodes.RawPlanningUnavailable,
                exception.Message
            );
        }
        BuildingReadResult read;
        try {
            read = await _store.ReadBuildingAsync(
                    plan.Handle,
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
                expectedDescriptor is not null
                && available.Snapshot.Descriptor
                    != expectedDescriptor
                    ? RetryableBuildingChanged(
                        expectedDescriptor,
                        available.Snapshot.Descriptor
                    )
                    : await ExecuteAndPublishAsync(
                            available.Snapshot,
                            plan.Handle,
                            barrier.PendingWindowProofs,
                            provenLineage,
                            publication,
                            cancellationToken
                        )
                        .ConfigureAwait(false),
            BuildingReadResult.Invalid invalid =>
                Unavailable(invalid.Defects),
            BuildingReadResult.Missing =>
                expectedDescriptor is null
                    ? Unavailable(
                        DerivedRecapExecutionDefectCodes.BuildingInvalid,
                        $"Building '{plan.Descriptor.SetAdmissionAnchor}' does not exist."
                    )
                    : new DerivedRecapExecutionResult.Retryable(
                        DerivedRecapExecutionDefectCodes.SourceChanged,
                        $"Expected Building '{expectedDescriptor}' "
                        + "disappeared before frozen Resume."
                    ),
            _ => throw new InvalidOperationException(
                "Unknown Building read result."
            )
        };
    }

    private async ValueTask<ResumeLineagePreflight>
        ProveResumeLineageAsync(
        DerivedRecapSetManifest manifest,
        EventAddress expectedRawHead,
        CancellationToken cancellationToken
    ) {
        DerivedRecapLineageView lineageView =
            DerivedRecapLineageView.Capture(
                _store,
                _engine,
                cancellationToken
            );
        if (lineageView.CapturedHead != expectedRawHead) {
            throw new RecapRawHeadChangedException(
                expectedRawHead,
                lineageView.CapturedHead
            );
        }
        SessionCurrentLineagePrefix lineage = lineageView.Prefix;
        Dictionary<EventAddress, int> lineageIndex =
            lineage.HeadToOldest
                .Select((node, index) => (node.Address, index))
                .ToDictionary(
                    static pair => pair.Address,
                    static pair => pair.index
                );
        if (!lineageIndex.TryGetValue(
                manifest.SetAdmissionAnchor,
                out int admissionIndex
            )) {
            return Failed(
                DerivedRecapExecutionDefectCodes.BuildingInvalid,
                "Building admission anchor is outside current raw lineage."
            );
        }

        DerivedRecapSelection latestSelection =
            await lineageView.SelectNthPreviousAsync(
                    nthPrevious: 0,
                    cancellationToken
                )
                .ConfigureAwait(false);
        ResumeLineagePreflight? failed = latestSelection switch {
            DerivedRecapSelection.Selected latest
                when !lineageIndex.TryGetValue(
                        latest.Descriptor.SetAdmissionAnchor,
                        out int latestIndex
                    )
                    || admissionIndex >= latestIndex => Failed(
                        DerivedRecapExecutionDefectCodes.BuildingInvalid,
                        "Building admission is not strictly newer than "
                        + "the latest current-lineage Published set."
                    ),
            DerivedRecapSelection.Selected => null,
            DerivedRecapSelection.EmptyLineage => null,
            DerivedRecapSelection.ExactPublishedSetInvalid invalid =>
                new ResumeLineagePreflight.Unavailable(
                    Unavailable(invalid.Defects)
                ),
            DerivedRecapSelection.BeyondPrefix beyond => Failed(
                DerivedRecapExecutionDefectCodes.BuildingInvalid,
                "Exact Building content contradicted its completed "
                + "metadata/header proof at "
                + $"'{beyond.Evidence.RequiredAnchor}'."
            ),
            DerivedRecapSelection.StoreUnavailable unavailable => Failed(
                DerivedRecapExecutionDefectCodes.StoreUnavailable,
                unavailable.Reason
            ),
            DerivedRecapSelection.OrdinalUnavailable => Failed(
                DerivedRecapExecutionDefectCodes.StoreUnavailable,
                "Latest strict Published ordinal is unavailable."
            ),
            _ => throw new InvalidOperationException(
                "Unknown latest Published selection result."
            )
        };
        EventAddress observedHead = _engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Frozen Resume requires a non-empty SessionJournal."
            );
        if (observedHead != expectedRawHead) {
            throw new RecapRawHeadChangedException(
                expectedRawHead,
                observedHead
            );
        }
        return failed
            ?? new ResumeLineagePreflight.Available(lineage);

        static ResumeLineagePreflight.Unavailable Failed(
            string code,
            string detail
        ) => new(Unavailable(code, detail));
    }

    private static DerivedRecapExecutionResult.Retryable
        RetryableBuildingChanged(
        BuildingDescriptor expected,
        BuildingDescriptor? observed
    ) => new(
        DerivedRecapExecutionDefectCodes.SourceChanged,
        $"Frozen Building changed before Resume. Expected "
        + $"'{expected}', observed "
        + (observed is null ? "no exact Building." : $"'{observed}'.")
    );

    private async ValueTask<DerivedRecapExecutionResult>
        ExecuteAndPublishAsync(
        BuildingSnapshot building,
        BuildingPlanHandle handle,
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            RecapPendingWindowProofAuthority
        > pendingWindowProofs,
        SessionCurrentLineagePrefix provenLineage,
        PreparedRecapPublication publication,
        CancellationToken cancellationToken
    ) {
        PreparedBuilding prepared;
        try {
            prepared = await PrepareBuildingAsync(
                    building,
                    handle,
                    pendingWindowProofs,
                    provenLineage,
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
        if (prepared.BeyondPrefix is { } beyondPrefix) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.BuildingInvalid,
                "Exact Building content contradicted its completed "
                + "metadata/header proof at "
                + $"'{beyondPrefix.RequiredAnchor}'."
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
                            handle,
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
                        publication,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            switch (publishability) {
                case RecapPublishability.Publishable:
                    break;
                case RecapPublishability.AlreadyPublished already:
                    return new DerivedRecapExecutionResult.Published(
                        already.Descriptor
                    );
                case RecapPublishability.SourceChanged changed:
                    return RetryableBuildingChanged(
                        changed.Expected,
                        changed.Observed
                    );
                case RecapPublishability.NotPublishable notPublishable:
                    return Unavailable(notPublishable.Defects);
                case RecapPublishability.BeyondPrefix beyond:
                    return new DerivedRecapExecutionResult.Retryable(
                        DerivedRecapExecutionDefectCodes.SourceChanged,
                        "Publishability lineage authority changed after "
                        + "the completed Resume proof at "
                        + $"'{beyond.Evidence.RequiredAnchor}'."
                    );
                case RecapPublishability.StoreUnavailable unavailable:
                    return Unavailable(
                        DerivedRecapExecutionDefectCodes.StoreUnavailable,
                        unavailable.Reason
                    );
                default:
                    throw new InvalidOperationException(
                        "Unknown Recap publishability result."
                    );
            }
            PublishRecapResult published =
                await _publisher.PublishAsync(
                        publication,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            return published switch {
                PublishRecapResult.Published success =>
                    new DerivedRecapExecutionResult.Published(
                        success.Descriptor
                    ),
                PublishRecapResult.AlreadyPublished already =>
                    new DerivedRecapExecutionResult.Published(
                        already.Descriptor
                    ),
                PublishRecapResult.SourceChanged changed =>
                    RetryableBuildingChanged(
                        changed.Expected,
                        changed.Observed
                    ),
                PublishRecapResult.NotPublishable notPublishable =>
                    Unavailable(notPublishable.Defects),
                PublishRecapResult.BeyondPrefix beyond =>
                    new DerivedRecapExecutionResult.Retryable(
                        DerivedRecapExecutionDefectCodes.SourceChanged,
                        "Publication lineage authority changed after "
                        + "the completed Resume proof at "
                        + $"'{beyond.Evidence.RequiredAnchor}'."
                    ),
                PublishRecapResult.StoreUnavailable unavailable =>
                    Unavailable(
                        DerivedRecapExecutionDefectCodes.StoreUnavailable,
                        unavailable.Reason
                    ),
                PublishRecapResult.RawHeadChanged changed =>
                    RetryableRawHead(
                        changed.Expected,
                        changed.Observed
                    ),
                _ => throw new InvalidOperationException(
                    "Unknown Recap publish result."
                )
            };
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
        BuildingPlanHandle handle,
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            RecapPendingWindowProofAuthority
        > pendingWindowProofs,
        SessionCurrentLineagePrefix provenLineage,
        CancellationToken cancellationToken
    ) {
        var defects = new List<DerivedRecapExecutionDefect>();
        var emptyInspections =
            new Dictionary<RecapBlockId, BuildingBlockInspection>();
        var emptyWindows = new Dictionary<
            (RecapBlockId, int),
            SessionHistoryPlanningWindow
        >();
        ArgumentNullException.ThrowIfNull(provenLineage);
        EventAddress observedHead = _engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Frozen Resume requires a non-empty SessionJournal."
            );
        if (observedHead != provenLineage.CapturedHead) {
            throw new RecapRawHeadChangedException(
                provenLineage.CapturedHead,
                observedHead
            );
        }
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

        var maintainPlans = new List<MaintainRecapBlockPlan>();
        foreach (RecapBlockPlan plan in building.Manifest.Blocks) {
            // The frozen barrier authenticated all setup authority before
            // Building components were read. This phase validates only the
            // component-dependent structure and must not re-walk headers.
            foreach (RecapFrozenPlanRawDefect defect
                     in RecapFrozenPlanRawValidator
                         .ValidateInputDependentBlock(
                         building.Manifest,
                         building.FrozenInputs,
                         provenLineage,
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
                        handle,
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
            bool artifactUnavailable = false;
            if (inspection.Final
                    is FinalRecapBlockHealth.Unavailable finalUnavailable) {
                AddStoreDefects(defects, finalUnavailable.Defects);
                artifactUnavailable = true;
            }
            if (plan is MaintainRecapBlockPlan
                && inspection.Checkpoint
                    is RollingRecapCheckpointHealth.Unavailable
                        checkpointUnavailable) {
                AddStoreDefects(defects, checkpointUnavailable.Defects);
                artifactUnavailable = true;
            }
            if (artifactUnavailable) {
                continue;
            }
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
                next == 0
                    ? GetMaintainStart(building, maintain)
                    : maintain.CatchUpBoundaries[next - 1].Address,
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
                provenLineage.CapturedHead,
                pendingRoutes,
                _hardCaps,
                pendingWindowProofs,
                cancellationToken
            );
        _testHooks.AfterPendingWindowMaterialization?.Invoke();
        if (preparedWindows.BeyondPrefix is { } beyondPrefix) {
            return new PreparedBuilding(
                defects,
                inspections,
                emptyWindows,
                beyondPrefix
            );
        }
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
        BuildingPlanHandle handle,
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
        if (inspection.Final
                is FinalRecapBlockHealth.Unavailable finalUnavailable) {
            return Unavailable(finalUnavailable.Defects);
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
                            handle,
                            inspection,
                    candidate,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var maintain = (MaintainRecapBlockPlan)plan;
        if (inspection.Checkpoint
                is RollingRecapCheckpointHealth.Unavailable
                    checkpointUnavailable) {
            return Unavailable(checkpointUnavailable.Defects);
        }
        if (!_maintainers.TryResolve(
                maintain.MaintainerId,
                maintain.Target,
                maintain.MaintainerCapabilityFingerprint,
                out IRecapBlockMaintainer? maintainer
            )
            || !string.Equals(
                maintainer.Id,
                maintain.MaintainerId,
                StringComparison.Ordinal
            )
            || maintainer.Target != maintain.Target
            || !string.Equals(
                maintainer.CapabilityFingerprint,
                maintain.MaintainerCapabilityFingerprint,
                StringComparison.Ordinal
            )) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes
                    .MaintainerUnavailable,
                $"Maintainer binding for '{plan.RecapBlockId}' "
                + "is unavailable."
            );
        }

        DerivedRecapBlock? currentBlock = null;
        int nextEndpoint = 0;
        BuildingBlockWriteAuthority writeAuthority =
            inspection.WriteAuthority;
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

        while (nextEndpoint < maintain.CatchUpBoundaries.Count) {
            cancellationToken.ThrowIfCancellationRequested();
            SessionHistoryPlanningWindow window =
                windows[(plan.RecapBlockId, nextEndpoint)];
            RecapMaintainerStepResult step =
                await RecapMaintainerStepRunner.RunAsync(
                        maintainer,
                        maintain,
                        currentBlock,
                        window,
                        maintain.CatchUpBoundaries[nextEndpoint].Address,
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
                        writeAuthority,
                        candidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            switch (write) {
                case CheckpointWriteResult.Updated updated:
                    currentBlock = candidate;
                    nextEndpoint++;
                    writeAuthority = updated.WriteAuthority;
                    break;
                case CheckpointWriteResult.AlreadyCurrent current:
                    currentBlock = candidate;
                    nextEndpoint++;
                    writeAuthority = current.WriteAuthority;
                    break;
                case CheckpointWriteResult.Stale:
                    BuildingBlockInspection refreshed =
                        await _store.InspectBuildingBlockAsync(
                                handle,
                                plan.RecapBlockId,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    if (refreshed.Final
                        is FinalRecapBlockHealth.Healthy) {
                        return null;
                    }
                    if (refreshed.Final
                            is FinalRecapBlockHealth.Unavailable
                                refreshedFinalUnavailable) {
                        return Unavailable(
                            refreshedFinalUnavailable.Defects
                        );
                    }
                    if (refreshed.Checkpoint
                            is RollingRecapCheckpointHealth.Unavailable
                                refreshedCheckpointUnavailable) {
                        return Unavailable(
                            refreshedCheckpointUnavailable.Defects
                        );
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
                    currentBlock = advanced.Block;
                    nextEndpoint = advanced.EndpointIndex + 1;
                    inspection = refreshed;
                    writeAuthority = refreshed.WriteAuthority;
                    break;
                case CheckpointWriteResult.Unavailable unavailable:
                    return Unavailable(unavailable.Defects);
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
                    handle,
                    plan.RecapBlockId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (finalInspection.Final is FinalRecapBlockHealth.Healthy) {
            return null;
        }
        return await EnsureFinalAsync(
                building.Descriptor,
                handle,
                finalInspection,
                currentBlock,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<DerivedRecapExecutionResult?> EnsureFinalAsync(
        BuildingDescriptor building,
        BuildingPlanHandle handle,
        BuildingBlockInspection inspection,
        DerivedRecapBlock candidate,
        CancellationToken cancellationToken
    ) {
        FinalBlockWriteResult write =
            await _store.EnsureFinalBlockAsync(
                    inspection.WriteAuthority,
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
                            handle,
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
            case FinalBlockWriteResult.Unavailable unavailable:
                return Unavailable(unavailable.Defects);
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

    private static void AddStoreDefects(
        List<DerivedRecapExecutionDefect> defects,
        IReadOnlyList<RecapStructuralDefect> storeDefects
    ) {
        foreach (RecapStructuralDefect defect in storeDefects) {
            defects.Add(new DerivedRecapExecutionDefect(
                defect.Code,
                defect.Detail
            ));
        }
    }

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
        SessionJournalReadView engine
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
                "DerivedRecap Planner, Store, and SessionJournalReadView "
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
        > Windows,
        SessionCurrentLineageBeyondPrefix? BeyondPrefix = null
    );

    private abstract record ResumeLineagePreflight {
        private ResumeLineagePreflight() { }

        internal sealed record Available(
            SessionCurrentLineagePrefix Lineage
        ) : ResumeLineagePreflight;

        internal sealed record Unavailable(
            DerivedRecapExecutionResult Result
        ) : ResumeLineagePreflight;
    }
}
