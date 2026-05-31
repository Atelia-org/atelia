using System.Text;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.ReadOnlyView;

/// <summary>
/// Location 级最短路规划结果。
///
/// 当前阶段显式区分三种结果：
/// - Found：找到了从起点到终点的可达路径；
/// - AlreadyThere：起点与终点相同；
/// - Unreachable：当前图上不存在可达路径。
///
/// 不使用“零步 + 总成本 0”同时表达多种语义，避免调用方必须靠猜测解读结果。
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
/// 这是 Location 级图上的一条有向边选择，并保留累计成本，方便人工核对路径是否符合预期。
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

public enum RoutePlanStatus {
    Found,
    AlreadyThere,
    Unreachable,
}

internal static class LocationRoutePlanTextRenderer {
    public static string Render(LocationRoutePlanObservation plan) {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new StringBuilder();
        builder.AppendLine(
            $"ROUTE PLAN from={plan.FromLocationId} ({plan.FromLocationName}) to={plan.ToLocationId} ({plan.ToLocationName}) status={ToText(plan.Status)}"
        );

        switch (plan.Status) {
            case RoutePlanStatus.AlreadyThere:
                builder.AppendLine("<already at destination>");
                break;
            case RoutePlanStatus.Unreachable:
                builder.AppendLine("<no route found>");
                break;
            case RoutePlanStatus.Found:
                foreach (var step in plan.Steps) {
                    builder.AppendLine(
                        $"{step.StepNumber}. {step.FromLocationId} --{step.ExitName}/{step.PassageId}--> {step.ToLocationId}"
                        + $" | {step.TravelMode.ToStorageValue()} | cost={step.TravelCost} | total={step.CumulativeTravelCost}"
                    );
                }
                break;
            default:
                throw new InvalidOperationException($"Unsupported route plan status '{plan.Status}'.");
        }

        builder.Append(
            $"summary: steps={plan.StepCount} | totalCost={(plan.TotalTravelCost is null ? "<unreachable>" : plan.TotalTravelCost.Value)}"
        );
        builder.AppendLine();
        builder.Append(
            $"search: heuristic={plan.SearchStats.HeuristicName} | landmarks={plan.SearchStats.LandmarkCount}"
            + $" | expanded={plan.SearchStats.ExpandedNodeCount} | relaxed={plan.SearchStats.RelaxedEdgeCount}"
            + $" | frontierPeak={plan.SearchStats.FrontierPeakSize} | staleSkips={plan.SearchStats.StaleStateSkipCount}"
        );
        return builder.ToString();
    }

    private static string ToText(RoutePlanStatus status)
        => RoutePlanStatusCodec.ToStorageValue(status);
}

internal static class RoutePlanStatusCodec {
    public static RoutePlanStatus FromStorageValue(string value)
        => value switch {
            "found" => RoutePlanStatus.Found,
            "already-there" => RoutePlanStatus.AlreadyThere,
            "unreachable" => RoutePlanStatus.Unreachable,
            _ => throw new InvalidOperationException($"Unknown route plan status '{value}'."),
        };

    public static string ToStorageValue(RoutePlanStatus value)
        => value switch {
            RoutePlanStatus.Found => "found",
            RoutePlanStatus.AlreadyThere => "already-there",
            RoutePlanStatus.Unreachable => "unreachable",
            _ => throw new InvalidOperationException($"Unsupported route plan status '{value}'."),
        };
}
