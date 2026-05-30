using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.ReadOnlyView;

/// <summary>
/// 面向导航算法与路线调试的轻量只读视图。
///
/// 与 <see cref="LocationObservation"/> 相比，这里只保留“从当前地点能沿哪些已启用边走出去”所需的最小稳定字段，
/// 以免后续 A* 直接耦合到更重的观察 DTO。
/// </summary>
internal sealed record LocationNavigationObservation(
    string LocationId,
    string LocationName,
    NavigationEdgeObservation[] Edges
);

/// <summary>
/// 单条可遍历导航边。
/// 这是 Location 级图上的一条有向边，边权直接使用 passage 的当前总 travel cost。
/// </summary>
internal sealed record NavigationEdgeObservation(
    string PassageId,
    string ExitName,
    string TargetLocationId,
    string TargetLocationName,
    TravelMode TravelMode,
    int TravelCost
);

/// <summary>
/// 仅供内部算法消费的最小 location graph 视图。
/// 它故意不携带名称、描述等展示字段，避免热路径耦合到更重的展示 DTO。
/// </summary>
internal sealed record LocationNavigationGraphObservation(
    string LocationId,
    NavigationGraphEdgeObservation[] Edges
);

/// <summary>
/// 算法层使用的最小有向边。
/// 只保留最短路、heuristic 和 graph signature 所需的稳定字段。
/// </summary>
internal sealed record NavigationGraphEdgeObservation(
    string PassageId,
    string TargetLocationId,
    TravelMode TravelMode,
    int TravelCost
);

/// <summary>
/// 基于 actor 当前所在位置的导航视图。
/// 供“角色此刻能往哪走”这类逻辑直接消费。
/// </summary>
internal sealed record ActorNavigationObservation(
    string ActorId,
    string ActorName,
    LocationNavigationObservation Navigation
);
