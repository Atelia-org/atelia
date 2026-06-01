using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// runtime 内部维护的轻量移动历史。
///
/// 它只保留 runtime route trace 真正需要的稳定字段，属于本次运行的易失调试态，
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
/// 单次 runtime 期间累计得到的 actor runtime route trace。
/// 其输入是“当前 actor 位置 + 本次 runtime 内已发生的移动序列”。
/// </summary>
internal sealed record ActorRuntimeRouteTraceObservation(
    RuntimeEpochId RuntimeEpochId,
    string ActorId,
    string ActorName,
    string StartLocationId,
    string StartLocationName,
    string EndLocationId,
    string EndLocationName,
    int StepCount,
    int TotalTravelCost,
    ActorRuntimeRouteTraceStepObservation[] Steps
);

/// <summary>
/// Runtime route trace 中的一步。
/// 保留足够的稳定字段以便断言和人工检查，但避免像完整 LocationObservation 那样过于冗长。
/// </summary>
internal sealed record ActorRuntimeRouteTraceStepObservation(
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

internal static class ActorRuntimeRouteTraceObservationProjector {
    public static ActorRuntimeRouteTraceObservation ObserveActorRuntimeRouteTrace(
        RuntimeEpochId runtimeEpochId,
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
            .Select((movement, index) => new ActorRuntimeRouteTraceStepObservation(
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

        return new ActorRuntimeRouteTraceObservation(
            runtimeEpochId,
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
