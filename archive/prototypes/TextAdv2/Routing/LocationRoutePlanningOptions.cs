namespace Atelia.TextAdv2.Routing;

/// <summary>
/// 最短路规划选项。
/// 当前只开放 heuristic seam；默认仍使用零启发，保证行为与原先 Dijkstra 实现一致。
/// </summary>
internal sealed class LocationRoutePlanningOptions {
    public static LocationRoutePlanningOptions Default { get; } = new(LocationRouteHeuristics.Zero);

    public LocationRoutePlanningOptions(ILocationRouteHeuristic heuristic) {
        Heuristic = heuristic ?? throw new ArgumentNullException(nameof(heuristic));
    }

    public ILocationRouteHeuristic Heuristic { get; }

    public static LocationRoutePlanningOptions Resolve(LocationRoutePlanningOptions? options) => options ?? Default;
}
