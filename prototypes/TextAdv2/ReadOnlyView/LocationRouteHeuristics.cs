namespace Atelia.TextAdv2.ReadOnlyView;

internal sealed record LocationRouteHeuristicObservation(string Name, int LandmarkCount);

/// <summary>
/// 最短路 heuristic 的最小接缝。
/// 实现必须返回非负 lower bound；拿不准时应返回 0，而不是猜测更激进的值。
/// </summary>
internal interface ILocationRouteHeuristic {
    int EstimateRemainingCost(string currentLocationId, string targetLocationId);

    LocationRouteHeuristicObservation Observe();
}

internal static class LocationRouteHeuristics {
    public static ILocationRouteHeuristic Zero { get; } = new ZeroLocationRouteHeuristic();

    private sealed class ZeroLocationRouteHeuristic : ILocationRouteHeuristic {
        public int EstimateRemainingCost(string currentLocationId, string targetLocationId) {
            _ = currentLocationId;
            _ = targetLocationId;
            return 0;
        }

        public LocationRouteHeuristicObservation Observe() => new("zero", 0);
    }
}
