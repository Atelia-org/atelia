using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public static class RecapPlanEvaluator {
    public static RecapHeaderPrefilterResult EvaluateHeaderPrefilter(
        RecapPlanningInputs inputs,
        SessionCurrentLineageSnapshot lineage,
        EventAddress? cadenceBaseline
    ) {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(lineage);
        var defects = new List<RecapPlanDefect>();
        Dictionary<EventAddress, int> lineageIndex =
            ValidateLineage(
                lineage.CapturedHead,
                lineage.HeadToRoot,
                defects
            );
        if (defects.Count != 0) {
            return HeaderUnavailable(defects);
        }
        int rawGrowthEventUpperBound;
        if (cadenceBaseline is { } exactBaseline) {
            if (!lineageIndex.TryGetValue(
                    exactBaseline,
                    out rawGrowthEventUpperBound
                )) {
                return HeaderUnavailable(
                    RecapPlanDefectCodes.CadenceBaselineInvalid,
                    "Cadence baseline is outside the captured raw "
                    + "lineage."
                );
            }
        }
        else {
            // Fresh bootstrap has not resolved its exact SessionCreated
            // start yet. The whole lineage is a conservative upper bound:
            // it can safely prove only that the HistoryUnit threshold is
            // unreachable, never that a Build or raw-limit failure exists.
            rawGrowthEventUpperBound = lineage.HeadToRoot.Count;
        }
        if (rawGrowthEventUpperBound
            < inputs.Cadence.BuildThresholdUnitCount) {
            return new RecapHeaderPrefilterResult.NoBuild(
                RecapPlanReasons.BelowCadenceThreshold,
                rawGrowthEventUpperBound
            );
        }
        return new RecapHeaderPrefilterResult.ExactEvaluationRequired(
            rawGrowthEventUpperBound
        );
    }

    public static RecapSchedulingResult EvaluateSchedule(
        RecapPlanningInputs inputs,
        RecapPlanningLimits limits,
        RecapSchedulingFacts facts
    ) {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(facts);
        List<RecapPlanDefect> defects = ValidateSchedulingFacts(
            facts,
            out Dictionary<EventAddress, int> lineage,
            out int baselineCompletedUnitCount
        );
        if (defects.Count != 0) {
            return ScheduleUnavailable(defects);
        }

        int rawGrowthEventCount = lineage[facts.CadenceBaseline];
        int growthHistoryUnitCount =
            facts.HistoryWindow.TotalHistoryUnitCount
            - baselineCompletedUnitCount;
        var measurement = new RecapExactScheduleMeasurement(
            growthHistoryUnitCount,
            rawGrowthEventCount
        );
        if (rawGrowthEventCount
            > limits.MaxRawGrowthEventCount) {
            return new RecapSchedulingResult.Unavailable(
                [
                    new RecapPlanDefect(
                        RecapPlanDefectCodes
                            .MaxRawGrowthEventCountExceeded,
                        $"Raw growth after cadence baseline is "
                        + $"{rawGrowthEventCount}; limit is "
                        + $"{limits.MaxRawGrowthEventCount}."
                    )
                ],
                measurement
            );
        }
        if (growthHistoryUnitCount
            < inputs.Cadence.BuildThresholdUnitCount) {
            return new RecapSchedulingResult.NoBuild(
                RecapPlanReasons.BelowCadenceThreshold,
                measurement
            );
        }

        int baselineIndex = lineage[facts.CadenceBaseline];
        RecapCadenceBoundary[] candidates = [
            .. facts.HistoryWindow.ReplaySafeBoundaries
                .Where(boundary =>
                    lineage[boundary.Address] < baselineIndex)
                .Select(boundary => new RecapCadenceBoundary(
                    boundary.Address,
                    boundary.CompletedUnitCount
                    - baselineCompletedUnitCount
                ))
                .Where(boundary =>
                    boundary.HistoryUnitCountSinceBaseline
                        >= inputs.Cadence
                            .RecapBuildIntervalUnitCount
                    && growthHistoryUnitCount
                       - boundary.HistoryUnitCountSinceBaseline
                       >= inputs.Cadence
                           .MinimumRecentHistoryUnitCount)
        ];
        if (candidates.Length == 0) {
            return new RecapSchedulingResult.NoBuild(
                RecapPlanReasons.AwaitingReplaySafeAdmission,
                measurement
            );
        }
        var cadence = new RecapCadenceFacts(
            facts.CadenceBaseline,
            growthHistoryUnitCount,
            rawGrowthEventCount,
            candidates
        );
        return new RecapSchedulingResult.Ready(
            inputs,
            limits,
            facts,
            cadence
        );
    }

    public static RecapPlanIntentResult EvaluateIntent(
        RecapSchedulingResult.Ready schedule,
        RecapPolicyFacts policyFacts
    ) {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(policyFacts);
        List<RecapPlanDefect> sourceDefects =
            ValidateSourceIntents(schedule, policyFacts);
        if (sourceDefects.Count != 0) {
            return IntentUnavailable(sourceDefects);
        }

        RecapPlanningPolicyDecision decision =
            schedule.Inputs.Policy.Decide(
            new RecapPlanningPolicyContext(
                schedule.Inputs,
                schedule.Limits,
                schedule.Facts,
                schedule.Cadence,
                policyFacts
            )
        );
        if (decision is null) {
            return IntentUnavailable(
                RecapPlanDefectCodes.PolicyDecisionInvalid,
                "Planning policy returned null."
            );
        }
        if (decision is RecapPlanningPolicyDecision.NoBuild noBuild) {
            return string.IsNullOrWhiteSpace(noBuild.Reason)
                ? IntentUnavailable(
                    RecapPlanDefectCodes.PolicyDecisionInvalid,
                    "Planning policy returned an empty NoBuild reason."
                )
                : new RecapPlanIntentResult.NoBuild(noBuild.Reason);
        }
        if (decision
            is RecapPlanningPolicyDecision.Unavailable unavailable) {
            if (unavailable.Defects.Count == 0
                || unavailable.Defects.Any(static defect =>
                    defect is null
                    || string.IsNullOrWhiteSpace(defect.Code)
                    || string.IsNullOrWhiteSpace(defect.Detail))) {
                return IntentUnavailable(
                    RecapPlanDefectCodes.PolicyDecisionInvalid,
                    "Planning policy returned malformed unavailable defects."
                );
            }
            return IntentUnavailable(unavailable.Defects);
        }

        var build = (RecapPlanningPolicyDecision.Build)decision;
        List<RecapPlanDefect> defects = ValidateIntent(
            schedule,
            policyFacts,
            build
        );
        return defects.Count == 0
            ? new RecapPlanIntentResult.IntentReady(schedule, build)
            : IntentUnavailable(defects);
    }

    public static RecapPlanResult ValidatePlan(
        RecapPlanIntentResult.IntentReady ready,
        RecapPlanPreflightFacts preflight
    ) {
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(preflight);
        List<RecapPlanDefect> defects =
            ValidatePreflight(ready, preflight);
        return defects.Count == 0
            ? new RecapPlanResult.PlanReady(
                ready.Schedule,
                ready.Intent,
                preflight
            )
            : PlanUnavailable(defects);
    }

    private static List<RecapPlanDefect> ValidateSchedulingFacts(
        RecapSchedulingFacts facts,
        out Dictionary<EventAddress, int> lineage,
        out int baselineCompletedUnitCount
    ) {
        var defects = new List<RecapPlanDefect>();
        baselineCompletedUnitCount = 0;
        lineage = ValidateLineage(
            facts.CapturedHead,
            facts.HeadToRoot,
            defects
        );
        if (defects.Count != 0) {
            return defects;
        }

        RecapHistoryWindowFacts window = facts.HistoryWindow;
        if (!lineage.TryGetValue(
                window.StartExclusive,
                out int startIndex
            )) {
            Add(
                defects,
                RecapPlanDefectCodes.PlanningFactsInvalid,
                "History window start is outside the raw lineage."
            );
            return defects;
        }
        var boundaryAddresses = new HashSet<EventAddress>();
        int previousLineageIndex = startIndex;
        int previousCompletedUnitCount = 0;
        foreach (SessionHistoryPlanningBoundary? boundary
                 in window.ReplaySafeBoundaries) {
            if (boundary is null
                || !boundaryAddresses.Add(boundary.Address)
                || !lineage.TryGetValue(
                    boundary.Address,
                    out int boundaryIndex
                )
                || boundaryIndex >= previousLineageIndex
                || boundary.CompletedUnitCount < 0
                || boundary.CompletedUnitCount
                    > window.TotalHistoryUnitCount
                || boundary.CompletedUnitCount
                    < previousCompletedUnitCount) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "Replay-safe boundaries must be unique, "
                    + "chronological lineage members with monotonic "
                    + "HistoryUnit counts inside the exact window."
                );
                return defects;
            }
            previousLineageIndex = boundaryIndex;
            previousCompletedUnitCount =
                boundary.CompletedUnitCount;
        }

        EventAddress expectedBaseline =
            facts.LatestPublishedSetAnchor
            ?? window.StartExclusive;
        if (facts.CadenceBaseline != expectedBaseline) {
            Add(
                defects,
                RecapPlanDefectCodes.CadenceBaselineInvalid,
                "Cadence baseline does not match the latest Published "
                + "admission or the exact bootstrap start."
            );
            return defects;
        }
        if (!lineage.TryGetValue(
                facts.CadenceBaseline,
                out int baselineIndex
            )
            || baselineIndex > startIndex) {
            Add(
                defects,
                RecapPlanDefectCodes.CadenceBaselineInvalid,
                "Cadence baseline is outside the exact history window."
            );
            return defects;
        }
        if (facts.CadenceBaseline != window.StartExclusive) {
            SessionHistoryPlanningBoundary? baselineBoundary =
                window.ReplaySafeBoundaries.SingleOrDefault(
                    boundary =>
                        boundary.Address == facts.CadenceBaseline
                );
            if (baselineBoundary is null) {
                Add(
                    defects,
                    RecapPlanDefectCodes.CadenceBaselineInvalid,
                    "Cadence baseline is not an exact replay-safe "
                    + "history-window boundary."
                );
                return defects;
            }
            baselineCompletedUnitCount =
                baselineBoundary.CompletedUnitCount;
        }
        return defects;
    }

    private static Dictionary<EventAddress, int> ValidateLineage(
        EventAddress capturedHead,
        IReadOnlyList<SessionCurrentLineageHeader> headToRoot,
        List<RecapPlanDefect> defects
    ) {
        var lineage = new Dictionary<EventAddress, int>();
        if (headToRoot.Count == 0
            || headToRoot[0] is null
            || headToRoot[0].Address != capturedHead) {
            Add(
                defects,
                RecapPlanDefectCodes.PlanningFactsInvalid,
                "Raw lineage does not start at CapturedHead."
            );
            return lineage;
        }
        for (int index = 0; index < headToRoot.Count; index++) {
            SessionCurrentLineageHeader? node = headToRoot[index];
            if (node is null || !lineage.TryAdd(node.Address, index)) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "Raw lineage contains a null or duplicate node."
                );
                return lineage;
            }
            EventAddress? expectedParent =
                index + 1 < headToRoot.Count
                    ? headToRoot[index + 1]?.Address
                    : null;
            if (node.Parent != expectedParent) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "Raw lineage is not Parent-contiguous."
                );
                return lineage;
            }
        }
        return lineage;
    }

    private static List<RecapPlanDefect> ValidateSourceIntents(
        RecapSchedulingResult.Ready schedule,
        RecapPolicyFacts policyFacts
    ) {
        var defects = new List<RecapPlanDefect>();
        RecapSchedulingFacts scheduling = schedule.Facts;
        Dictionary<EventAddress, int> lineage = scheduling.HeadToRoot
            .Select((node, index) => (node.Address, index))
            .ToDictionary(
                static pair => pair.Address,
                static pair => pair.index
            );
        HashSet<EventAddress> replaySafe =
            GetReplaySafeAddresses(scheduling);

        if (policyFacts.EmptyReplayStartExclusive is { } emptyStart) {
            if (emptyStart == default
                || scheduling.LatestPublishedSetAnchor is not null
                || policyFacts.AvailableSources.Count != 0
                || !lineage.ContainsKey(emptyStart)
                || !replaySafe.Contains(emptyStart)) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "First-build facts must contain only one exact "
                    + "replay-safe empty replay start."
                );
            }
            return defects;
        }

        if (scheduling.LatestPublishedSetAnchor is null
            || policyFacts.AvailableSources.Count
                != schedule.Inputs.OrderedCatalog.Count) {
            Add(
                defects,
                RecapPlanDefectCodes.PlanningFactsInvalid,
                "Existing-build facts must cover the exact ordered catalog."
            );
            return defects;
        }

        for (int index = 0;
             index < schedule.Inputs.OrderedCatalog.Count;
             index++) {
            RecapBlockCatalogEntry expected =
                schedule.Inputs.OrderedCatalog[index];
            RecapBlockSourceIntent? item =
                policyFacts.AvailableSources[index];
            if (item is null
                || item.RecapBlockId is null
                || item.Source is null
                || item.RecapBlockId != expected.RecapBlockId
                || item.Source.SourceSetAnchor
                    != scheduling.LatestPublishedSetAnchor
                || item.AbsorbedThrough == default
                || !lineage.TryGetValue(
                    item.Source.SourceSetAnchor,
                    out int sourceIndex
                )
                || !lineage.TryGetValue(
                    item.AbsorbedThrough,
                    out int cursorIndex
                )
                || cursorIndex < sourceIndex
                || !replaySafe.Contains(item.AbsorbedThrough)) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "Existing source facts are incomplete, reordered, "
                    + "off-lineage, or contain an invalid exact cursor."
                );
                break;
            }
        }
        return defects;
    }

    private static List<RecapPlanDefect> ValidateIntent(
        RecapSchedulingResult.Ready schedule,
        RecapPolicyFacts policyFacts,
        RecapPlanningPolicyDecision.Build build
    ) {
        var defects = new List<RecapPlanDefect>();
        RecapPlanningInputs inputs = schedule.Inputs;
        RecapPlanningLimits limits = schedule.Limits;
        RecapSchedulingFacts facts = schedule.Facts;
        Dictionary<EventAddress, int> lineage = facts.HeadToRoot
            .Select((node, index) => (node.Address, index))
            .ToDictionary(
                static pair => pair.Address,
                static pair => pair.index
            );
        HashSet<EventAddress> replaySafe =
            GetReplaySafeAddresses(facts);
        if (!lineage.TryGetValue(
                build.SetAdmissionAnchor,
                out int admissionIndex
            )
            || !schedule.Cadence.AdmissionCandidates.Any(
                candidate =>
                    candidate.Address == build.SetAdmissionAnchor
            )) {
            Add(
                defects,
                RecapPlanDefectCodes.AdmissionInvalid,
                "SetAdmissionAnchor is not a cadence-safe replay "
                + "boundary preserving the configured recent reserve."
            );
        }
        if (facts.LatestPublishedSetAnchor is { } latest
            && (!lineage.TryGetValue(latest, out int latestIndex)
                || admissionIndex >= latestIndex)) {
            Add(
                defects,
                RecapPlanDefectCodes.AdmissionInvalid,
                "SetAdmissionAnchor is not strictly newer than latest Published."
            );
        }
        if (build.Blocks.Count != inputs.OrderedCatalog.Count) {
            Add(
                defects,
                RecapPlanDefectCodes.CatalogMismatch,
                "Policy block roster does not match the ordered catalog."
            );
            return defects;
        }

        long calls = 0;
        for (int index = 0;
             index < inputs.OrderedCatalog.Count;
             index++) {
            RecapBlockCatalogEntry catalog =
                inputs.OrderedCatalog[index];
            RecapBlockPlanningDecision? decision =
                build.Blocks[index];
            if (decision is null
                || GetBlockId(decision) != catalog.RecapBlockId) {
                Add(
                    defects,
                    RecapPlanDefectCodes.CatalogMismatch,
                    "Policy block order or RecapBlockId differs from catalog."
                );
                continue;
            }
            switch (decision) {
                case RecapBlockPlanningDecision.Inherit inherit:
                    ValidateChosenSource(
                        policyFacts,
                        lineage,
                        catalog.RecapBlockId,
                        inherit.Source,
                        admissionIndex,
                        defects
                    );
                    break;
                case RecapBlockPlanningDecision.Maintain maintain:
                    ValidateMaintainIntent(
                        limits,
                        policyFacts,
                        lineage,
                        replaySafe,
                        maintain,
                        admissionIndex,
                        defects
                    );
                    calls += maintain.CatchUpThrough.Count;
                    break;
            }
        }
        if (calls > limits.MaxMaintainerCallsPerBuild) {
            Add(
                defects,
                RecapPlanDefectCodes.CallLimitExceeded,
                $"Plan requires {calls} Maintainer calls; limit is "
                + $"{limits.MaxMaintainerCallsPerBuild}."
            );
        }
        return defects;
    }

    private static void ValidateMaintainIntent(
        RecapPlanningLimits limits,
        RecapPolicyFacts policyFacts,
        IReadOnlyDictionary<EventAddress, int> lineage,
        IReadOnlySet<EventAddress> replaySafe,
        RecapBlockPlanningDecision.Maintain maintain,
        int admissionIndex,
        List<RecapPlanDefect> defects
    ) {
        switch (maintain.Source) {
            case RecapPlanningMaintainSource.Existing existing:
                ValidateChosenSource(
                    policyFacts,
                    lineage,
                    maintain.RecapBlockId,
                    existing.Source,
                    admissionIndex,
                    defects
                );
                break;
            case RecapPlanningMaintainSource.Empty empty:
                if (policyFacts.EmptyReplayStartExclusive
                        is not { } authoritativeStart
                    || empty.ReplayStartExclusive != authoritativeStart
                    || !lineage.TryGetValue(
                        empty.ReplayStartExclusive,
                        out int startIndex
                    )
                    || startIndex <= admissionIndex
                    || !replaySafe.Contains(
                        empty.ReplayStartExclusive
                    )) {
                    Add(
                        defects,
                        RecapPlanDefectCodes.SourceInvalid,
                        "Empty replay start is not the exact authorized "
                        + "first-build seed or a replay-safe strict "
                        + "ancestor of admission."
                    );
                }
                break;
            default:
                Add(
                    defects,
                    RecapPlanDefectCodes.SourceInvalid,
                    "Maintain source intent is invalid."
                );
                break;
        }

        IReadOnlyList<EventAddress> route = maintain.CatchUpThrough;
        if (route.Count == 0) {
            Add(
                defects,
                RecapPlanDefectCodes.RouteInvalid,
                "Maintain route must contain at least one endpoint."
            );
            return;
        }
        if (route.Count > limits.MaxRouteEndpointsPerBlock) {
            Add(
                defects,
                RecapPlanDefectCodes.RouteLimitExceeded,
                $"Block '{maintain.RecapBlockId}' has {route.Count} "
                + "route endpoints."
            );
        }
        int? previousIndex = null;
        foreach (EventAddress endpoint in route) {
            if (!lineage.TryGetValue(endpoint, out int endpointIndex)
                || previousIndex is int previous
                   && endpointIndex >= previous
                || !replaySafe.Contains(endpoint)) {
                Add(
                    defects,
                    RecapPlanDefectCodes.RouteInvalid,
                    $"Block '{maintain.RecapBlockId}' route is not "
                    + "strictly increasing over replay-safe boundaries."
                );
                break;
            }
            previousIndex = endpointIndex;
        }
        if (route[^1] != buildAdmission()) {
            Add(
                defects,
                RecapPlanDefectCodes.RouteInvalid,
                $"Block '{maintain.RecapBlockId}' final endpoint is "
                + "not SetAdmissionAnchor."
            );
        }

        EventAddress buildAdmission()
            => lineage.Single(pair => pair.Value == admissionIndex).Key;
    }

    private static void ValidateChosenSource(
        RecapPolicyFacts policyFacts,
        IReadOnlyDictionary<EventAddress, int> lineage,
        RecapBlockId blockId,
        RecapSourceIntent? chosen,
        int admissionIndex,
        List<RecapPlanDefect> defects
    ) {
        if (chosen is null
            || !policyFacts.AvailableSources.Any(candidate =>
                candidate.RecapBlockId == blockId
                && candidate.Source == chosen)
            || !lineage.TryGetValue(
                chosen.SourceSetAnchor,
                out int sourceIndex
            )
            || sourceIndex <= admissionIndex) {
            Add(
                defects,
                RecapPlanDefectCodes.SourceInvalid,
                $"Block '{blockId}' source is not an available exact "
                + "pre-freeze intent or strict admission ancestor."
            );
        }
    }

    private static List<RecapPlanDefect> ValidatePreflight(
        RecapPlanIntentResult.IntentReady ready,
        RecapPlanPreflightFacts preflight
    ) {
        var defects = new List<RecapPlanDefect>();
        RecapPlanningLimits limits = ready.Schedule.Limits;
        RecapSchedulingFacts facts = ready.Schedule.Facts;
        RecapPlanningPolicyDecision.Build build = ready.Intent;
        ValidatePreflightShape(preflight, defects);
        if (defects.Count != 0) {
            return defects;
        }
        Dictionary<EventAddress, int> lineage = facts.HeadToRoot
            .Select((node, index) => (node.Address, index))
            .ToDictionary(
                static pair => pair.Address,
                static pair => pair.index
            );
        int admissionIndex = lineage[build.SetAdmissionAnchor];
        var usedSourceFacts = new HashSet<RecapBlockId>();
        var expectedCosts = new List<RecapPlannedStepCost>();

        foreach (RecapBlockPlanningDecision decision in build.Blocks) {
            switch (decision) {
                case RecapBlockPlanningDecision.Inherit inherit:
                    _ = ResolveSourceStart(
                        inherit.RecapBlockId,
                        inherit.Source,
                        preflight,
                        lineage,
                        admissionIndex,
                        usedSourceFacts,
                        defects
                    );
                    break;
                case RecapBlockPlanningDecision.Maintain maintain:
                    EventAddress? start = maintain.Source switch {
                        RecapPlanningMaintainSource.Empty empty =>
                            empty.ReplayStartExclusive,
                        RecapPlanningMaintainSource.Existing existing =>
                            ResolveSourceStart(
                                maintain.RecapBlockId,
                                existing.Source,
                                preflight,
                                lineage,
                                admissionIndex,
                                usedSourceFacts,
                                defects
                            ),
                        _ => null
                    };
                    if (start is not { } previous) {
                        break;
                    }
                    ValidateRouteFromStart(
                        maintain,
                        previous,
                        lineage,
                        GetReplaySafeAddresses(facts),
                        defects
                    );
                    ValidatePriorContext(
                        maintain,
                        previous,
                        lineage,
                        defects
                    );
                    foreach (EventAddress endpoint
                             in maintain.CatchUpThrough) {
                        expectedCosts.Add(new RecapPlannedStepCost(
                            maintain.RecapBlockId,
                            previous,
                            endpoint,
                            RawEventCount: 0
                        ));
                        previous = endpoint;
                    }
                    break;
            }
        }
        if (usedSourceFacts.Count
            != preflight.SourceReplayFacts.Count) {
            Add(
                defects,
                RecapPlanDefectCodes.PlanningFactsInvalid,
                "Preflight contains an unused or duplicate source replay fact."
            );
        }
        ValidateCosts(limits, expectedCosts, preflight, defects);
        return defects;
    }

    private static void ValidatePreflightShape(
        RecapPlanPreflightFacts preflight,
        List<RecapPlanDefect> defects
    ) {
        var sourceIds = new HashSet<RecapBlockId>();
        foreach (RecapSourceReplayFact? fact
                 in preflight.SourceReplayFacts) {
            if (fact is null
                || fact.RecapBlockId is null
                || fact.Source is null
                || fact.AbsorbedThrough == default
                || !sourceIds.Add(fact.RecapBlockId)) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "Source replay facts are null, incomplete, or "
                    + "duplicate."
                );
                return;
            }
        }
        var stepKeys = new HashSet<(
            RecapBlockId BlockId,
            EventAddress Start,
            EventAddress End
        )>();
        foreach (RecapPlannedStepCost? cost in preflight.StepCosts) {
            if (cost is null
                || cost.RecapBlockId is null
                || cost.StartExclusive == default
                || cost.EndInclusive == default
                || !stepKeys.Add((
                    cost.RecapBlockId,
                    cost.StartExclusive,
                    cost.EndInclusive
                ))) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "Step cost facts are null, incomplete, or duplicate."
                );
                return;
            }
        }
    }

    private static EventAddress? ResolveSourceStart(
        RecapBlockId blockId,
        RecapSourceIntent source,
        RecapPlanPreflightFacts preflight,
        IReadOnlyDictionary<EventAddress, int> lineage,
        int admissionIndex,
        HashSet<RecapBlockId> used,
        List<RecapPlanDefect> defects
    ) {
        RecapSourceReplayFact? fact =
            preflight.SourceReplayFacts.SingleOrDefault(candidate =>
                candidate.RecapBlockId == blockId
                && candidate.Source == source);
        if (fact is null
            || !used.Add(blockId)
            || !lineage.TryGetValue(
                source.SourceSetAnchor,
                out int sourceIndex
            )
            || !lineage.TryGetValue(
                fact.AbsorbedThrough,
                out int absorbedIndex
            )
            || sourceIndex <= admissionIndex
            || absorbedIndex < sourceIndex) {
            Add(
                defects,
                RecapPlanDefectCodes.SourceInvalid,
                $"Block '{blockId}' exact source replay fact is "
                + "missing, duplicate, or has an invalid cursor."
            );
            return null;
        }
        return fact.AbsorbedThrough;
    }

    private static void ValidateRouteFromStart(
        RecapBlockPlanningDecision.Maintain maintain,
        EventAddress start,
        IReadOnlyDictionary<EventAddress, int> lineage,
        IReadOnlySet<EventAddress> replaySafe,
        List<RecapPlanDefect> defects
    ) {
        if (!lineage.TryGetValue(start, out int previousIndex)
            || !replaySafe.Contains(start)) {
            Add(
                defects,
                RecapPlanDefectCodes.SourceInvalid,
                $"Block '{maintain.RecapBlockId}' replay start is not "
                + "an exact replay-safe lineage boundary."
            );
            return;
        }
        foreach (EventAddress endpoint in maintain.CatchUpThrough) {
            if (!lineage.TryGetValue(endpoint, out int endpointIndex)
                || endpointIndex >= previousIndex) {
                Add(
                    defects,
                    RecapPlanDefectCodes.RouteInvalid,
                    $"Block '{maintain.RecapBlockId}' first endpoint "
                    + "is not strictly newer than its exact source cursor."
                );
                return;
            }
            previousIndex = endpointIndex;
        }
    }

    private static void ValidatePriorContext(
        RecapBlockPlanningDecision.Maintain maintain,
        EventAddress start,
        IReadOnlyDictionary<EventAddress, int> lineage,
        List<RecapPlanDefect> defects
    ) {
        switch (maintain.PriorContext) {
            case EmptyRecapPriorContext:
                return;
            case InlineRecapPriorContext inline
                when inline.Snapshot is not null
                     && lineage.TryGetValue(
                         inline.AdmissionAnchor,
                         out int priorIndex
                     )
                     && priorIndex >= lineage[start]:
                return;
            default:
                Add(
                    defects,
                    RecapPlanDefectCodes.PriorContextInvalid,
                    $"Block '{maintain.RecapBlockId}' prior context "
                    + "is not an ancestor of its replay start."
                );
                return;
        }
    }

    private static void ValidateCosts(
        RecapPlanningLimits limits,
        IReadOnlyList<RecapPlannedStepCost> expected,
        RecapPlanPreflightFacts preflight,
        List<RecapPlanDefect> defects
    ) {
        if (expected.Count != preflight.StepCosts.Count) {
            Add(
                defects,
                RecapPlanDefectCodes.PlanningFactsInvalid,
                "Preflight step costs do not cover the exact planned route."
            );
            return;
        }
        long total = 0;
        var used = new HashSet<int>();
        foreach (RecapPlannedStepCost planned in expected) {
            int match = -1;
            for (int index = 0;
                 index < preflight.StepCosts.Count;
                 index++) {
                RecapPlannedStepCost candidate =
                    preflight.StepCosts[index];
                if (!used.Contains(index)
                    && candidate.RecapBlockId
                        == planned.RecapBlockId
                    && candidate.StartExclusive
                        == planned.StartExclusive
                    && candidate.EndInclusive
                        == planned.EndInclusive) {
                    match = index;
                    break;
                }
            }
            if (match < 0) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "A planned route step has no exact raw cost fact."
                );
                continue;
            }
            used.Add(match);
            int count = preflight.StepCosts[match].RawEventCount;
            if (count <= 0) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "Raw step cost must be positive."
                );
                continue;
            }
            if (count > limits.MaxRawEventsPerStep) {
                Add(
                    defects,
                    RecapPlanDefectCodes.RawStepLimitExceeded,
                    $"Raw step cost {count} exceeds limit "
                    + $"{limits.MaxRawEventsPerStep}."
                );
            }
            total += count;
        }
        if (total > limits.MaxRawEventsPerBuild) {
            Add(
                defects,
                RecapPlanDefectCodes.RawBuildLimitExceeded,
                $"Raw build cost {total} exceeds limit "
                + $"{limits.MaxRawEventsPerBuild}."
            );
        }
    }

    private static RecapBlockId GetBlockId(
        RecapBlockPlanningDecision decision
    ) => decision switch {
        RecapBlockPlanningDecision.Inherit inherit =>
            inherit.RecapBlockId,
        RecapBlockPlanningDecision.Maintain maintain =>
            maintain.RecapBlockId,
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };

    private static HashSet<EventAddress> GetReplaySafeAddresses(
        RecapSchedulingFacts facts
    ) {
        var replaySafe = facts.HistoryWindow.ReplaySafeBoundaries
            .Select(static boundary => boundary.Address)
            .ToHashSet();
        replaySafe.Add(facts.HistoryWindow.StartExclusive);
        return replaySafe;
    }

    private static RecapHeaderPrefilterResult.Unavailable
        HeaderUnavailable(
        string code,
        string detail
    ) => HeaderUnavailable([new RecapPlanDefect(code, detail)]);

    private static RecapHeaderPrefilterResult.Unavailable
        HeaderUnavailable(
        IReadOnlyList<RecapPlanDefect> defects
    ) => new(Array.AsReadOnly([.. defects]));

    private static RecapSchedulingResult.Unavailable
        ScheduleUnavailable(
        string code,
        string detail
    ) => ScheduleUnavailable([new RecapPlanDefect(code, detail)]);

    private static RecapSchedulingResult.Unavailable
        ScheduleUnavailable(
        IReadOnlyList<RecapPlanDefect> defects
    ) => new(Array.AsReadOnly([.. defects]));

    private static RecapPlanIntentResult.Unavailable IntentUnavailable(
        string code,
        string detail
    ) => IntentUnavailable([new RecapPlanDefect(code, detail)]);

    private static RecapPlanIntentResult.Unavailable IntentUnavailable(
        IReadOnlyList<RecapPlanDefect> defects
    ) => new(Array.AsReadOnly([.. defects]));

    private static RecapPlanResult.Unavailable PlanUnavailable(
        IReadOnlyList<RecapPlanDefect> defects
    ) => new(Array.AsReadOnly([.. defects]));

    private static void Add(
        List<RecapPlanDefect> defects,
        string code,
        string detail
    ) => defects.Add(new RecapPlanDefect(code, detail));
}
