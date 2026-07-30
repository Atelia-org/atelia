using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Deterministic baseline policy that advances every catalog block as far as
/// the configured replay and call ceilings permit.
/// </summary>
public sealed class BoundedMaintainAllRecapPlanningPolicy
    : IRecapPlanningPolicy {
    public RecapPlanningPolicyDecision Decide(
        RecapPlanningPolicyContext context
    ) {
        ArgumentNullException.ThrowIfNull(context);

        RecapPlannerConfig config = context.Config;
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
            config,
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
        (int Index, int CompletedUnits)[] admissions = [
            .. context.Cadence.AdmissionCandidates
                .Select(candidate => (
                    Index: lineage[candidate.Address],
                    CompletedUnits:
                        candidate.HistoryUnitCountSinceBaseline
                ))
                .Where(candidate =>
                    candidate.Index < latestPublishedIndex
                    && candidate.Index < newestAllowedStartIndex)
                .OrderByDescending(
                    static candidate => candidate.CompletedUnits
                )
                .ThenBy(static candidate => candidate.Index)
        ];
        if (admissions.Length == 0) {
            return Unavailable(
                RecapPlanDefectCodes.AdmissionInvalid,
                "Evaluator-provided cadence candidates are not newer "
                + "than the authorized source cursors."
            );
        }

        IReadOnlyList<RecapPlanDefect> lastDefects = [];
        foreach ((int admissionIndex, _) in admissions) {
            CandidateResult candidate = TryBuildCandidate(
                config,
                starts,
                replaySafe,
                admissionIndex
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
        RecapPlannerConfig config,
        RecapPolicyFacts facts,
        IReadOnlyDictionary<EventAddress, int> lineage
    ) {
        if (facts.EmptyReplayStartExclusive is { } emptyStart) {
            return [
                .. config.Catalog.Select(entry => new BlockStart(
                    entry,
                    new RecapPlanningMaintainSource.Empty(emptyStart),
                    lineage[emptyStart]
                ))
            ];
        }

        return [
            .. config.Catalog.Select((entry, index) => {
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
        RecapPlannerConfig config,
        IReadOnlyList<BlockStart> starts,
        IReadOnlyDictionary<int, EventAddress> replaySafe,
        int admissionIndex
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
                    + $"{config.MaxRawEventsPerStep}."
                );
                continue;
            }
            if (route.Count > config.MaxRouteEndpointsPerBlock) {
                AddOnce(
                    defects,
                    RecapPlanDefectCodes.RouteLimitExceeded,
                    $"Block '{start.Catalog.RecapBlockId}' requires "
                    + $"{route.Count} route endpoints; limit is "
                    + $"{config.MaxRouteEndpointsPerBlock}."
                );
            }

            calls += route.Count;
            rawEvents += start.LineageIndex - admissionIndex;
            blocks.Add(new RecapBlockPlanningDecision.Maintain(
                start.Catalog.RecapBlockId,
                start.Source,
                route,
                EmptyRecapPriorContext.Instance
            ));
        }

        if (calls > config.MaxMaintainerCallsPerBuild) {
            AddOnce(
                defects,
                RecapPlanDefectCodes.CallLimitExceeded,
                $"Plan requires {calls} Maintainer calls; limit is "
                + $"{config.MaxMaintainerCallsPerBuild}."
            );
        }
        if (rawEvents > config.MaxRawEventsPerBuild) {
            AddOnce(
                defects,
                RecapPlanDefectCodes.RawBuildLimitExceeded,
                $"Plan requires {rawEvents} maintained raw events; "
                + $"limit is {config.MaxRawEventsPerBuild}."
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
                            <= config.MaxRawEventsPerStep)
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
