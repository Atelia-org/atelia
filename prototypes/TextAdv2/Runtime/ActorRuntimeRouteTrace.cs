using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

public sealed record ActorRuntimeRouteTrace(
    string RuntimeEpochId,
    string ActorId,
    string ActorName,
    string StartLocationId,
    string StartLocationName,
    string EndLocationId,
    string EndLocationName,
    int StepCount,
    int TotalTravelCost,
    ActorRuntimeRouteTraceStep[] Steps
);

public sealed record ActorRuntimeRouteTraceStep(
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

internal static class ActorRuntimeRouteTraceProjector {
    public static ActorRuntimeRouteTrace Project(ActorRuntimeRouteTraceObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new ActorRuntimeRouteTrace(
            observation.RuntimeEpochId.ToString(),
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

    private static ActorRuntimeRouteTraceStep ProjectStep(ActorRuntimeRouteTraceStepObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new ActorRuntimeRouteTraceStep(
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
