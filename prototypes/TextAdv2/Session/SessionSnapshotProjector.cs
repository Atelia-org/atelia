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

internal static class SessionMovementProjector {
    public static ActorMoveResult Project(ActorMovementObservation observation) {
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

    private static LocationSnapshot ProjectLocation(LocationObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new LocationSnapshot(
            observation.LocationId,
            observation.LocationName,
            observation.LocationDescription,
            observation.Exits.Select(ProjectExit).ToArray(),
            observation.PresentActors.Select(ProjectActorPresence).ToArray()
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
}
