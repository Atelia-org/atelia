using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

public sealed record TextAdv2RuntimeLocationObservation(
    string LocationId,
    string LocationName,
    string LocationDescription,
    TextAdv2RuntimeExitObservation[] Exits,
    TextAdv2RuntimeActorPresenceObservation[] PresentActors
);

public sealed record TextAdv2RuntimeExitObservation(
    string PassageId,
    string ExitName,
    string TargetLocationId,
    string TargetLocationName,
    string TravelMode,
    int BaseTravelCost,
    int TravelCostModifier,
    int TotalTravelCost,
    string SharedConditionNote,
    string DirectionalConditionNote,
    string LocalViewNote,
    bool IsEnabled
);

public sealed record TextAdv2RuntimeActorPresenceObservation(string ActorId, string ActorName);

public sealed record TextAdv2RuntimeActorObservation(
    string ActorId,
    string ActorName,
    TextAdv2RuntimeLocationObservation Location
);

public sealed record TextAdv2RuntimeActorMovementObservation(
    string ActorId,
    string ActorName,
    string PassageId,
    string ExitName,
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    string TravelMode,
    int TravelCost,
    TextAdv2RuntimeLocationObservation CurrentLocation
);

public sealed record TextAdv2RuntimeLocationNavigationObservation(
    string LocationId,
    string LocationName,
    TextAdv2RuntimeNavigationEdgeObservation[] Edges
);

public sealed record TextAdv2RuntimeNavigationEdgeObservation(
    string PassageId,
    string ExitName,
    string TargetLocationId,
    string TargetLocationName,
    string TravelMode,
    int TravelCost
);

public sealed record TextAdv2RuntimeActorNavigationObservation(
    string ActorId,
    string ActorName,
    TextAdv2RuntimeLocationNavigationObservation Navigation
);

internal static class TextAdv2RuntimeObservationProjector {
    public static TextAdv2RuntimeLocationObservation ProjectLocation(WorldState world, string locationId)
        => ProjectLocation(LocationObservationProjector.ObserveLocation(world, locationId));

    public static TextAdv2RuntimeActorObservation ProjectActor(WorldState world, string actorId)
        => ProjectActor(LocationObservationProjector.ObserveActorLocation(world, actorId));

    public static TextAdv2RuntimeActorMovementObservation ProjectMovement(ActorMovementObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeActorMovementObservation(
            observation.ActorId,
            observation.ActorName,
            observation.PassageId,
            observation.ExitName,
            observation.FromLocationId,
            observation.FromLocationName,
            observation.ToLocationId,
            observation.ToLocationName,
            observation.TravelMode.ToStorageValue(),
            observation.TravelCost,
            ProjectLocation(observation.CurrentLocation)
        );
    }

    public static TextAdv2RuntimeLocationNavigationObservation ProjectNavigation(WorldState world, string locationId)
        => ProjectNavigation(NavigationObservationProjector.ObserveLocationNavigation(world, locationId));

    public static TextAdv2RuntimeActorNavigationObservation ProjectActorNavigation(WorldState world, string actorId)
        => ProjectActorNavigation(NavigationObservationProjector.ObserveActorNavigation(world, actorId));

    public static TextAdv2RuntimeLocationObservation ProjectLocation(LocationObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeLocationObservation(
            observation.LocationId,
            observation.LocationName,
            observation.LocationDescription,
            observation.Exits.Select(ProjectExit).ToArray(),
            observation.PresentActors.Select(ProjectActorPresence).ToArray()
        );
    }

    public static TextAdv2RuntimeActorObservation ProjectActor(ActorLocationObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeActorObservation(
            observation.ActorId,
            observation.ActorName,
            ProjectLocation(observation.Location)
        );
    }

    public static TextAdv2RuntimeLocationNavigationObservation ProjectNavigation(LocationNavigationObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeLocationNavigationObservation(
            observation.LocationId,
            observation.LocationName,
            observation.Edges.Select(ProjectNavigationEdge).ToArray()
        );
    }

    public static TextAdv2RuntimeActorNavigationObservation ProjectActorNavigation(ActorNavigationObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeActorNavigationObservation(
            observation.ActorId,
            observation.ActorName,
            ProjectNavigation(observation.Navigation)
        );
    }

    private static TextAdv2RuntimeExitObservation ProjectExit(ExitObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeExitObservation(
            observation.PassageId,
            observation.ExitName,
            observation.TargetLocationId,
            observation.TargetLocationName,
            observation.TravelMode.ToStorageValue(),
            observation.BaseTravelCost,
            observation.TravelCostModifier,
            observation.TotalTravelCost,
            observation.SharedConditionNote,
            observation.DirectionalConditionNote,
            observation.LocalViewNote,
            observation.IsEnabled
        );
    }

    private static TextAdv2RuntimeActorPresenceObservation ProjectActorPresence(ActorPresenceObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeActorPresenceObservation(observation.ActorId, observation.ActorName);
    }

    private static TextAdv2RuntimeNavigationEdgeObservation ProjectNavigationEdge(NavigationEdgeObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeNavigationEdgeObservation(
            observation.PassageId,
            observation.ExitName,
            observation.TargetLocationId,
            observation.TargetLocationName,
            observation.TravelMode.ToStorageValue(),
            observation.TravelCost
        );
    }
}
