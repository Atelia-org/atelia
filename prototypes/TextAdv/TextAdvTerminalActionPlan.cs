using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal enum TerminalActionMode {
    Immediate,
    Large
}

internal enum InteractionExecutionKind {
    ImmediateSelf,
    DeferredTurnEnd,
    WorkingStart,
    TurnEnding
}

internal abstract record TerminalActionExecutionPlan(string PreActionReason) {
    internal abstract TerminalActionMode Mode { get; }

    internal abstract string ActionKind { get; }

    internal abstract string ActionSummary { get; }

    internal abstract string? ActionPayload { get; }

    internal sealed record Explore(
        string Direction,
        string? Focus,
        string PreActionReason
    ) : TerminalActionExecutionPlan(PreActionReason) {
        internal override TerminalActionMode Mode => TerminalActionMode.Large;

        internal override string ActionKind => TerminalActionKinds.LargeExplore;

        internal override string ActionSummary => Focus is null
            ? $"向 {Direction} 探索"
            : $"向 {Direction} 探索：{Focus}";

        internal override string? ActionPayload => GameSimulation.BuildExplorePayload(Direction, Focus);
    }

    internal sealed record RestAWhile(string PreActionReason) : TerminalActionExecutionPlan(PreActionReason) {
        internal override TerminalActionMode Mode => TerminalActionMode.Large;

        internal override string ActionKind => TerminalActionKinds.LargeRestAWhile;

        internal override string ActionSummary => "原地休息一会";

        internal override string? ActionPayload => null;
    }

    internal sealed record Interaction(
        string InteractionId,
        string VisibleLabel,
        string InteractionActionKind,
        string InteractionPayload,
        InteractionExecutionKind ExecutionKind,
        string PreActionReason
    ) : TerminalActionExecutionPlan(PreActionReason) {
        internal override TerminalActionMode Mode => ExecutionKind switch {
            InteractionExecutionKind.ImmediateSelf or InteractionExecutionKind.DeferredTurnEnd => TerminalActionMode.Immediate,
            InteractionExecutionKind.WorkingStart or InteractionExecutionKind.TurnEnding => TerminalActionMode.Large,
            _ => throw new InvalidOperationException($"Unknown interaction execution kind: {ExecutionKind}")
        };

        internal override string ActionKind => ExecutionKind switch {
            InteractionExecutionKind.ImmediateSelf or InteractionExecutionKind.DeferredTurnEnd => TerminalActionKinds.SmallInteract,
            InteractionExecutionKind.WorkingStart or InteractionExecutionKind.TurnEnding => TerminalActionKinds.LargeInteract,
            _ => throw new InvalidOperationException($"Unknown interaction execution kind: {ExecutionKind}")
        };

        internal override string ActionSummary => $"{VisibleLabel} ({InteractionActionKind})";

        internal override string? ActionPayload => InteractionPayload;
    }
}
