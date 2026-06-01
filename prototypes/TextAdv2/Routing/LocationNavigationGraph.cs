using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Routing;

/// <summary>
/// 仅供内部算法消费的最小 location graph 视图。
/// 它故意不携带名称、描述等展示字段，避免热路径耦合到更重的展示 DTO。
/// </summary>
internal sealed record LocationNavigationGraph(
    string LocationId,
    LocationNavigationGraphEdge[] Edges
);

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
