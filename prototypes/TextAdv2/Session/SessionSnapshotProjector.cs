using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Session;

public sealed record LocationSnapshot(
    string LocationId,
    string LocationName,
    string LocationDescription,
    ExitSnapshot[] Exits,
    ActorPresenceSnapshot[] PresentActors
);

public sealed record ExitSnapshot(
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

public sealed record ActorPresenceSnapshot(string ActorId, string ActorName);

public sealed record ActorSnapshot(
    string ActorId,
    string ActorName,
    LocationSnapshot Location
);

public sealed record ActorMoveResult(
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
    LocationSnapshot CurrentLocation
);

public sealed record LocationNavigationSnapshot(
    string LocationId,
    string LocationName,
    NavigationEdgeSnapshot[] Edges
);

public sealed record NavigationEdgeSnapshot(
    string PassageId,
    string ExitName,
    string TargetLocationId,
    string TargetLocationName,
    string TravelMode,
    int TravelCost
);

public sealed record ActorNavigationSnapshot(
    string ActorId,
    string ActorName,
    LocationNavigationSnapshot Navigation
);

internal static class SessionSnapshotProjector {
    public static LocationSnapshot ProjectLocation(WorldState world, string locationId)
        => ProjectLocation(LocationObservationProjector.ObserveLocation(world, locationId));

    public static ActorSnapshot ProjectActor(WorldState world, string actorId)
        => ProjectActor(LocationObservationProjector.ObserveActorLocation(world, actorId));

    public static ActorMoveResult ProjectMovement(ActorMovementObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new ActorMoveResult(
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

    public static LocationNavigationSnapshot ProjectNavigation(WorldState world, string locationId)
        => ProjectNavigation(NavigationObservationProjector.ObserveLocationNavigation(world, locationId));

    public static ActorNavigationSnapshot ProjectActorNavigation(WorldState world, string actorId)
        => ProjectActorNavigation(NavigationObservationProjector.ObserveActorNavigation(world, actorId));

    public static LocationSnapshot ProjectLocation(LocationObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new LocationSnapshot(
            observation.LocationId,
            observation.LocationName,
            observation.LocationDescription,
            observation.Exits.Select(ProjectExit).ToArray(),
            observation.PresentActors.Select(ProjectActorPresence).ToArray()
        );
    }

    public static ActorSnapshot ProjectActor(ActorLocationObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new ActorSnapshot(
            observation.ActorId,
            observation.ActorName,
            ProjectLocation(observation.Location)
        );
    }

    public static LocationNavigationSnapshot ProjectNavigation(LocationNavigationObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new LocationNavigationSnapshot(
            observation.LocationId,
            observation.LocationName,
            observation.Edges.Select(ProjectNavigationEdge).ToArray()
        );
    }

    public static ActorNavigationSnapshot ProjectActorNavigation(ActorNavigationObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new ActorNavigationSnapshot(
            observation.ActorId,
            observation.ActorName,
            ProjectNavigation(observation.Navigation)
        );
    }

    private static ExitSnapshot ProjectExit(ExitObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new ExitSnapshot(
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

    private static ActorPresenceSnapshot ProjectActorPresence(ActorPresenceObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new ActorPresenceSnapshot(observation.ActorId, observation.ActorName);
    }

    private static NavigationEdgeSnapshot ProjectNavigationEdge(NavigationEdgeObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new NavigationEdgeSnapshot(
            observation.PassageId,
            observation.ExitName,
            observation.TargetLocationId,
            observation.TargetLocationName,
            observation.TravelMode.ToStorageValue(),
            observation.TravelCost
        );
    }
}
