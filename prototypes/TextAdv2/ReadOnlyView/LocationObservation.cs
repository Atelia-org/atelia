using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.ReadOnlyView;

/// <summary>
/// 面向调用方的地点观察结果。
///
/// 它是从 WorldTruth 投影出来的只读结构化数据，不持有第二份业务真相。
/// 当前阶段刻意保持非文学化，便于测试、调试、日志和后续算法复用。
/// </summary>
internal sealed record LocationObservation(
    string LocationId,
    string LocationName,
    string LocationDescription,
    ExitObservation[] Exits,
    ActorPresenceObservation[] PresentActors
);

/// <summary>
/// 从某个地点看出去的一条出口观察。
/// 这是面向读取方的稳定结构，不要求读取方再去反查 Passage 才知道基础移动语义。
/// </summary>
internal sealed record ExitObservation(
    string PassageId,
    string ExitName,
    string TargetLocationId,
    string TargetLocationName,
    TravelMode TravelMode,
    int BaseTravelCost,
    int TravelCostModifier,
    int TotalTravelCost,
    string SharedConditionNote,
    string DirectionalConditionNote,
    string LocalViewNote,
    bool IsEnabled
);

/// <summary>
/// 当前地点内可见的 actor 占位信息。
/// 当前阶段只暴露身份与名字；更复杂的可见性与呈现策略留待后续层处理。
/// </summary>
internal sealed record ActorPresenceObservation(string ActorId, string ActorName);

/// <summary>
/// 基于 actor 的位置观察结果。
/// 这是“actor 站在何处，因此现在看到什么”的直接结构化投影。
/// </summary>
internal sealed record ActorLocationObservation(string ActorId, string ActorName, LocationObservation Location);
