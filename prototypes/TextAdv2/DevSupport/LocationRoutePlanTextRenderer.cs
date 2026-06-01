using System.Text;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.DevSupport;

internal static class LocationRoutePlanTextRenderer {
    public static string Render(LocationRoutePlanObservation plan) {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new StringBuilder();
        builder.AppendLine(
            $"ROUTE PLAN from={plan.FromLocationId} ({plan.FromLocationName}) to={plan.ToLocationId} ({plan.ToLocationName}) status={plan.Status.ToWireToken()}"
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
}
