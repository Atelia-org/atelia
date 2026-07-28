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
    SessionContextSelectionOptions? ContextSelection = null,
    ISessionMemoryLifecycleCoordinator? MemoryLifecycle = null
);

/// <summary>
/// Runtime-local policy for choosing a coherent derived context candidate. It is deliberately
/// smaller than <see cref="SessionContextSelectionRequest"/>: the engine supplies the exact
/// completion boundary, while the host supplies only its selection preference.
/// </summary>
public sealed record SessionContextSelectionOptions(
    string CoherenceGroup,
    SessionContextSelectionMode Mode = SessionContextSelectionMode.Latest,
    long? RawSuffixTokenBudget = null,
    long? TotalContextTokenBudget = null,
    int NthPreviousOrdinal = 0,
    int MaxCandidateCount = 32,
    long? BootstrapRawSuffixTokenBudget = null
) {
    public static SessionContextSelectionOptions Default { get; } = new("default");

    public void ValidateShape() {
        if (!Enum.IsDefined(Mode)) {
            throw new ArgumentOutOfRangeException(
                nameof(Mode),
                Mode,
                "Unsupported context selection mode."
            );
        }
        if (string.IsNullOrWhiteSpace(CoherenceGroup)) {
            throw new ArgumentException(
                "Coherence group cannot be empty.",
                nameof(CoherenceGroup)
            );
        }
        if (RawSuffixTokenBudget is <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(RawSuffixTokenBudget),
                "Raw suffix token budget must be positive when specified."
            );
        }
        if (TotalContextTokenBudget is <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(TotalContextTokenBudget),
                "Total context token budget must be positive when specified."
            );
        }
        if (BootstrapRawSuffixTokenBudget is <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(BootstrapRawSuffixTokenBudget),
                "Bootstrap raw suffix token budget must be positive when specified."
            );
        }
        if (NthPreviousOrdinal < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(NthPreviousOrdinal),
                "Nth-previous ordinal cannot be negative."
            );
        }
        if (Mode != SessionContextSelectionMode.NthPrevious
            && NthPreviousOrdinal != 0) {
            throw new ArgumentException(
                "A non-zero nth-previous ordinal requires NthPrevious mode.",
                nameof(NthPreviousOrdinal)
            );
        }
        if (Mode == SessionContextSelectionMode.Budgeted
            && RawSuffixTokenBudget is null
            && TotalContextTokenBudget is null) {
            throw new ArgumentException(
                "Budgeted selection requires a raw-suffix or total-context token budget."
            );
        }
        if (MaxCandidateCount is <= 0
            or > SessionContextSelectionRequest.MaximumCandidateCount) {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCandidateCount),
                $"Candidate count must be between 1 and {SessionContextSelectionRequest.MaximumCandidateCount}."
            );
        }
        if (Mode == SessionContextSelectionMode.NthPrevious
            && NthPreviousOrdinal >= MaxCandidateCount) {
            throw new ArgumentException(
                "Nth-previous ordinal must be smaller than the candidate discovery bound.",
                nameof(NthPreviousOrdinal)
            );
        }
    }

    public SessionContextSelectionRequest CreateRequest(EventAddress completionBoundary) {
        ValidateShape();
        var request = new SessionContextSelectionRequest(
            completionBoundary,
            Mode,
            CoherenceGroup,
            RawSuffixTokenBudget,
            TotalContextTokenBudget,
            NthPreviousOrdinal,
            MaxCandidateCount
        );
        request.ValidateShape();
        return request;
    }
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

internal sealed record SessionCreatedBody;

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
