using Atelia.TextAdv2.ReadModel;
using Atelia.TextAdv2.Spatial;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Observation;

/// <summary>
/// 把 WorldTruth 投影为稳定的 LocationObservation / ActorLocationObservation。
///
/// 该组件 MUST 保持只读：
/// - 不回写世界真相；
/// - 不引入缓存；
/// - 不替调用方做文学化润色。
/// </summary>
internal static class LocationObservationProjector {

    internal static ActorPresenceObservation[] ProjectPresentActors(
        WorldState world,
        ActorOccupancyIndex occupancy,
        string locationId
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(occupancy);
        WorldState.ValidateEntityId(locationId, nameof(locationId));

        return occupancy.EnumerateActorIdsAtLocation(locationId)
            .Select(world.GetActor)
            .Select(actor => new ActorPresenceObservation(actor.Id, actor.Name))
            .ToArray();
    }

    /// <summary>
    /// 从调用方提供的 spatial snapshot 投影地点观察。
    /// 调用方应保证 snapshot 来自同一个、且未在此期间发生空间变更的 <see cref="WorldState"/>。
    /// </summary>
    public static LocationObservation ObserveLocation(
        WorldState world,
        WorldSpatialSnapshot spatial,
        ActorOccupancyIndex occupancy,
        string locationId
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(spatial);
        ArgumentNullException.ThrowIfNull(occupancy);
        WorldState.ValidateEntityId(locationId, nameof(locationId));

        var location = world.GetLocation(locationId);
        var adjacency = spatial.Locations[locationId];
        var exits = adjacency.Edges
            .Select(edge => {
                var targetLocation = world.GetLocation(edge.ToLocationId);

                return new ExitObservation(
                    edge.PassageId,
                    edge.ExitName,
                    edge.ToLocationId,
                    targetLocation.Name,
                    edge.TravelMode,
                    edge.BaseTravelCost,
                    edge.TravelCostModifier,
                    edge.TotalTravelCost,
                    edge.SharedConditionNote,
                    edge.DirectionConditionNote,
                    edge.LocalViewNote,
                    edge.IsEnabled
                );
            })
            .OrderBy(exit => exit.ExitName, StringComparer.Ordinal)
            .ThenBy(exit => exit.PassageId, StringComparer.Ordinal)
            .ToArray();
        var presentActors = ProjectPresentActors(world, occupancy, locationId);

        return new LocationObservation(location.Id, location.Name, location.Description, exits, presentActors);
    }

    public static LocationObservation ObserveLocation(
        WorldState world,
        WorldSpatialSnapshot spatial,
        string locationId
    ) {
        ArgumentNullException.ThrowIfNull(world);
        return ObserveLocation(world, spatial, ActorOccupancyIndex.Build(world), locationId);
    }

    public static LocationObservation ObserveLocation(WorldState world, string locationId) {
        ArgumentNullException.ThrowIfNull(world);
        var spatial = WorldSpatialSnapshotBuilder.Build(world);
        var occupancy = ActorOccupancyIndex.Build(world);
        return ObserveLocation(world, spatial, occupancy, locationId);
    }

    /// <summary>
    /// 从调用方提供的 spatial snapshot 投影 actor 当前所在地点观察。
    /// 调用方应保证 snapshot 来自同一个、且未在此期间发生空间变更的 <see cref="WorldState"/>。
    /// </summary>
    public static ActorLocationObservation ObserveActorLocation(
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
        return new ActorLocationObservation(actor.Id, actor.Name, ObserveLocation(world, spatial, occupancy, actor.CurrentLocationId));
    }

    public static ActorLocationObservation ObserveActorLocation(WorldState world, string actorId) {
        ArgumentNullException.ThrowIfNull(world);
        var spatial = WorldSpatialSnapshotBuilder.Build(world);
        var occupancy = ActorOccupancyIndex.Build(world);
        return ObserveActorLocation(world, spatial, occupancy, actorId);
    }
}
