namespace Atelia.TextAdv;

internal enum TerminalActionMode {
    Immediate,
    Large
}

internal enum InteractionExecutionClass {
    ImmediateSelf,
    DeferredTurnEnd,
    WorkingStart,
    TurnEnding,
    UnsupportedZeroTurn
}

internal sealed record TerminalActionRequest(
    string ActionKind,
    string ActionSummary,
    string? ActionPayload,
    string PreActionReason
);

internal abstract record TerminalActionResolver {
    internal sealed record Explore(string Direction, string? Focus) : TerminalActionResolver;

    internal sealed record RestAWhile : TerminalActionResolver;

    internal sealed record Interaction(
        string InteractionId,
        InteractionExecutionClass ExecutionClass
    ) : TerminalActionResolver;
}

internal sealed record TerminalActionExecutionPlan(
    TerminalActionMode Mode,
    TerminalActionRequest Request,
    TerminalActionResolver Resolver
);
