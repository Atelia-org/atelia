using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Session;

public sealed record ActorRouteTrace(
    string ActorId,
    string ActorName,
    string StartLocationId,
    string StartLocationName,
    string EndLocationId,
    string EndLocationName,
    int StepCount,
    int TotalTravelCost,
    ActorRouteTraceStep[] Steps
);

public sealed record ActorRouteTraceStep(
    int StepNumber,
    string PassageId,
    string ExitName,
    string FromLocationId,
    string FromLocationName,
    string ToLocationId,
    string ToLocationName,
    string TravelMode,
    int TravelCost
);

internal static class SessionRouteTraceProjector {
    public static ActorRouteTrace Project(ActorRouteTraceObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new ActorRouteTrace(
            observation.ActorId,
            observation.ActorName,
            observation.StartLocationId,
            observation.StartLocationName,
            observation.EndLocationId,
            observation.EndLocationName,
            observation.StepCount,
            observation.TotalTravelCost,
            observation.Steps.Select(ProjectStep).ToArray()
        );
    }

    private static ActorRouteTraceStep ProjectStep(ActorRouteTraceStepObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new ActorRouteTraceStep(
            observation.StepNumber,
            observation.PassageId,
            observation.ExitName,
            observation.FromLocationId,
            observation.FromLocationName,
            observation.ToLocationId,
            observation.ToLocationName,
            observation.TravelMode.ToStorageValue(),
            observation.TravelCost
        );
    }
}
