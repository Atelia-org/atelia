using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public static class RecapPlanEvaluator {
    public static RecapPlanResult Evaluate(
        RecapPlannerConfig config,
        RecapPlanningFacts facts,
        IRecapPlanningPolicy policy
    ) {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(policy);

        List<RecapPlanDefect> factDefects = ValidateFacts(facts);
        if (factDefects.Count != 0) {
            return Unavailable(factDefects);
        }
        if (facts.RawGrowth > config.RawGrowthHardLimit) {
            return Unavailable(
                RecapPlanDefectCodes.RawGrowthHardLimitExceeded,
                $"Raw growth {facts.RawGrowth} exceeds hard limit "
                + $"{config.RawGrowthHardLimit}."
            );
        }
        if (facts.RawGrowth < config.RawGrowthTrigger) {
            return new RecapPlanResult.NoBuild(
                RecapPlanReasons.BelowRawGrowthTrigger
            );
        }

        RecapPlanningPolicyDecision decision =
            policy.Decide(new RecapPlanningPolicyContext(config, facts));
        if (decision is null) {
            return Unavailable(
                RecapPlanDefectCodes.PolicyDecisionInvalid,
                "Planning policy returned null."
            );
        }
        if (decision is RecapPlanningPolicyDecision.NoBuild noBuild) {
            return string.IsNullOrWhiteSpace(noBuild.Reason)
                ? Unavailable(
                    RecapPlanDefectCodes.PolicyDecisionInvalid,
                    "Planning policy returned an empty NoBuild reason."
                )
                : new RecapPlanResult.NoBuild(noBuild.Reason);
        }

        var build = (RecapPlanningPolicyDecision.Build)decision;
        List<RecapPlanDefect> defects =
            ValidateBuild(config, facts, build);
        return defects.Count == 0
            ? new RecapPlanResult.PlanReady(
                config,
                build.SetAdmissionAnchor,
                Array.AsReadOnly([.. build.Blocks])
            )
            : Unavailable(defects);
    }

    private static List<RecapPlanDefect> ValidateFacts(
        RecapPlanningFacts facts
    ) {
        var defects = new List<RecapPlanDefect>();
        if (facts.HeadToRoot.Count == 0
            || facts.HeadToRoot[0].Address != facts.CapturedHead) {
            Add(
                defects,
                RecapPlanDefectCodes.PlanningFactsInvalid,
                "Raw lineage does not start at CapturedHead."
            );
            return defects;
        }

        var lineage = new HashSet<EventAddress>();
        for (int index = 0; index < facts.HeadToRoot.Count; index++) {
            SessionCurrentLineageHeader? node =
                facts.HeadToRoot[index];
            if (node is null || !lineage.Add(node.Address)) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "Raw lineage contains a null or duplicate node."
                );
                return defects;
            }
            EventAddress? expectedParent =
                index + 1 < facts.HeadToRoot.Count
                    ? facts.HeadToRoot[index + 1].Address
                    : null;
            if (node.Parent != expectedParent) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "Raw lineage is not Parent-contiguous."
                );
                return defects;
            }
        }
        if (facts.ReplaySafeBoundaries.Any(
                boundary => !lineage.Contains(boundary)
            )
            || facts.ReplaySafeBoundaries.Distinct().Count()
                != facts.ReplaySafeBoundaries.Count) {
            Add(
                defects,
                RecapPlanDefectCodes.PlanningFactsInvalid,
                "Replay-safe boundaries must be unique lineage members."
            );
        }
        if (facts.LatestPublishedSetAnchor is { } latest
            && !lineage.Contains(latest)) {
            Add(
                defects,
                RecapPlanDefectCodes.PlanningFactsInvalid,
                "Latest Published anchor is outside the raw lineage."
            );
        }
        var sourceKeys = new HashSet<(
            RecapBlockId RecapBlockId,
            EventAddress SetAnchor,
            string Envelope
        )>();
        foreach (RecapPublishedBlockFact? source
                 in facts.PublishedBlocks) {
            if (source is null
                || !sourceKeys.Add((
                    source.RecapBlockId,
                    source.SourceSetAnchor,
                    source.SourcePublicationEnvelopeSha256
                ))
                || !lineage.Contains(source.SourceSetAnchor)
                || !lineage.Contains(source.AbsorbedThrough)) {
                Add(
                    defects,
                    RecapPlanDefectCodes.PlanningFactsInvalid,
                    "Published block facts are null, duplicate, or "
                    + "outside the raw lineage."
                );
                break;
            }
        }
        return defects;
    }

    private static List<RecapPlanDefect> ValidateBuild(
        RecapPlannerConfig config,
        RecapPlanningFacts facts,
        RecapPlanningPolicyDecision.Build build
    ) {
        var defects = new List<RecapPlanDefect>();
        if (build.Blocks is null) {
            Add(
                defects,
                RecapPlanDefectCodes.PolicyDecisionInvalid,
                "Build block decisions are null."
            );
            return defects;
        }

        Dictionary<EventAddress, int> lineage = facts.HeadToRoot
            .Select((node, index) => (node.Address, index))
            .ToDictionary(static pair => pair.Address, static pair => pair.index);
        var replaySafe = facts.ReplaySafeBoundaries.ToHashSet();
        if (!lineage.TryGetValue(
                build.SetAdmissionAnchor,
                out int admissionIndex
            )
            || !replaySafe.Contains(build.SetAdmissionAnchor)) {
            Add(
                defects,
                RecapPlanDefectCodes.AdmissionInvalid,
                "SetAdmissionAnchor is not a replay-safe lineage boundary."
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

        if (build.Blocks.Count != config.Catalog.Count) {
            Add(
                defects,
                RecapPlanDefectCodes.CatalogMismatch,
                "Policy block roster does not match the ordered catalog."
            );
            return defects;
        }

        long maintainerCalls = 0;
        for (int index = 0; index < config.Catalog.Count; index++) {
            RecapBlockCatalogEntry catalog = config.Catalog[index];
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
                    _ = FindAndValidateSource(
                        facts,
                        lineage,
                        catalog,
                        inherit.SourceSetAnchor,
                        inherit.SourcePublicationEnvelopeSha256,
                        admissionIndex,
                        defects
                    );
                    break;
                case RecapBlockPlanningDecision.Maintain maintain:
                    ValidateMaintain(
                        config,
                        facts,
                        lineage,
                        replaySafe,
                        catalog,
                        maintain,
                        admissionIndex,
                        defects
                    );
                    maintainerCalls +=
                        maintain.CatchUpThrough?.Count ?? 0;
                    break;
                default:
                    Add(
                        defects,
                        RecapPlanDefectCodes.PolicyDecisionInvalid,
                        "Unknown Recap block decision."
                    );
                    break;
            }
        }

        if (maintainerCalls > config.MaxMaintainerCallsPerBuild) {
            Add(
                defects,
                RecapPlanDefectCodes.CallLimitExceeded,
                $"Plan requires {maintainerCalls} Maintainer calls; "
                + $"limit is {config.MaxMaintainerCallsPerBuild}."
            );
        }
        return defects;
    }

    private static void ValidateMaintain(
        RecapPlannerConfig config,
        RecapPlanningFacts facts,
        IReadOnlyDictionary<EventAddress, int> lineage,
        IReadOnlySet<EventAddress> replaySafe,
        RecapBlockCatalogEntry catalog,
        RecapBlockPlanningDecision.Maintain maintain,
        int admissionIndex,
        List<RecapPlanDefect> defects
    ) {
        EventAddress? replayStart = maintain.Source switch {
            RecapPlanningMaintainSource.Existing existing =>
                FindAndValidateSource(
                    facts,
                    lineage,
                    catalog,
                    existing.SourceSetAnchor,
                    existing.SourcePublicationEnvelopeSha256,
                    admissionIndex,
                    defects
                )?.AbsorbedThrough,
            RecapPlanningMaintainSource.Empty empty =>
                empty.ReplayStartExclusive,
            null => null,
            _ => null
        };
        if (replayStart is not { } start
            || !lineage.TryGetValue(start, out int previousIndex)
            || previousIndex <= admissionIndex
            || !replaySafe.Contains(start)) {
            Add(
                defects,
                RecapPlanDefectCodes.SourceInvalid,
                "Maintain replay start is not a replay-safe strict "
                + "ancestor of admission."
            );
            return;
        }

        IReadOnlyList<EventAddress>? route = maintain.CatchUpThrough;
        if (route is null || route.Count == 0) {
            Add(
                defects,
                RecapPlanDefectCodes.RouteInvalid,
                "Maintain route must contain at least one endpoint."
            );
            return;
        }
        if (route.Count > config.MaxRouteEndpointsPerBlock) {
            Add(
                defects,
                RecapPlanDefectCodes.RouteLimitExceeded,
                $"Block '{catalog.RecapBlockId}' has {route.Count} "
                + "route endpoints."
            );
        }
        foreach (EventAddress endpoint in route) {
            if (!lineage.TryGetValue(endpoint, out int endpointIndex)
                || endpointIndex >= previousIndex
                || !replaySafe.Contains(endpoint)) {
                Add(
                    defects,
                    RecapPlanDefectCodes.RouteInvalid,
                    $"Block '{catalog.RecapBlockId}' route is not "
                    + "strictly increasing over replay-safe boundaries."
                );
                break;
            }
            previousIndex = endpointIndex;
        }
        if (route[^1] != facts.HeadToRoot[admissionIndex].Address) {
            Add(
                defects,
                RecapPlanDefectCodes.RouteInvalid,
                $"Block '{catalog.RecapBlockId}' final endpoint is "
                + "not SetAdmissionAnchor."
            );
        }

        switch (maintain.PriorContext) {
            case EmptyRecapPriorContext:
                break;
            case InlineRecapPriorContext inline
                when inline.Snapshot is not null
                     && lineage.TryGetValue(
                         inline.AdmissionAnchor,
                         out int priorIndex
                     )
                     && priorIndex >= lineage[start]:
                break;
            case InlineRecapPriorContext:
            case null:
            default:
                Add(
                    defects,
                    RecapPlanDefectCodes.PriorContextInvalid,
                    $"Block '{catalog.RecapBlockId}' prior context is "
                    + "not an ancestor of its replay start."
                );
                break;
        }
    }

    private static RecapPublishedBlockFact? FindAndValidateSource(
        RecapPlanningFacts facts,
        IReadOnlyDictionary<EventAddress, int> lineage,
        RecapBlockCatalogEntry catalog,
        EventAddress sourceSetAnchor,
        string sourceEnvelope,
        int admissionIndex,
        List<RecapPlanDefect> defects
    ) {
        RecapPublishedBlockFact? source =
            facts.PublishedBlocks.SingleOrDefault(candidate =>
                candidate.RecapBlockId == catalog.RecapBlockId
                && candidate.SourceSetAnchor == sourceSetAnchor
                && string.Equals(
                    candidate.SourcePublicationEnvelopeSha256,
                    sourceEnvelope,
                    StringComparison.Ordinal
                ));
        if (source is null
            || source.Target != catalog.Target
            || !lineage.TryGetValue(sourceSetAnchor, out int sourceIndex)
            || sourceIndex <= admissionIndex
            || !lineage.TryGetValue(
                source.AbsorbedThrough,
                out int absorbedIndex
            )
            || absorbedIndex < sourceIndex) {
            Add(
                defects,
                RecapPlanDefectCodes.SourceInvalid,
                $"Block '{catalog.RecapBlockId}' source does not "
                + "match supplied exact Published facts."
            );
            return null;
        }
        return source;
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

    private static RecapPlanResult.Unavailable Unavailable(
        string code,
        string detail
    ) => Unavailable([new RecapPlanDefect(code, detail)]);

    private static RecapPlanResult.Unavailable Unavailable(
        IReadOnlyList<RecapPlanDefect> defects
    ) => new(Array.AsReadOnly([.. defects]));

    private static void Add(
        List<RecapPlanDefect> defects,
        string code,
        string detail
    ) => defects.Add(new RecapPlanDefect(code, detail));
}
