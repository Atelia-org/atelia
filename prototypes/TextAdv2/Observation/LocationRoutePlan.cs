using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Observation;

/// <summary>
/// Machine-facing route-plan observation contract。
///
/// 当前阶段显式区分三种结果：
/// - Found：找到了从起点到终点的可达路径；
/// - AlreadyThere：起点与终点相同；
/// - Unreachable：当前图上不存在可达路径。
///
/// `LocationRoutePlanner` 当前有意直接产出该 contract；
/// wire token / JSON codec / dev text rendering 则留在相邻 helper 中。
/// </summary>
public sealed record LocationRoutePlanObservation(
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    RoutePlanStatus Status,
    int StepCount,
    int? TotalTravelCost,
    LocationRoutePlanStepObservation[] Steps,
    LocationRoutePlanSearchStatsObservation SearchStats
);

public sealed record LocationRoutePlanSearchStatsObservation(
    string HeuristicName,
    int LandmarkCount,
    int ExpandedNodeCount,
    int RelaxedEdgeCount,
    int FrontierPeakSize,
    int StaleStateSkipCount
);

/// <summary>
/// 规划结果中的一步。
/// 这是 Location 级图上的一条有向边选择，并保留累计成本，方便 machine consumer 与开发期人工核对路径。
/// </summary>
public sealed record LocationRoutePlanStepObservation(
    int StepNumber,
    string PassageId,
    string ExitName,
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    TravelMode TravelMode,
    int TravelCost,
    int CumulativeTravelCost
);

/// <summary>
/// Route-plan observation contract 的 machine-facing result status。
/// </summary>
public enum RoutePlanStatus {
    Found,
    AlreadyThere,
    Unreachable,
}
