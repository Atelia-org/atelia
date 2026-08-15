using Atelia.TextAdv2.Runtime;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Observation;

internal static class ActorRuntimeStateObservationProjector {
    public static ActorRuntimeStateObservation ObserveActorRuntimeState(WorldState world, Actor actor) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(actor);

        return new ActorRuntimeStateObservation(
            ProjectCurrentActivity(world, actor),
            ProjectCarriedResources(actor)
        );
    }

    public static ActorActivityObservation ProjectCurrentActivity(WorldState world, Actor actor) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(actor);

        return actor.EmbodiedState switch {
            IdleActorEmbodiedState => new ActorActivityObservation(
                "idle",
                IsInterruptible: false,
                RouteFollowing: null,
                Mining: null
            ),
            RouteFollowingActorProcessState routeFollowing => new ActorActivityObservation(
                "route-following",
                routeFollowing.IsInterruptible,
                new ActorRouteFollowingActivityObservation(
                    routeFollowing.DestinationLocationId,
                    world.GetLocation(routeFollowing.DestinationLocationId).Name,
                    [.. routeFollowing.RemainingPassageIds],
                    routeFollowing.RemainingTravelTicksOnCurrentLeg
                ),
                Mining: null
            ),
            MiningActorProcessState mining => new ActorActivityObservation(
                "mining",
                mining.IsInterruptible,
                RouteFollowing: null,
                new ActorMiningActivityObservation(
                    mining.WorksiteId,
                    world.GetLocation(mining.WorksiteId).Name,
                    mining.ProgressTicksInCurrentCycle,
                    mining.TicksPerYield,
                    mining.YieldItemId,
                    mining.YieldAmount,
                    mining.ProducedYieldCount
                )
            ),
            _ => throw new InvalidOperationException(
                $"Actor '{actor.Id}' uses unsupported embodied state type '{actor.EmbodiedState.GetType().Name}'."
            ),
        };
    }

    public static ActorCarriedResourceObservation[] ProjectCarriedResources(Actor actor) {
        ArgumentNullException.ThrowIfNull(actor);

        return actor.Inventory
            .Select(static entry => new ActorCarriedResourceObservation(entry.ItemId, entry.Quantity))
            .ToArray();
    }
}
