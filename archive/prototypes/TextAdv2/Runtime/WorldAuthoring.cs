using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

public sealed record LocationAuthoringSnapshot(
    string LocationId,
    string LocationName,
    string LocationDescription,
    LocationMiningWorksiteAuthoringSnapshot? MiningWorksite
);

public sealed record LocationMiningWorksiteAuthoringSnapshot(
    int TicksPerYield,
    string YieldItemId,
    int YieldAmount
);

public sealed record ActorAuthoringSnapshot(
    string ActorId,
    string ActorName,
    string CurrentLocationId
);

public sealed record PassageAuthoringSnapshot(
    string PassageId,
    PassageEndpointAuthoringSnapshot EndpointA,
    PassageEndpointAuthoringSnapshot EndpointB,
    PassageDirectionAuthoringSnapshot FromAToB,
    PassageDirectionAuthoringSnapshot FromBToA,
    TravelMode TravelMode,
    int BaseTravelCost,
    string SharedConditionNote
);

public sealed record PassageEndpointAuthoringSnapshot(
    string LocationId,
    string ExitName,
    string LocalViewNote
);

public sealed record PassageDirectionAuthoringSnapshot(
    bool IsEnabled,
    int TravelCostModifier,
    string DirectionConditionNote
);

internal static class RuntimeWorldAuthoringProjector {
    public static LocationAuthoringSnapshot Project(Location location) {
        ArgumentNullException.ThrowIfNull(location);

        var miningWorksite = location.MiningWorksite;

        return new LocationAuthoringSnapshot(
            location.Id,
            location.Name,
            location.Description,
            miningWorksite is null
                ? null
                : new LocationMiningWorksiteAuthoringSnapshot(
                    miningWorksite.TicksPerYield,
                    miningWorksite.YieldItemId,
                    miningWorksite.YieldAmount
                )
        );
    }

    public static ActorAuthoringSnapshot Project(Actor actor) {
        ArgumentNullException.ThrowIfNull(actor);

        return new ActorAuthoringSnapshot(
            actor.Id,
            actor.Name,
            actor.CurrentLocationId
        );
    }

    public static PassageAuthoringSnapshot Project(Passage passage) {
        ArgumentNullException.ThrowIfNull(passage);

        return new PassageAuthoringSnapshot(
            passage.Id,
            Project(passage.EndpointA),
            Project(passage.EndpointB),
            Project(passage.FromAToB),
            Project(passage.FromBToA),
            passage.TravelMode,
            passage.BaseTravelCost,
            passage.SharedConditionNote
        );
    }

    private static PassageEndpointAuthoringSnapshot Project(PassageEndpoint endpoint) {
        ArgumentNullException.ThrowIfNull(endpoint);

        return new PassageEndpointAuthoringSnapshot(
            endpoint.LocationId,
            endpoint.ExitName,
            endpoint.LocalViewNote
        );
    }

    private static PassageDirectionAuthoringSnapshot Project(PassageDirectionRule direction) {
        ArgumentNullException.ThrowIfNull(direction);

        return new PassageDirectionAuthoringSnapshot(
            direction.IsEnabled,
            direction.TravelCostModifier,
            direction.DirectionConditionNote
        );
    }
}
