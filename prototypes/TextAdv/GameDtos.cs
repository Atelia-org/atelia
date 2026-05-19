using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal sealed record LocationPerception(
    string LocationId,
    string Name,
    string Description,
    IReadOnlyList<LocationExitPerception> Exits,
    IReadOnlyList<ItemPerception> Items,
    IReadOnlyList<ActorPerception> Actors,
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

internal sealed record ActorPerception(
    string ActorId,
    string Kind,
    string Name,
    string ProfileNote,
    IReadOnlyList<InteractionPerception> Interactions
);

internal sealed record InteractionPerception(
    string InteractionId,
    string TargetKind,
    string TargetId,
    string ActionKind,
    string VisibleLabel,
    string? PreconditionNote,
    string? EffectNote,
    int TurnCost,
    string EffectScope,
    IReadOnlyList<string> EffectSlots
);

internal sealed record TurnStep(
    int StepNumber,
    string ActionKind,
    string ActionSummary,
    string? ActionPayload,
    string PreActionReason,
    string ValidatorFeedback,
    bool EndsTurn,
    string? StepOutcomeSummary,
    string StepOutcomeState
);

internal sealed record TurnActorStatus(
    string ActorId,
    string Kind,
    string Name,
    bool Active,
    bool HasSubmittedLargeAction,
    string? LargeActionKind,
    string? LargeActionSummary
);

internal sealed record TurnCollectionStatus(
    int Day,
    int Slot,
    int SlotsPerDay,
    string TurnOwnerActorId,
    string BarrierState,
    bool AllActiveActorsSubmittedLargeAction,
    IReadOnlyList<TurnActorStatus> Actors
);

internal sealed record PerceptionBundle(
    string ActorId,
    string ActorKind,
    string ActorName,
    string ActorProfileNote,
    int Day,
    int Slot,
    int SlotsPerDay,
    LocationPerception Location,
    IReadOnlyList<ItemPerception> InventoryItems,
    TextBlockSnapshotDocument NotebookBlocks,
    IReadOnlyList<TurnStep> AcceptedSteps,
    string? LastResolution
);

internal sealed record ActionResolution(
    string Summary,
    PerceptionBundle NextPerception
);

internal sealed record ActorJournalExport(
    string ActorId,
    string ActorName,
    string ActorKind,
    string FileName,
    string Content
);

internal sealed record AutonomousRoundReport(
    int RoundNumber,
    string TerminalActionKind,
    string TerminalActionSummary,
    string? TerminalActionPayload,
    string ResolutionSummary,
    TurnCollectionStatus EndingStatus
);
