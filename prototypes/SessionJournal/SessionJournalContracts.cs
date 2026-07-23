using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public static class SessionJournalDefaults {
    public const string MainBranchName = "main";
    public const string Schema = "atelia.session-journal.trunk.v1";
}

public enum SessionEventKind : uint {
    SessionCreated = 1,
    ObservationAccepted = 3,
    AssistantActionProduced = 4,
}

public enum SessionExecutionPhase {
    Empty,
    Idle,
    AwaitingAssistantAction,
    AwaitingToolExecution,
}

public sealed record SessionCreateOptions(
    string ModelId,
    string SystemPrompt,
    string CompletionSurfaceId,
    string Schema = SessionJournalDefaults.Schema
);

public sealed record SessionConfiguration(
    string ModelId,
    string SystemPrompt,
    string CompletionSurfaceId,
    string Schema
);

public sealed record SessionExecutionState(
    SessionExecutionPhase Phase,
    SessionEventKind? HeadKind,
    RawToolCall? PendingToolCall = null
);

public sealed record SessionProjection(
    SessionConfiguration? Config,
    IReadOnlyList<IHistoryMessage> Context,
    SessionExecutionState ExecutionState,
    EventAddress? Head
);

internal sealed record SessionCreatedBody(
    string ModelId,
    string SystemPrompt,
    string CompletionSurfaceId,
    string Schema
);

internal sealed record ObservationAcceptedBody(string Content);

internal sealed record AssistantActionProducedBody(
    ActionMessage Action,
    CompletionDescriptor Invocation
);

internal readonly record struct DecodedSessionEvent(
    SessionEventKind Kind,
    int BodySchemaVersion,
    object Body,
    EventAddress Address
);
