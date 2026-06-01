using System.Text;
using Atelia.TextAdv2.Runtime;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.DevSupport;

/// <summary>
/// 共享给宿主/dev-support 的轻量文本渲染器。
///
/// 公开 runtime API 只返回 typed DTO；若宿主仍需要 compact text，
/// 应显式调用这里，而不是把字符串别名塞回 runtime façade。
/// </summary>
public static class DevTextRenderer {
    public static string RenderWorld(SerialWorldRuntime session) {
        ArgumentNullException.ThrowIfNull(session);
        return WorldDumpRenderer.Render(session.Host.DurableWorld);
    }

    public static string RenderLocation(SerialWorldRuntime session, string locationId) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return WorldDumpRenderer.RenderLocation(session.Host.DurableWorld, locationId);
    }

    public static string RenderCompactMovement(ActorMoveResult movement) {
        ArgumentNullException.ThrowIfNull(movement);

        return $"{movement.ActorId}: {movement.FromLocationId} --{movement.ExitName}/{movement.PassageId}--> {movement.ToLocationId}"
            + $" | {movement.TravelMode.ToStorageValue()} | cost={movement.TravelCost}";
    }

    public static string RenderRuntimeRouteTrace(ActorRuntimeRouteTrace trace) {
        ArgumentNullException.ThrowIfNull(trace);

        var builder = new StringBuilder();
        builder.AppendLine($"RUNTIME ROUTE TRACE epoch={trace.RuntimeEpochId} actor={trace.ActorId} name={trace.ActorName}");
        builder.AppendLine($"start={trace.StartLocationId} ({trace.StartLocationName})");

        if (trace.Steps.Length == 0) {
            builder.AppendLine("<no movement in this runtime>");
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
