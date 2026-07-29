using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Normalized, non-context facts emitted by the offline-only checked audit scan.
/// The event payload has already passed the strict SessionJournal codec before
/// one of these facts is exposed.
/// </summary>
public abstract record SessionJournalAuditFact {
    private protected SessionJournalAuditFact() {
    }
}

public sealed record SessionJournalAuditRuntimeConfigFact(
    SessionRuntimeConfiguration Configuration
) : SessionJournalAuditFact;

public sealed record SessionJournalAuditSystemPromptFact(
    string SystemPrompt
) : SessionJournalAuditFact;

public sealed record SessionJournalAuditSessionCreatedFact(
    SessionCreationOrigin Origin
) : SessionJournalAuditFact;

public sealed record SessionJournalAuditObservationFact(
    string SemanticContributionSha256
) : SessionJournalAuditFact;

public sealed record SessionJournalAuditPreparedFact(
    string CorrelationId,
    string Reason,
    long LastIssuedToolExecutionSequence,
    SessionToolRuntimeIdentity? ToolRuntimeIdentity
) : SessionJournalAuditFact;

public sealed record SessionJournalAuditActionFact(
    IReadOnlyList<RawToolCall> ToolCalls,
    string CorrelationId,
    long LastIssuedToolExecutionSequence,
    SessionToolRuntimeIdentity? ToolRuntimeIdentity,
    string SemanticContributionSha256
) : SessionJournalAuditFact;

public sealed record SessionJournalAuditToolExecutionStartedFact(
    string ToolCallId,
    string ToolName,
    string RawArgumentsJson,
    string OperationId,
    long ExecutionSequence,
    SessionToolRuntimeIdentity ToolRuntimeIdentity
) : SessionJournalAuditFact;

public sealed record SessionJournalAuditToolResultObservedFact(
    string ToolCallId,
    string ToolName,
    long ExecutionSequence,
    ToolExecutionStatus Status,
    string SemanticResultSha256
) : SessionJournalAuditFact;

public sealed record SessionJournalAuditCompletionAttemptStartedFact
    : SessionJournalAuditFact;

public sealed record SessionJournalAuditCompletionAttemptFailedFact(
    CompletionTerminationKind TerminationKind
) : SessionJournalAuditFact;

/// <summary>
/// One checked raw event plus only the normalized facts needed by a future
/// forward audit fold. This is deliberately not a replayable session-event
/// body or a projected history message.
/// </summary>
public sealed record SessionJournalAuditEvent(
    EventAddress Address,
    EventAddress? Parent,
    SessionEventKind Kind,
    int BodySchemaVersion,
    uint LogicalPayloadBytes,
    string PayloadSha256,
    SessionJournalAuditFact Fact
);

public sealed record SessionJournalAuditScanDiagnostics(
    int CapturedEventCount,
    long RepositoryEventReadCount,
    long IndexedHeaderLookupCount,
    long IndexedEventLookupCount,
    long DecodedPayloadBytes,
    int PreparedReconstructionCount
);

/// <summary>
/// Metadata for one successful, exact-ref checked audit scan. Events are
/// delivered to the caller's visitor and are not retained in this result.
/// </summary>
public sealed record SessionJournalAuditScanResult(
    string BranchName,
    RefId BranchRefId,
    EventAddress? CapturedHead,
    SessionExecutionState ExecutionStateAtCapturedHead,
    int EventCount,
    long LogicalPayloadBytes,
    SessionJournalAuditScanDiagnostics Diagnostics
);
