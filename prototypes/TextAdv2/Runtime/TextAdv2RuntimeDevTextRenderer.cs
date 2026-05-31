using System.Text;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// 共享给宿主/dev-support 的轻量文本渲染器。
///
/// runtime public seam 只返回 typed DTO；若宿主仍需要 compact text，
/// 应显式调用这里，而不是回到 runtime 主对象要字符串别名。
/// </summary>
public static class TextAdv2RuntimeDevTextRenderer {
    public static string RenderWorld(TextAdv2Runtime runtime) {
        ArgumentNullException.ThrowIfNull(runtime);
        return WorldDumpRenderer.Render(runtime.WorldForDevSupport);
    }

    public static string RenderLocation(TextAdv2Runtime runtime, string locationId) {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return WorldDumpRenderer.RenderLocation(runtime.WorldForDevSupport, locationId);
    }

    public static string RenderCompactMovement(TextAdv2RuntimeActorMovementObservation movement) {
        ArgumentNullException.ThrowIfNull(movement);

        return $"{movement.ActorId}: {movement.FromLocationId} --{movement.ExitName}/{movement.PassageId}--> {movement.ToLocationId}"
            + $" | {movement.TravelMode} | cost={movement.TravelCost}";
    }

    public static string RenderRouteTrace(TextAdv2RuntimeActorRouteTraceObservation trace) {
        ArgumentNullException.ThrowIfNull(trace);

        var builder = new StringBuilder();
        builder.AppendLine($"ROUTE TRACE actor={trace.ActorId} name={trace.ActorName}");
        builder.AppendLine($"start={trace.StartLocationId} ({trace.StartLocationName})");

        if (trace.Steps.Length == 0) {
            builder.AppendLine("<no movement in this run>");
        }
        else {
            foreach (var step in trace.Steps) {
                builder.AppendLine(
                    $"{step.StepNumber}. {step.FromLocationId} --{step.ExitName}/{step.PassageId}--> {step.ToLocationId}"
                    + $" | {step.TravelMode} | cost={step.TravelCost}"
                );
            }
        }

        builder.Append(
            $"end={trace.EndLocationId} ({trace.EndLocationName}) | steps={trace.StepCount} | totalCost={trace.TotalTravelCost}"
        );
        return builder.ToString();
    }
}
