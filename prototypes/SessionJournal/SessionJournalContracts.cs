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
    // 11 is retired. It was the opaque CompletionAttemptRestarted event.
    CompletionAttemptStarted = 13,
}

public enum SessionExecutionPhase {
    Empty,
    Idle,
    AwaitingAgentAction,
    AwaitingCompletionDispatch,
    AwaitingCompletion,
    AwaitingToolExecution,
    TurnFailed,
}

/// <summary>
/// Minimal store-neutral online boundary inspection. It exposes no projection, context, derived
/// identity, or pending request body.
/// </summary>
public sealed record SessionExecutionBoundaryInspection(
    EventAddress? Head,
    SessionExecutionPhase Phase,
    SessionEventKind? HeadKind
);

public sealed record SessionCreateOptions(
    string ModelId,
    string SystemPrompt,
    string CompletionSurfaceId,
    string Schema = SessionJournalDefaults.Schema,
    int DerivedContextNthPrevious = 0,
    SessionCreationOrigin Origin = SessionCreationOrigin.Native
) {
    public SessionRuntimeConfiguration ToRuntimeConfiguration()
        => new(
            ModelId,
            CompletionSurfaceId,
            Schema,
            new SessionDerivedContextConfiguration(
                DerivedContextNthPrevious
            )
        );
}

public enum SessionCreationOrigin {
    Native,
    LegacyImport,
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

/// <summary>
/// Non-secret identity of the concrete tool host selected for durable dispatch.
/// Definitions describe what the model sees; this identity additionally pins which
/// implementation set and side-effect capability policy will execute those calls.
/// </summary>
public sealed record SessionToolRuntimeIdentity(
    string HostId,
    string ImplementationSetFingerprint,
    string CapabilitySetFingerprint
);

public enum SessionUncertainCompletionRecoveryPolicy {
    Refuse,
    RestartWithNewAttempt,
}

public sealed record SessionRuntime(
    ICompletionClient CompletionClient,
    ToolSession? ToolSession = null,
    SessionCompletionTargetIdentity? CompletionTarget = null,
    int? MaxTokens = null,
    SessionUncertainCompletionRecoveryPolicy UncertainCompletionRecoveryPolicy =
        SessionUncertainCompletionRecoveryPolicy.Refuse,
    SessionToolRuntimeIdentity? ToolRuntimeIdentity = null,
    ICoherentContextCandidateSource? ContextCandidateSource = null,
    long? MaximumCanonicalRequestBytes = null,
    ISessionMemoryLifecycleCoordinator? MemoryLifecycle = null
);

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

/// <summary>
/// Stable reason why an online completion route cannot currently be entered.
/// Raw journal corruption and lineage violations are deliberately reported by
/// their existing exceptions instead of being mapped to this readiness surface.
/// </summary>
public enum SessionJournalNotReadyReason {
    ContextCandidateSourceRequired,
    ContextCandidateUnavailable,
    MemoryMaintenanceBackpressure,
    MemoryMaintenanceUnavailable,
}

/// <summary>
/// Indicates that the durable raw lineage is valid, but an online completion
/// prerequisite is absent or its rebuildable derived material is unusable.
/// </summary>
public sealed class SessionJournalNotReadyException : InvalidOperationException {
    public SessionJournalNotReadyException(
        SessionJournalNotReadyReason reason,
        string message
    ) : base(message) {
        Reason = reason;
    }

    public SessionJournalNotReadyReason Reason { get; }
}

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
    AfterCompletionAttemptStartedCommitted,
    AfterCompletionBeforeActionCommitted,
    AfterActionCommitted,
    AfterToolStartedCommitted,
    AfterToolExecutionBeforeResultCommitted,
    AfterToolResultCommitted
}

internal sealed record SessionJournalTestHooks(
    SessionJournalFailpoint Failpoint = SessionJournalFailpoint.None,
    Action<SessionEventKind>? BeforeCommit = null,
    Action<EventJournal.EventJournal>?
        AfterAuditSnapshotValidated = null
);

internal sealed class SessionJournalFailpointException(SessionJournalFailpoint failpoint)
    : Exception($"SessionJournal failpoint reached: {failpoint}") {
    public SessionJournalFailpoint Failpoint { get; } = failpoint;
}

public sealed record SessionRuntimeConfiguration(
    string ModelId,
    string CompletionSurfaceId,
    string Schema,
    SessionDerivedContextConfiguration DerivedContext
);

public sealed record SessionDerivedContextConfiguration(
    int NthPrevious
);

public sealed record SessionExecutionState(
    SessionExecutionPhase Phase,
    SessionEventKind? HeadKind,
    RawToolCall? PendingToolCall = null,
    string? PendingOperationId = null,
    bool PendingToolExecutionStarted = false,
    long ToolExecutionSequenceCheckpoint = 0,
    EventAddress? PendingRequestPreparedAddress = null,
    string? ActiveCorrelationId = null,
    EventAddress? ActiveCompletionAttemptAddress = null,
    SessionToolRuntimeIdentity? PendingToolRuntimeIdentity = null
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

internal sealed record SessionCreatedBody(
    SessionCreationOrigin Origin
);

internal sealed record SystemPromptSetupBody(string Content);

internal sealed record ObservationAcceptedBody(string Content);

internal sealed record AgentActionProducedBody(
    ActionMessage Action,
    CompletionDescriptor Invocation,
    string CorrelationId,
    SessionExecutionCheckpoint Execution,
    SessionToolRuntimeIdentity? ToolRuntimeIdentity
);

internal sealed record ToolExecutionStartedBody(
    string ToolCallId,
    string ToolName,
    string RawArgumentsJson,
    string OperationId,
    long ExecutionSequence,
    SessionToolRuntimeIdentity ToolRuntimeIdentity
);

internal sealed record ToolResultObservedBody(
    string ToolCallId,
    string ToolName,
    long ExecutionSequence,
    ToolExecutionStatus Status,
    IReadOnlyList<ToolResultBlock> Blocks
);

internal sealed record CompletionRequestPreparedBody(
    SessionRequestOrigin Origin,
    SessionExecutionCheckpoint Execution,
    SessionContextPlan Plan,
    SessionGoverningSetupReferences Setups,
    SessionRequestParameters Parameters,
    SessionRequestToolSet ToolSet,
    SessionRequestRecipe Recipe,
    SessionRequestTarget Target,
    SessionRequestCommitment Commitment
);

internal sealed record CompletionAttemptFailedBody(
    CompletionTerminationKind TerminationKind,
    string? ProviderReason,
    string? Detail,
    IReadOnlyList<string> Errors
);

internal sealed record CompletionAttemptStartedBody;

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

internal sealed record SessionExecutionRecovery(
    EventAddress? Head,
    SessionExecutionState State,
    SessionExecutionRecoveryBoundary Boundary,
    SessionExecutionRecoveryDiagnostics Diagnostics
);

internal sealed record SessionExecutionRecoveryBoundary(
    EventAddress? SourcePrepared,
    EventAddress? SourceAction,
    EventAddress? SourceObservation,
    EventAddress? LatestExecutionCheckpoint
);

internal readonly record struct SessionExecutionRecoveryDiagnostics(
    int HeaderReadCount,
    int PayloadReadCount
);

internal readonly record struct SessionJournalReadDiagnostics(
    long HeaderPreviewReadCount,
    long PayloadReadCount,
    long LogicalPayloadByteCount,
    long ChronologicalChainReadCount,
    long ChronologicalEventCount,
    long FullProjectionInvocationCount
) {
    public static SessionJournalReadDiagnostics operator -(
        SessionJournalReadDiagnostics end,
        SessionJournalReadDiagnostics start
    ) => new(
        HeaderPreviewReadCount: checked(end.HeaderPreviewReadCount - start.HeaderPreviewReadCount),
        PayloadReadCount: checked(end.PayloadReadCount - start.PayloadReadCount),
        LogicalPayloadByteCount: checked(
            end.LogicalPayloadByteCount - start.LogicalPayloadByteCount
        ),
        ChronologicalChainReadCount: checked(
            end.ChronologicalChainReadCount - start.ChronologicalChainReadCount
        ),
        ChronologicalEventCount: checked(
            end.ChronologicalEventCount - start.ChronologicalEventCount
        ),
        FullProjectionInvocationCount: checked(
            end.FullProjectionInvocationCount - start.FullProjectionInvocationCount
        )
    );
}

internal readonly record struct SessionJournalPayloadLifetimeDiagnostics(
    long CurrentLiveLogicalPayloadBytes,
    long PeakLiveLogicalPayloadBytes
);
