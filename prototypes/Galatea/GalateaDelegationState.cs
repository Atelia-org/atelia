using System.Collections.ObjectModel;
using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;

namespace Atelia.Galatea.Server;

internal static class GalateaDelegationStateBounds {
    internal const int MaximumCapturedArtifacts =
        TextExtractorBounds.MaximumToolCallCount;
    internal const int MaximumCandidateCount =
        GalateaDelegationDurableContract.MaximumCandidateCount;
    internal const int MaximumCandidateUtf8Bytes =
        GalateaDelegationDurableContract.MaximumCandidateUtf8Bytes;
    internal const int MaximumReplyNoticeCount =
        PlayerTurnObservationEnvelope.MaximumNoticeCount;
    internal const int MaximumObservationUtf8Bytes =
        PlayerTurnObservationEnvelope.MaximumRenderedUtf8Bytes;
    internal const int MaximumIdentityUtf8Bytes = 1024;
    internal const int MaximumOperationIdUtf8Bytes = 512;
    internal const int MaximumFailureTokenUtf8Bytes = 128;
    internal const int MaximumTaskUtf8Bytes = 1024 * 1024;
}

internal enum GalateaDelegationRouteState {
    Unbound,
    Binding,
    Bound,
    Quarantined
}

internal enum GalateaDurableMailState {
    Unrouted,
    Queued,
    Started,
    OutcomeUnknown,
    Accepted,
    TerminalCompleted,
    TerminalFailed,
    Quarantined
}

/// <summary>
/// Sender-side durable state for one character-to-character delivery. This is
/// deliberately separate from the Codex delegation state machine.
/// </summary>
internal enum GalateaInternalMailState {
    Pending,
    ObservationBound,
    Delivered,
    Quarantined
}

internal enum GalateaReplyNoticeKind {
    Reply,
    DeliveryFailure
}

internal enum GalateaReplyNoticeState {
    Ready,
    Leased,
    Consumed
}

internal enum GalateaReplyLeaseState {
    CutoffFrozen,
    ObservationBound,
    ObservationCommitted,
    Quarantined
}

internal enum GalateaMailboxStatusState {
    NoMail,
    Queued,
    ActiveRunning,
    Backoff,
    AcceptedHistoryUnavailable,
    ReadyReply,
    Quarantined,
    Unavailable
}

/// <summary>
/// Closed, non-public evidence that permits a zero-interval Character to
/// attach and reconcile durable reply work. This is a scheduling hint only;
/// the reply lease cutoff remains the authoritative claim.
/// </summary>
internal enum GalateaAutomaticWakeReason {
    None,
    ReadyNotice,
    ActiveReplyLease
}

/// <summary>
/// Non-sensitive aggregate projection for mailbox observability. This type
/// deliberately cannot carry message content or durable identities.
/// </summary>
internal sealed record GalateaMailboxStatusProjection(
    GalateaMailboxStatusState State,
    int QueuedCount,
    int ReadyNoticeCount,
    int AttemptCount,
    string? Code,
    long? NextRetryAtUnixTimeMilliseconds
) {
    internal static GalateaMailboxStatusProjection NoMail { get; } = new(
        GalateaMailboxStatusState.NoMail,
        QueuedCount: 0,
        ReadyNoticeCount: 0,
        AttemptCount: 0,
        Code: null,
        NextRetryAtUnixTimeMilliseconds: null
    );

    internal static GalateaMailboxStatusProjection Unavailable(
        string code
    ) => new(
        GalateaMailboxStatusState.Unavailable,
        QueuedCount: 0,
        ReadyNoticeCount: 0,
        AttemptCount: 0,
        code,
        NextRetryAtUnixTimeMilliseconds: null
    );
}

internal sealed record GalateaMailboxStatusAggregate(
    GalateaDelegationRouteState RouteState,
    string? RouteQuarantineCode,
    int QueuedMailAttemptCount,
    string? QueuedMailLastCode,
    long? QueuedMailNextRetryAtUnixTimeMilliseconds,
    bool RouteHasActiveMail,
    GalateaDurableMailState? ActiveMailState,
    string? ActiveMailTerminalCode,
    int ActiveMailAttemptCount,
    string? ActiveMailLastCode,
    long? ActiveMailNextRetryAtUnixTimeMilliseconds,
    bool ActiveLeaseQuarantined,
    bool HasActiveReplyLease,
    int ActiveStateMailCount,
    int QueuedCount,
    int ReadyNoticeCount
);

internal sealed record GalateaDelegationStoreOwner(
    string CharacterId,
    string SessionRepositoryId
);

internal sealed record GalateaDelegationStoreBaseline(
    EventJournalPhysicalAppendFrontier CaptureFromPhysicalFrontier,
    string? SelectedHead
);

/// <summary>
/// Inbox UTF-8 capacity covers retained payload fields, independently of the
/// bounded provenance fields. This preserves existing active reply reservations.
/// </summary>
internal sealed record GalateaDelegationStoreLimits(
    int MaximumQueuedMails,
    int MaximumTaskUtf8Bytes,
    int MaximumReplyUtf8Bytes,
    int MaximumInboxReplies,
    int MaximumInboxUtf8Bytes
);

internal sealed record GalateaDelegationCaptureRequest(
    string SourceActionAddress,
    string VisibleActionSha256,
    int VisibleActionUtf8Bytes,
    string ExtractorContractId,
    IReadOnlyList<SendMailIntent> Intents,
    GalateaSenderSnapshot Sender,
    IReadOnlyList<GalateaInternalMailTarget?>? InternalTargets = null
);

/// <summary>Already-resolved, immutable target locator supplied by the host.</summary>
internal sealed record GalateaInternalMailTarget(
    string TargetCharacterId,
    string TargetSessionRepositoryId,
    string FromCharacterName
);

internal enum GalateaDelegationCaptureDisposition {
    Captured,
    AlreadyCaptured
}

internal sealed record GalateaDelegationCaptureResult(
    GalateaDelegationCaptureDisposition Disposition,
    long StoreRevision,
    IReadOnlyList<string> DispatchIds
);

internal sealed record GalateaActionCaptureSnapshot(
    string SourceActionAddress,
    long CaptureSequence,
    string VisibleActionSha256,
    int VisibleActionUtf8Bytes,
    string ExtractorContractId,
    int ArtifactCount,
    long Revision
);

internal sealed record GalateaOutboundMailSnapshot(
    string DispatchId,
    string SourceActionAddress,
    int ArtifactOrdinal,
    string Recipient,
    string? Subject,
    string? Body,
    string? InReplyToMessageId,
    string? EvidenceQuote,
    bool IsCodexRouted,
    GalateaDurableMailState State,
    string? OperationId,
    string? RequestedThreadId,
    string? AcceptedThreadId,
    string? AcceptedTurnId,
    string? TerminalFinalSha256,
    string? TerminalStage,
    string? TerminalCode,
    int RecoveryFailureCount,
    string? RecoveryLastCode,
    long? NextRetryAtUnixTimeMilliseconds,
    long Revision,
    string ContentFormat = "legacy-task",
    string? SenderName = null,
    string? TaskSha256 = null,
    int? TaskUtf8Bytes = null
);

internal sealed record GalateaInternalMailOutboxSnapshot(
    string DispatchId,
    string SourceActionAddress,
    long CaptureSequence,
    int ArtifactOrdinal,
    string TargetCharacterId,
    string TargetSessionRepositoryId,
    string FromCharacterName,
    string MessageId,
    GalateaInternalMailState State,
    string? ExpectedSessionHead,
    string? RenderedObservation,
    string? ObservationAddress,
    string? QuarantineCode,
    long Revision,
    Atelia.SessionJournal.SessionInputContent? BoundInput = null
) {
    internal Atelia.SessionJournal.SessionInputContent? ObservationContent => BoundInput
        ?? (RenderedObservation is null ? null : Atelia.SessionJournal.SessionInputContent.Text(RenderedObservation));
}

internal sealed record GalateaRouteBindingSnapshot(
    GalateaDelegationRouteState State,
    string? BindingOperationId,
    string? ThreadId,
    string? ActiveDispatchId,
    string? QuarantineCode,
    long Revision
);

internal sealed record GalateaReplyNoticeSnapshot(
    string NoticeId,
    string DispatchId,
    GalateaReplyNoticeKind Kind,
    string Body,
    string? Stage,
    string? Code,
    long CompletionSequence,
    GalateaReplyNoticeState State,
    string? ConsumedTurnEndAddress,
    long Revision,
    string NoticeFormat = "legacy-text",
    GalateaSenderSnapshot? Sender = null,
    string? Detail = null,
    string? ThreadId = null,
    string? TurnId = null
);

internal sealed record GalateaReplyLeaseMember(
    string NoticeId,
    long ExpectedRevision
);

internal sealed record GalateaReplyLeaseSnapshot(
    string LeaseId,
    GalateaReplyLeaseState State,
    string PlayerText,
    string? ExpectedSessionHead,
    string? RenderedObservation,
    int? ObservationUtf8Bytes,
    string? ObservationSha256,
    long CompletionFrontier,
    string? ObservationAddress,
    long Revision,
    IReadOnlyList<string> NoticeIds,
    Atelia.SessionJournal.SessionInputContent? BoundInput = null
) {
    internal Atelia.SessionJournal.SessionInputContent? ObservationContent => BoundInput
        ?? (RenderedObservation is null ? null : Atelia.SessionJournal.SessionInputContent.Text(RenderedObservation));
}

internal sealed record GalateaDelegationStateSnapshot(
    GalateaDelegationStoreOwner Owner,
    GalateaDelegationStoreBaseline Baseline,
    GalateaDelegationStoreLimits Limits,
    long StoreRevision,
    long NextCompletionSequence,
    GalateaRouteBindingSnapshot Route,
    IReadOnlyList<GalateaActionCaptureSnapshot> Captures,
    IReadOnlyList<GalateaOutboundMailSnapshot> Mails,
    IReadOnlyList<GalateaInternalMailOutboxSnapshot> InternalMailOutboxes,
    IReadOnlyList<GalateaReplyNoticeSnapshot> Notices,
    GalateaReplyLeaseSnapshot? ActiveLease
) {
    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());
}

internal sealed record GalateaDelegationStoreTestHooks(
    Action<string>? BeforeCommit = null,
    Action<string>? AfterCommitBeforeReturn = null
) {
    internal static GalateaDelegationStoreTestHooks None { get; } = new();
}

internal sealed class GalateaDelegationStoreConflictException
    : InvalidOperationException {
    internal GalateaDelegationStoreConflictException(string message)
        : base(message) { }
}

internal sealed class GalateaDelegationStoreReadOnlyException
    : InvalidOperationException {
    internal GalateaDelegationStoreReadOnlyException()
        : base("The delegation store was opened read-only.") { }
}

internal sealed class GalateaDelegationInboxBackpressureException
    : InvalidOperationException {
    internal GalateaDelegationInboxBackpressureException(
        long currentCount,
        long currentUtf8Bytes,
        int reservedCount,
        int reservedUtf8Bytes,
        GalateaDelegationStoreLimits limits
    ) : base("The delegation inbox has no capacity for one durable notice.") {
        CurrentCount = currentCount;
        CurrentUtf8Bytes = currentUtf8Bytes;
        ReservedCount = reservedCount;
        ReservedUtf8Bytes = reservedUtf8Bytes;
        Limits = limits;
    }

    internal long CurrentCount { get; }
    internal long CurrentUtf8Bytes { get; }
    internal int ReservedCount { get; }
    internal int ReservedUtf8Bytes { get; }
    internal GalateaDelegationStoreLimits Limits { get; }
}

internal sealed class GalateaDelegationCommitOutcomeException
    : IOException {
    internal GalateaDelegationCommitOutcomeException(
        string operation,
        string detail,
        Exception? innerException = null
    ) : base(
        $"Delegation store operation '{operation}' was not published: {detail}",
        innerException
    ) {
        Operation = operation;
    }

    internal string Operation { get; }
}
