using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public static class SessionJournalDefaults {
    public const string MainBranchName = "main";
    public const string Schema = "atelia.session-journal.trunk.v1";
}

public enum SessionEventKind : uint {
    RuntimeConfigSetup = 1,
    SystemPromptSetup = 2,
    SessionCreated = 3,
    ObservationAccepted = 4,
    AgentActionProduced = 5,
    ToolExecutionStarted = 6,
    ToolResultObserved = 7,
    CompletionRequestPrepared = 8,
    CompletionAttemptFailed = 9,
    ImportedAgentAction = 10,
}

public enum SessionExecutionPhase {
    Empty,
    Idle,
    AwaitingAgentAction,
    AwaitingCompletion,
    AwaitingToolExecution,
    TurnFailed,
}

public sealed record SessionCreateOptions(
    string ModelId,
    string SystemPrompt,
    string CompletionSurfaceId,
    string Schema = SessionJournalDefaults.Schema
) {
    public SessionRuntimeConfiguration ToRuntimeConfiguration()
        => new(ModelId, CompletionSurfaceId, Schema);
}

/// <summary>
/// Non-secret identity of the configured completion connection. Secrets and endpoint credentials
/// must never be copied into the raw SessionJournal.
/// </summary>
public sealed record SessionCompletionTargetIdentity(
    string ConnectionId,
    string Kind,
    string ConnectionFingerprint,
    string RequestAdapterFingerprint
);

public sealed record SessionRuntime(
    ICompletionClient CompletionClient,
    ToolSession? ToolSession = null,
    SessionCompletionTargetIdentity? CompletionTarget = null,
    int? MaxTokens = null,
    SessionTailProjectionOptions? TailProjection = null
);

public sealed record SessionTailProjectionOptions(string ArtifactId) {
    public string ArtifactId { get; init; } = string.IsNullOrWhiteSpace(ArtifactId)
        ? throw new ArgumentException("Tail projection artifact id cannot be empty.", nameof(ArtifactId))
        : ArtifactId;
}

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
    AfterRequestPreparedCommitted,
    AfterCompletionBeforeActionCommitted,
    AfterToolStartedCommitted,
    AfterToolResultCommitted
}

internal sealed record SessionJournalTestHooks(
    SessionJournalFailpoint Failpoint = SessionJournalFailpoint.None,
    Action<SessionEventKind>? BeforeCommit = null
);

internal sealed class SessionJournalFailpointException(SessionJournalFailpoint failpoint)
    : Exception($"SessionJournal failpoint reached: {failpoint}") {
    public SessionJournalFailpoint Failpoint { get; } = failpoint;
}

public sealed record SessionRuntimeConfiguration(
    string ModelId,
    string CompletionSurfaceId,
    string Schema
);

public sealed record SessionExecutionState(
    SessionExecutionPhase Phase,
    SessionEventKind? HeadKind,
    RawToolCall? PendingToolCall = null,
    string? PendingOperationId = null,
    bool PendingToolExecutionStarted = false,
    long ToolExecutionSequenceCheckpoint = 0,
    EventAddress? PendingRequestPreparedAddress = null,
    string? PendingCompletionAttemptId = null,
    string? ActiveCorrelationId = null
);

public sealed record SessionProjection(
    SessionRuntimeConfiguration? Config,
    string? SystemPrompt,
    IReadOnlyList<IHistoryMessage> Context,
    SessionExecutionState ExecutionState,
    EventAddress? Head
);

public sealed record AddressedSessionHistoryMessage(
    IHistoryMessage Message,
    EventAddress SourceStartInclusive,
    EventAddress SourceEndInclusive
);

public sealed record SessionHistoryReplay(
    EventAddress? SourceRawHead,
    IReadOnlyList<AddressedSessionHistoryMessage> Messages,
    SessionExecutionState ExecutionState
) {
    public static SessionHistoryReplay Empty { get; } = new(
        SourceRawHead: null,
        Messages: Array.AsReadOnly(Array.Empty<AddressedSessionHistoryMessage>()),
        ExecutionState: new SessionExecutionState(SessionExecutionPhase.Empty, HeadKind: null)
    );
}

public sealed record SessionGoverningSetup(
    EventAddress Head,
    EventAddress RuntimeConfigSetupAddress,
    SessionRuntimeConfiguration RuntimeConfig,
    EventAddress SystemPromptSetupAddress,
    string SystemPrompt
);

internal sealed record SessionCreatedBody;

internal sealed record SystemPromptSetupBody(string Content);

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

internal sealed record CompletionRequestPreparedBody(
    SessionRequestAttempt Attempt,
    SessionContextPlan Plan,
    SessionGoverningSetupReferences Setups,
    SessionRequestParameters Parameters,
    SessionRequestToolSet ToolSet,
    SessionRequestRendering Rendering,
    SessionRequestTarget Target,
    SessionRequestCommitment Commitment
);

internal sealed record CompletionAttemptFailedBody(
    string AttemptId,
    CompletionTerminationKind TerminationKind,
    string? ProviderReason,
    string? Detail,
    IReadOnlyList<string> Errors
);

internal readonly record struct DecodedSessionEvent(
    SessionEventKind Kind,
    int BodySchemaVersion,
    object Body,
    EventAddress Address,
    EventAddress? Parent
);

internal readonly record struct GoverningSetupResolutionDiagnostics(
    int HeaderVisitCount,
    int PayloadReadCount,
    int ManifestPayloadReadCount
);

internal readonly record struct SessionTailProjectionDiagnostics(
    int HeaderVisitCount,
    int SuffixPayloadReadCount,
    int SuffixEventCount
);
