using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Deterministic baseline policy that advances every catalog block as far as
/// the configured replay and call ceilings permit.
/// </summary>
public sealed class BoundedMaintainAllRecapPlanningPolicy
    : IRecapPlanningPolicy {
    public string Id => RecapPlanningPolicyIds.BoundedMaintainAllV1;

    public RecapPlanningPolicyDecision Decide(
        RecapPlanningPolicyContext context
    ) {
        ArgumentNullException.ThrowIfNull(context);

        RecapPlanningInputs inputs = context.Inputs;
        RecapPlanningLimits limits = context.Limits;
        RecapSchedulingFacts scheduling = context.Scheduling;
        Dictionary<EventAddress, int> lineage =
            scheduling.HeadToRoot
                .Select((node, index) => (node.Address, index))
                .ToDictionary(
                    static pair => pair.Address,
                    static pair => pair.index
                );
        Dictionary<int, EventAddress> replaySafe =
            scheduling.HistoryWindow.ReplaySafeBoundaries.ToDictionary(
                boundary => lineage[boundary.Address],
                static boundary => boundary.Address
            );

        IReadOnlyList<BlockStart> starts = ResolveStarts(
            inputs,
            context.PolicyFacts,
            lineage
        );
        int latestPublishedIndex =
            scheduling.LatestPublishedSetAnchor is { } latest
                ? lineage[latest]
                : int.MaxValue;
        int newestAllowedStartIndex = starts.Min(
            static start => start.LineageIndex
        );
        int[] admissions = [
            .. context.Cadence.AdmissionCandidates
                .Select(candidate => lineage[candidate.Address])
                .Where(index =>
                    index < latestPublishedIndex
                    && index < newestAllowedStartIndex)
                .OrderBy(static index => index)
        ];
        if (admissions.Length == 0) {
            return Unavailable(
                RecapPlanDefectCodes.AdmissionInvalid,
                "Evaluator-provided cadence candidates are not newer "
                + "than the authorized source cursors."
            );
        }

        IReadOnlyList<RecapPlanDefect> lastDefects = [];
        foreach (int admissionIndex in admissions) {
            CandidateResult candidate = TryBuildCandidate(
                limits,
                starts,
                replaySafe,
                admissionIndex,
                context.SharedPriorContext
            );
            if (candidate.Blocks is { } blocks) {
                return new RecapPlanningPolicyDecision.Build(
                    replaySafe[admissionIndex],
                    blocks
                );
            }
            lastDefects = candidate.Defects;
        }

        return new RecapPlanningPolicyDecision.Unavailable(
            lastDefects.Count == 0
                ? [
                    new RecapPlanDefect(
                        RecapPlanDefectCodes.AdmissionInvalid,
                        "No budget-valid replay-safe admission exists."
                    )
                ]
                : lastDefects
        );
    }

    private static IReadOnlyList<BlockStart> ResolveStarts(
        RecapPlanningInputs inputs,
        RecapPolicyFacts facts,
        IReadOnlyDictionary<EventAddress, int> lineage
    ) {
        if (facts.EmptyReplayStartExclusive is { } emptyStart) {
            return [
                .. inputs.OrderedCatalog.Select(entry => new BlockStart(
                    entry,
                    new RecapPlanningMaintainSource.Empty(emptyStart),
                    lineage[emptyStart]
                ))
            ];
        }

        return [
            .. inputs.OrderedCatalog.Select((entry, index) => {
                RecapBlockSourceIntent source =
                    facts.AvailableSources[index];
                return new BlockStart(
                    entry,
                    new RecapPlanningMaintainSource.Existing(
                        source.Source
                    ),
                    lineage[source.AbsorbedThrough]
                );
            })
        ];
    }

    private static CandidateResult TryBuildCandidate(
        RecapPlanningLimits limits,
        IReadOnlyList<BlockStart> starts,
        IReadOnlyDictionary<int, EventAddress> replaySafe,
        int admissionIndex,
        RecapPriorContext sharedPriorContext
    ) {
        var blocks = new List<RecapBlockPlanningDecision>(
            starts.Count
        );
        var defects = new List<RecapPlanDefect>();
        long calls = 0;
        long rawEvents = 0;

        foreach (BlockStart start in starts) {
            IReadOnlyList<EventAddress>? route = BuildGreedyRoute(
                start,
                replaySafe,
                admissionIndex
            );
            if (route is null) {
                AddOnce(
                    defects,
                    RecapPlanDefectCodes.RawStepLimitExceeded,
                    $"Block '{start.Catalog.RecapBlockId}' has no "
                    + "replay-safe route whose steps fit "
                    + $"MaxRawEventsPerStep "
                    + $"{limits.MaxRawEventsPerStep}."
                );
                continue;
            }
            if (route.Count > limits.MaxRouteEndpointsPerBlock) {
                AddOnce(
                    defects,
                    RecapPlanDefectCodes.RouteLimitExceeded,
                    $"Block '{start.Catalog.RecapBlockId}' requires "
                    + $"{route.Count} route endpoints; limit is "
                    + $"{limits.MaxRouteEndpointsPerBlock}."
                );
            }

            calls += route.Count;
            rawEvents += start.LineageIndex - admissionIndex;
            blocks.Add(new RecapBlockPlanningDecision.Maintain(
                start.Catalog.RecapBlockId,
                start.Source,
                route,
                sharedPriorContext
            ));
        }

        if (calls > limits.MaxMaintainerCallsPerBuild) {
            AddOnce(
                defects,
                RecapPlanDefectCodes.CallLimitExceeded,
                $"Plan requires {calls} Maintainer calls; limit is "
                + $"{limits.MaxMaintainerCallsPerBuild}."
            );
        }
        if (rawEvents > limits.MaxRawEventsPerBuild) {
            AddOnce(
                defects,
                RecapPlanDefectCodes.RawBuildLimitExceeded,
                $"Plan requires {rawEvents} maintained raw events; "
                + $"limit is {limits.MaxRawEventsPerBuild}."
            );
        }

        return defects.Count == 0
            ? new CandidateResult(blocks, [])
            : new CandidateResult(null, defects);

        IReadOnlyList<EventAddress>? BuildGreedyRoute(
            BlockStart start,
            IReadOnlyDictionary<int, EventAddress> boundaries,
            int targetIndex
        ) {
            var route = new List<EventAddress>();
            int cursorIndex = start.LineageIndex;
            while (cursorIndex > targetIndex) {
                int? nextIndex = boundaries.Keys
                    .Where(index =>
                        index >= targetIndex
                        && index < cursorIndex
                        && cursorIndex - index
                            <= limits.MaxRawEventsPerStep)
                    .Cast<int?>()
                    .Min();
                if (nextIndex is not { } next) {
                    return null;
                }
                route.Add(boundaries[next]);
                cursorIndex = next;
            }
            return route;
        }
    }

    private static RecapPlanningPolicyDecision.Unavailable Unavailable(
        string code,
        string detail
    ) => new([new RecapPlanDefect(code, detail)]);

    private static void AddOnce(
        List<RecapPlanDefect> defects,
        string code,
        string detail
    ) {
        if (!defects.Any(defect => defect.Code == code)) {
            defects.Add(new RecapPlanDefect(code, detail));
        }
    }

    private sealed record BlockStart(
        RecapBlockCatalogEntry Catalog,
        RecapPlanningMaintainSource Source,
        int LineageIndex
    );

    private sealed record CandidateResult(
        IReadOnlyList<RecapBlockPlanningDecision>? Blocks,
        IReadOnlyList<RecapPlanDefect> Defects
    );
}
