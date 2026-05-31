using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

public sealed record TextAdv2RuntimeActorRouteTraceObservation(
    string ActorId,
    string ActorName,
    string StartLocationId,
    string StartLocationName,
    string EndLocationId,
    string EndLocationName,
    int StepCount,
    int TotalTravelCost,
    TextAdv2RuntimeActorRouteTraceStepObservation[] Steps
);

public sealed record TextAdv2RuntimeActorRouteTraceStepObservation(
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

internal static class TextAdv2RuntimeActorRouteTraceProjector {
    public static TextAdv2RuntimeActorRouteTraceObservation Project(ActorRouteTraceObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeActorRouteTraceObservation(
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

    private static TextAdv2RuntimeActorRouteTraceStepObservation ProjectStep(ActorRouteTraceStepObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);

        return new TextAdv2RuntimeActorRouteTraceStepObservation(
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
