using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.ReadOnlyView;

/// <summary>
/// 面向导航算法与路线调试的轻量只读视图。
///
/// 与 <see cref="LocationObservation"/> 相比，这里只保留“从当前地点能沿哪些已启用边走出去”所需的最小稳定字段，
/// 以免后续 A* 直接耦合到更重的观察 DTO。
/// </summary>
public sealed record LocationNavigationObservation(
    string LocationId,
    string LocationName,
    NavigationEdgeObservation[] Edges
);

/// <summary>
/// 单条可遍历导航边。
/// 这是 Location 级图上的一条有向边，边权直接使用 passage 的当前总 travel cost。
/// </summary>
public sealed record NavigationEdgeObservation(
    string PassageId,
    string ExitName,
    string TargetLocationId,
    string TargetLocationName,
    TravelMode TravelMode,
    int TravelCost
);

/// <summary>
/// 基于 actor 当前所在位置的导航视图。
/// 供“角色此刻能往哪走”这类逻辑直接消费。
/// </summary>
public sealed record ActorNavigationObservation(
    string ActorId,
    string ActorName,
    LocationNavigationObservation Navigation
);
