using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public enum SessionTurnEndReason { Stopped, Rejected, Incomplete }

public sealed record SessionTurnEndProjection(EventAddress Address, SessionTurnEndReason Reason);

public abstract record SessionClosedTurnOutcome {
    private SessionClosedTurnOutcome() { }
    public abstract EventAddress Address { get; }
    public sealed record Completed(SessionTerminalActionProjection Action) : SessionClosedTurnOutcome {
        public override EventAddress Address => Action.Address;
    }
    public sealed record Terminated(SessionTurnEndProjection End) : SessionClosedTurnOutcome {
        public override EventAddress Address => End.Address;
    }
}

public abstract record SessionTurnEndResult {
    private SessionTurnEndResult() { }
    public sealed record Ended(SessionTurnEndProjection End) : SessionTurnEndResult;
    public sealed record Unavailable(SessionExecutionBoundaryInspection Boundary) : SessionTurnEndResult;
    public sealed record Retryable(EventAddress ExpectedHead, EventAddress? ObservedHead) : SessionTurnEndResult;
}

/// <summary>A business closure, not a provider Action. Render only at a request/display boundary.</summary>
public sealed record SessionTurnEndedMessage(SessionTurnEndReason Reason) : IHistoryMessage {
    public HistoryMessageKind Kind => HistoryMessageKind.Observation;
    public string Render() => $"[Turn ended: {Reason}]";
}

internal sealed record TurnEndedBody(SessionTurnEndReason Reason);
