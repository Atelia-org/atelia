using System.Text.Json.Serialization;

namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// actor 当前运行时身体状态的 machine-facing snapshot。
///
/// 它把“当前具身活动”和“当前携带资源”收口为单个 read model，
/// 供 runtime mutation API 返回，而不是直接泄漏 world truth draft types。
/// </summary>
public sealed record ActorRuntimeStateObservation(
    ActorActivityObservation CurrentActivity,
    ActorCarriedResourceObservation[] CarriedResources
);

/// <summary>
/// actor 当前具身活动的 machine-facing snapshot。
///
/// 它刻意不直接泄漏 world truth 内部类型，而是暴露稳定的 observation contract。
/// </summary>
public sealed record ActorActivityObservation(
    string Kind,
    bool IsInterruptible,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ActorRouteFollowingActivityObservation? RouteFollowing,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ActorMiningActivityObservation? Mining
);

public sealed record ActorRouteFollowingActivityObservation(
    string DestinationLocationId,
    string DestinationLocationName,
    string[] RemainingPassageIds,
    int RemainingTravelTicksOnCurrentLeg
);

public sealed record ActorMiningActivityObservation(
    string WorksiteId,
    string WorksiteName,
    int ProgressTicksInCurrentCycle,
    int TicksPerYield,
    string YieldItemId,
    int YieldAmount,
    long ProducedYieldCount
);

public sealed record ActorCarriedResourceObservation(
    string ItemId,
    long Quantity
);
