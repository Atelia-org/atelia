using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

public sealed record ActorMoveResult(
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

internal static class RuntimeMovementProjector {
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
            observation.TravelMode,
            observation.TravelCost,
            observation.CurrentLocation
        );
    }
}
