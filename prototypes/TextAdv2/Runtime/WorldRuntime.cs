using Atelia.TextAdv2.Routing;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

internal sealed class WorldRuntime {
    private readonly RouteAccelerationCache _routeAcceleration = new();
    private readonly Dictionary<string, List<ActorMovementHistoryEntry>> _movementHistoryByActor = new(StringComparer.Ordinal);

    public RuntimeEpochId EpochId { get; } = RuntimeEpochId.CreateNew();

    public RouteAccelerationSnapshot ObserveRouteAcceleration(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);
        return _routeAcceleration.Observe(world);
    }

    public LocationRoutePlanningOptions? GetPlanningOptions(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);
        return _routeAcceleration.GetPlanningOptions(world);
    }

    public RouteAccelerationSnapshot RebuildRouteAcceleration(
        WorldState world,
        IEnumerable<string> landmarkLocationIds,
        string landmarkProfileName
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(landmarkLocationIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(landmarkProfileName);

        return _routeAcceleration.Rebuild(world, landmarkLocationIds, landmarkProfileName);
    }

    public void RecordMovement(ActorMovementObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        GetOrCreateMovementHistory(observation.ActorId).Add(
            new ActorMovementHistoryEntry(
                observation.PassageId,
                observation.ExitName,
                observation.FromLocationId,
                observation.FromLocationName,
                observation.ToLocationId,
                observation.ToLocationName,
                observation.TravelMode,
                observation.TravelCost
            )
        );
    }

    public ActorRuntimeRouteTraceObservation ObserveActorRuntimeRouteTrace(WorldState world, string actorId) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return ActorRuntimeRouteTraceObservationProjector.ObserveActorRuntimeRouteTrace(
            EpochId,
            world,
            actorId,
            GetMovementHistory(actorId)
        );
    }

    private IReadOnlyList<ActorMovementHistoryEntry> GetMovementHistory(string actorId)
        => _movementHistoryByActor.TryGetValue(actorId, out var history) ? history : [];

    private List<ActorMovementHistoryEntry> GetOrCreateMovementHistory(string actorId) {
        if (!_movementHistoryByActor.TryGetValue(actorId, out var history)) {
            history = [];
            _movementHistoryByActor.Add(actorId, history);
        }

        return history;
    }
}
