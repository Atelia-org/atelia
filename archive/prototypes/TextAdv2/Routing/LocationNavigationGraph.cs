using Atelia.TextAdv2.Spatial;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Routing;

/// <summary>
/// 仅供内部算法消费的最小 location graph 视图。
/// 它故意不携带名称、描述等展示字段，避免热路径耦合到更重的展示 DTO。
///
/// 重构后，它不再从 WorldState 直接产生，而是从 <see cref="LocationAdjacencySnapshot"/> 过滤 IsEnabled 后得到。
/// WorldSpatialSnapshot 是 canonical spatial seam；LocationNavigationGraph 是 routing-only view。
/// </summary>
internal sealed record LocationNavigationGraph(
    string LocationId,
    LocationNavigationGraphEdge[] Edges
)
{
    /// <summary>
    /// 从 spatial seam 的 adjacency snapshot 构建 routing-only 最小图。
    /// 只保留 IsEnabled 的边，并按 (TargetLocationId, PassageId, TravelCost) 稳定排序以保持与旧实现的兼容性。
    /// </summary>
    internal static LocationNavigationGraph FromAdjacency(LocationAdjacencySnapshot adjacency)
    {
        ArgumentNullException.ThrowIfNull(adjacency);

        var edges = adjacency.Edges
            .Where(e => e.IsEnabled)
            .Select(e => new LocationNavigationGraphEdge(
                e.PassageId,
                e.ToLocationId,
                e.TravelMode,
                e.TotalTravelCost
            ))
            .OrderBy(e => e.TargetLocationId, StringComparer.Ordinal)
            .ThenBy(e => e.PassageId, StringComparer.Ordinal)
            .ThenBy(e => e.TravelCost, Comparer<int>.Default)
            .ToArray();

        return new LocationNavigationGraph(adjacency.LocationId, edges);
    }
}

/// <summary>
/// 算法层使用的最小有向边。
/// 只保留最短路、heuristic 和 graph signature 所需的稳定字段。
/// </summary>
internal sealed record LocationNavigationGraphEdge(
    string PassageId,
    string TargetLocationId,
    TravelMode TravelMode,
    int TravelCost
);
