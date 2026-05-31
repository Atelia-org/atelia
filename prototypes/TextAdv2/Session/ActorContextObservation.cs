using Atelia.TextAdv2.ReadOnlyView;

namespace Atelia.TextAdv2.Session;

/// <summary>
/// 面向 machine-consumable 调用方的 actor 主 context。
///
/// 它收口为：
/// - actor 身份；
/// - 当前逻辑时间；
/// - 当前地点观察；
/// - 当前可走导航边。
/// </summary>
public sealed record ActorContextObservation(
    string ActorId,
    string ActorName,
    long CurrentTick,
    LocationObservation CurrentLocation,
    NavigationEdgeObservation[] AvailableMoves
);
