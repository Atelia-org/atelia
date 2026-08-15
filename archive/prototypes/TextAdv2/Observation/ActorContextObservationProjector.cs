using Atelia.TextAdv2.Runtime;
using Atelia.TextAdv2.ReadModel;
using Atelia.TextAdv2.Spatial;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Observation;

/// <summary>
/// 直接从 world truth 与 spatial seam 投影 actor-facing context。
///
/// 它不是 LocationObservation 或 ActorNavigationObservation 的包壳，
/// 而是一等 read model：一次性收口 actor 身份、当前逻辑时间、窄地点上下文与 available moves。
/// </summary>
internal static class ActorContextObservationProjector {
    /// <summary>
    /// 从调用方提供的 spatial snapshot 投影 actor context。
    /// 调用方应保证 snapshot 来自同一个、且未在此期间发生空间变更的 <see cref="WorldState"/>。
    /// </summary>
    public static ActorContextObservation ObserveActorContext(
        WorldState world,
        WorldSpatialSnapshot spatial,
        ActorOccupancyIndex occupancy,
        string actorId
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(spatial);
        ArgumentNullException.ThrowIfNull(occupancy);
        WorldState.ValidateEntityId(actorId, nameof(actorId));

        var actor = world.GetActor(actorId);
        var location = world.GetLocation(actor.CurrentLocationId);
        var presentActors = LocationObservationProjector.ProjectPresentActors(world, occupancy, location.Id);
        var currentLocation = new ActorContextLocationObservation(
            location.Id,
            location.Name,
            location.Description,
            presentActors
        );
        var adjacency = spatial.Locations[location.Id];
        var availableMoves = NavigationObservationProjector.ProjectNavigationEdges(world, adjacency);
        var runtimeState = ActorRuntimeStateObservationProjector.ObserveActorRuntimeState(world, actor);

        return new ActorContextObservation(
            actor.Id,
            actor.Name,
            world.CurrentLogicalTick,
            currentLocation,
            availableMoves,
            runtimeState.CurrentActivity,
            runtimeState.CarriedResources
        );
    }

    public static ActorContextObservation ObserveActorContext(
        WorldState world,
        WorldSpatialSnapshot spatial,
        string actorId
    ) {
        ArgumentNullException.ThrowIfNull(world);
        return ObserveActorContext(world, spatial, ActorOccupancyIndex.Build(world), actorId);
    }

    public static ActorContextObservation ObserveActorContext(WorldState world, string actorId) {
        ArgumentNullException.ThrowIfNull(world);
        var spatial = WorldSpatialSnapshotBuilder.Build(world);
        var occupancy = ActorOccupancyIndex.Build(world);
        return ObserveActorContext(world, spatial, occupancy, actorId);
    }
}
