using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public static class SessionJournalDefaults {
    public const string MainBranchName = "main";
    public const string Schema = "atelia.session-journal.trunk.v1";
}

public enum SessionEventKind : uint {
    SessionCreated = 1,
    ObservationAccepted = 3,
    AgentActionProduced = 4,
    ToolExecutionStarted = 5,
    ToolResultObserved = 6,
}

public enum SessionExecutionPhase {
    Empty,
    Idle,
    AwaitingAgentAction,
    AwaitingToolExecution,
}

public sealed record SessionCreateOptions(
    string ModelId,
    string SystemPrompt,
    string CompletionSurfaceId,
    string Schema = SessionJournalDefaults.Schema
) {
    public SessionConfiguration ToConfiguration()
        => new(ModelId, SystemPrompt, CompletionSurfaceId, Schema);
}

public sealed record SessionRuntime(ICompletionClient CompletionClient, ToolSession? ToolSession = null);

public sealed record TurnResult(
    ActionMessage Message,
    CompletionDescriptor Invocation,
    IReadOnlyList<string>? Errors
);

public sealed record ResumeOutcome(
    bool Advanced,
    ActionMessage? Message = null,
    CompletionDescriptor? Invocation = null,
    IReadOnlyList<string>? Errors = null
);

public sealed class SessionJournalTurnAbortedException : InvalidOperationException {
    public SessionJournalTurnAbortedException(
        string message,
        CompletionTermination termination,
        IReadOnlyList<string>? errors
    ) : base(message) {
        Termination = termination;
        Errors = errors;
    }

    public CompletionTermination Termination { get; }

    public IReadOnlyList<string>? Errors { get; }
}

internal enum SessionJournalFailpoint {
    None,
    AfterObservationCommitted,
    AfterCompletionBeforeActionCommitted,
    AfterToolStartedCommitted,
    AfterToolResultCommitted
}

internal sealed record SessionJournalTestHooks(
    SessionJournalFailpoint Failpoint = SessionJournalFailpoint.None
);

internal sealed class SessionJournalFailpointException(SessionJournalFailpoint failpoint)
    : Exception($"SessionJournal failpoint reached: {failpoint}") {
    public SessionJournalFailpoint Failpoint { get; } = failpoint;
}

public sealed record SessionConfiguration(
    string ModelId,
    string SystemPrompt,
    string CompletionSurfaceId,
    string Schema
);

public sealed record SessionExecutionState(
    SessionExecutionPhase Phase,
    SessionEventKind? HeadKind,
    RawToolCall? PendingToolCall = null,
    string? PendingOperationId = null,
    bool PendingToolExecutionStarted = false,
    long ToolExecutionSequenceCheckpoint = 0
);

public sealed record SessionProjection(
    SessionConfiguration? Config,
    IReadOnlyList<IHistoryMessage> Context,
    SessionExecutionState ExecutionState,
    EventAddress? Head
);

internal sealed record ObservationAcceptedBody(string Content);

internal sealed record AgentActionProducedBody(
    ActionMessage Action,
    CompletionDescriptor Invocation
);

internal sealed record ToolExecutionStartedBody(
    string ToolCallId,
    string ToolName,
    string RawArgumentsJson,
    string OperationId
);

internal sealed record ToolResultObservedBody(
    string ToolCallId,
    string ToolName,
    ToolExecutionStatus Status,
    IReadOnlyList<ToolResultBlock> Blocks
);

internal readonly record struct DecodedSessionEvent(
    SessionEventKind Kind,
    int BodySchemaVersion,
    object Body,
    EventAddress Address
);
