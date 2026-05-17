using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal sealed record LocationPerception(
    string LocationId,
    string Name,
    string Description,
    IReadOnlyList<LocationExitPerception> Exits,
    IReadOnlyList<ItemPerception> Items,
    IReadOnlyList<InteractionPerception> Interactions
);

internal sealed record LocationExitPerception(
    string Direction,
    string TargetLocationId,
    string TargetName
);

internal sealed record ItemPerception(
    string ItemId,
    string Name,
    string Description,
    IReadOnlyList<InteractionPerception> Interactions
);

internal sealed record InteractionPerception(
    string InteractionId,
    string TargetKind,
    string TargetId,
    string ActionKind,
    string VisibleLabel,
    string? EffectNote
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
