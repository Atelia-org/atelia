using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

internal sealed record ActorMoveCommit(
    string ActorId,
    string ActorName,
    string PassageId,
    string ExitName,
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    TravelMode TravelMode,
    int TravelCost
) {
    public ActorMovementHistoryEntry ToHistoryEntry() => new(
        PassageId,
        ExitName,
        FromLocationId,
        FromLocationName,
        ToLocationId,
        ToLocationName,
        TravelMode,
        TravelCost
    );

    public ActorMoveResult ToResult(LocationObservation currentLocation) {
        ArgumentNullException.ThrowIfNull(currentLocation);

        return new ActorMoveResult(
            ActorId,
            ActorName,
            PassageId,
            ExitName,
            FromLocationId,
            FromLocationName,
            ToLocationId,
            ToLocationName,
            TravelMode,
            TravelCost,
            currentLocation
        );
    }
}

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
