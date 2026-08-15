using Atelia.TextAdv2.Observation;

namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// actor context 中的窄地点观察。
///
/// 它保留 actor 当前所处地点的描述性上下文，
/// 但不重复暴露地点级 exits；actor-facing 的可行动面统一走 <see cref="ActorContextObservation.AvailableMoves"/>。
/// </summary>
public sealed record ActorContextLocationObservation(
    string LocationId,
    string LocationName,
    string LocationDescription,
    ActorPresenceObservation[] PresentActors
);

/// <summary>
/// 面向 machine-consumable 调用方的 actor 主 context。
///
/// 它收口为：
/// - actor 身份；
/// - 当前逻辑时间；
/// - 当前地点的描述性上下文；
/// - 当前可走导航边（唯一 canonical actor-facing action surface）；
/// - 当前具身活动；
/// - 当前携带资源。
/// </summary>
public sealed record ActorContextObservation(
    string ActorId,
    string ActorName,
    long CurrentTick,
    ActorContextLocationObservation CurrentLocation,
    NavigationEdgeObservation[] AvailableMoves,
    ActorActivityObservation CurrentActivity,
    ActorCarriedResourceObservation[] CarriedResources
);
