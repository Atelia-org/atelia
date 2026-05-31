using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

public sealed record TextAdv2RuntimeRoutePlanObservation(
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    string Status,
    int StepCount,
    int? TotalTravelCost,
    TextAdv2RuntimeRoutePlanStepObservation[] Steps,
    TextAdv2RuntimeRoutePlanSearchStatsObservation SearchStats
);

public sealed record TextAdv2RuntimeRoutePlanSearchStatsObservation(
    string HeuristicName,
    int LandmarkCount,
    int ExpandedNodeCount,
    int RelaxedEdgeCount,
    int FrontierPeakSize,
    int StaleStateSkipCount
);

public sealed record TextAdv2RuntimeRoutePlanStepObservation(
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

internal static class TextAdv2RuntimeRoutePlanProjector {
    public static TextAdv2RuntimeRoutePlanObservation Project(LocationRoutePlanObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeRoutePlanObservation(
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

    private static TextAdv2RuntimeRoutePlanSearchStatsObservation ProjectSearchStats(
        LocationRoutePlanSearchStatsObservation observation
    ) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeRoutePlanSearchStatsObservation(
            observation.HeuristicName,
            observation.LandmarkCount,
            observation.ExpandedNodeCount,
            observation.RelaxedEdgeCount,
            observation.FrontierPeakSize,
            observation.StaleStateSkipCount
        );
    }

    private static TextAdv2RuntimeRoutePlanStepObservation ProjectStep(LocationRoutePlanStepObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeRoutePlanStepObservation(
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
