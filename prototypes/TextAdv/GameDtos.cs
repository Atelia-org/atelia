using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal sealed record LocationPerception(
    string LocationId,
    string Name,
    string Description,
    IReadOnlyList<LocationExitPerception> Exits
);

internal sealed record LocationExitPerception(
    string Direction,
    string TargetLocationId,
    string TargetName
);

internal sealed record TurnStep(
    int StepNumber,
    string ActionKind,
    string ActionSummary,
    string? ActionPayload,
    string PreActionReason,
    string ValidatorFeedback,
    bool EndsTurn
);

internal sealed record PerceptionBundle(
    int Day,
    int Slot,
    int SlotsPerDay,
    LocationPerception Location,
    TextBlockSnapshotDocument NotebookBlocks,
    IReadOnlyList<TurnStep> AcceptedSteps,
    string? LastResolution
);

internal sealed record TurnResolution(
    string Summary,
    PerceptionBundle NextPerception
);

internal sealed record TurnResolutionApplyResult(
    TurnResolution? Resolution,
    AteliaError? Error
) {
    internal bool IsSuccess => Error is null && Resolution is not null;

    internal static TurnResolutionApplyResult Success(TurnResolution resolution)
        => new(resolution, null);

    internal static TurnResolutionApplyResult Failure(AteliaError error)
        => new(null, error);
}
