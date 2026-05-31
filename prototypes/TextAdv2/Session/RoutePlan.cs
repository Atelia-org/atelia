using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Session;

public sealed record RoutePlan(
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    string Status,
    int StepCount,
    int? TotalTravelCost,
    RoutePlanStep[] Steps,
    RoutePlanSearchStats SearchStats
);

public sealed record RoutePlanSearchStats(
    string HeuristicName,
    int LandmarkCount,
    int ExpandedNodeCount,
    int RelaxedEdgeCount,
    int FrontierPeakSize,
    int StaleStateSkipCount
);

public sealed record RoutePlanStep(
    int StepNumber,
    string PassageId,
    string ExitName,
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    string TravelMode,
    int TravelCost,
    int CumulativeTravelCost
);

internal static class SessionRoutePlanProjector {
    public static RoutePlan Project(LocationRoutePlanObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new RoutePlan(
            observation.FromLocationId,
            observation.FromLocationName,
            observation.ToLocationId,
            observation.ToLocationName,
            ProjectStatus(observation.Status),
            observation.StepCount,
            observation.TotalTravelCost,
            observation.Steps.Select(ProjectStep).ToArray(),
            ProjectSearchStats(observation.SearchStats)
        );
    }

    private static RoutePlanSearchStats ProjectSearchStats(
        LocationRoutePlanSearchStatsObservation observation
    ) {
        ArgumentNullException.ThrowIfNull(observation);

        return new RoutePlanSearchStats(
            observation.HeuristicName,
            observation.LandmarkCount,
            observation.ExpandedNodeCount,
            observation.RelaxedEdgeCount,
            observation.FrontierPeakSize,
            observation.StaleStateSkipCount
        );
    }

    private static RoutePlanStep ProjectStep(LocationRoutePlanStepObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new RoutePlanStep(
            observation.StepNumber,
            observation.PassageId,
            observation.ExitName,
            observation.FromLocationId,
            observation.FromLocationName,
            observation.ToLocationId,
            observation.ToLocationName,
            observation.TravelMode.ToStorageValue(),
            observation.TravelCost,
            observation.CumulativeTravelCost
        );
    }

    private static string ProjectStatus(RoutePlanStatus status)
        => status switch {
            RoutePlanStatus.Found => "found",
            RoutePlanStatus.AlreadyThere => "already-there",
            RoutePlanStatus.Unreachable => "unreachable",
            _ => throw new InvalidOperationException($"Unsupported route plan status '{status}'."),
        };
}
