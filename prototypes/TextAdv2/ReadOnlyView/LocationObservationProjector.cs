using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.ReadOnlyView;

/// <summary>
/// 把 WorldTruth 投影为稳定的 LocationObservation / ActorLocationObservation。
///
/// 该组件 MUST 保持只读：
/// - 不回写世界真相；
/// - 不引入缓存；
/// - 不替调用方做文学化润色。
/// </summary>
internal static class LocationObservationProjector {
    public static LocationObservation ObserveLocation(WorldState world, string locationId) {
        ArgumentNullException.ThrowIfNull(world);
        WorldState.ValidateEntityId(locationId, nameof(locationId));

        var location = world.GetLocation(locationId);
        var exits = world.EnumeratePassagesTouching(locationId)
            .Select(passage => {
                var endpoint = passage.GetEndpointFor(locationId);
                var direction = passage.GetDirectionFrom(locationId);
                var targetLocation = world.GetLocation(passage.GetOtherLocationId(locationId));

                return new ExitObservation(
                    passage.Id,
                    endpoint.ExitName,
                    targetLocation.Id,
                    targetLocation.Name,
                    passage.TravelMode,
                    passage.BaseTravelCost,
                    direction.TravelCostModifier,
                    passage.GetTotalTravelCostFrom(locationId),
                    passage.SharedConditionNote,
                    direction.DirectionConditionNote,
                    endpoint.LocalViewNote,
                    direction.IsEnabled
                );
            })
            .OrderBy(exit => exit.ExitName, StringComparer.Ordinal)
            .ThenBy(exit => exit.PassageId, StringComparer.Ordinal)
            .ToArray();
        var presentActors = world.EnumerateActorsAtLocation(locationId)
            .OrderBy(actor => actor.Id, StringComparer.Ordinal)
            .Select(actor => new ActorPresenceObservation(actor.Id, actor.Name))
            .ToArray();

        return new LocationObservation(location.Id, location.Name, location.Description, exits, presentActors);
    }

    public static ActorLocationObservation ObserveActorLocation(WorldState world, string actorId) {
        ArgumentNullException.ThrowIfNull(world);
        WorldState.ValidateEntityId(actorId, nameof(actorId));

        var actor = world.GetActor(actorId);
        return new ActorLocationObservation(actor.Id, actor.Name, ObserveLocation(world, actor.CurrentLocationId));
    }
}
