using System.Text;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.ReadOnlyView;

/// <summary>
/// 一次成功移动后的结构化结果。
///
/// 它不是持久化世界真相，而是当前运行时对一次合法移动的只读记录，
/// 供 CLI 调试、trace 汇总和后续 route replay 使用。
/// </summary>
internal sealed record ActorMovementObservation(
    string ActorId,
    string ActorName,
    string PassageId,
    string ExitName,
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    TravelMode TravelMode,
    int TravelCost,
    LocationObservation CurrentLocation
);

/// <summary>
/// runtime 内部维护的轻量移动历史。
///
/// 它只保留 route trace 真正需要的稳定字段，属于本次运行的易失调试态，
/// 不持有完整 LocationObservation，也不承诺跨 reopen 恢复。
/// </summary>
internal sealed record ActorMovementHistoryEntry(
    string PassageId,
    string ExitName,
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    TravelMode TravelMode,
    int TravelCost
);

/// <summary>
/// 单次 CLI 运行期间累计得到的 actor 路线 trace。
/// 其输入是“当前 actor 位置 + 本次运行内已发生的移动序列”。
/// </summary>
internal sealed record ActorRouteTraceObservation(
    string ActorId,
    string ActorName,
    string StartLocationId,
    string StartLocationName,
    string EndLocationId,
    string EndLocationName,
    int StepCount,
    int TotalTravelCost,
    ActorRouteTraceStepObservation[] Steps
);

/// <summary>
/// Route trace 中的一步。
/// 保留足够的稳定字段以便断言和人工检查，但避免像完整 LocationObservation 那样过于冗长。
/// </summary>
internal sealed record ActorRouteTraceStepObservation(
    int StepNumber,
    string PassageId,
    string ExitName,
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    TravelMode TravelMode,
    int TravelCost
);

internal static class ActorRouteTraceProjector {
    public static ActorRouteTraceObservation ObserveActorRouteTrace(
        WorldState world,
        string actorId,
        IReadOnlyList<ActorMovementHistoryEntry> movementHistory
    ) {
        ArgumentNullException.ThrowIfNull(world);
        WorldState.ValidateEntityId(actorId, nameof(actorId));
        ArgumentNullException.ThrowIfNull(movementHistory);

        var actor = world.GetActor(actorId);
        var endLocation = world.GetLocation(actor.CurrentLocationId);
        var startLocationId = movementHistory.Count > 0 ? movementHistory[0].FromLocationId : endLocation.Id;
        var startLocationName = movementHistory.Count > 0 ? movementHistory[0].FromLocationName : endLocation.Name;
        var steps = movementHistory
            .Select((movement, index) => new ActorRouteTraceStepObservation(
                index + 1,
                movement.PassageId,
                movement.ExitName,
                movement.FromLocationId,
                movement.FromLocationName,
                movement.ToLocationId,
                movement.ToLocationName,
                movement.TravelMode,
                movement.TravelCost
            ))
            .ToArray();

        return new ActorRouteTraceObservation(
            actor.Id,
            actor.Name,
            startLocationId,
            startLocationName,
            endLocation.Id,
            endLocation.Name,
            steps.Length,
            steps.Sum(step => step.TravelCost),
            steps
        );
    }
}

internal static class ActorRouteTraceTextRenderer {
    public static string Render(ActorRouteTraceObservation trace) {
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
                    + $" | {step.TravelMode.ToStorageValue()} | cost={step.TravelCost}"
                );
            }
        }

        builder.Append(
            $"end={trace.EndLocationId} ({trace.EndLocationName}) | steps={trace.StepCount} | totalCost={trace.TotalTravelCost}"
        );
        return builder.ToString();
    }
}
