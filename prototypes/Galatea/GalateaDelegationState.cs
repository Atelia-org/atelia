using System.Collections.ObjectModel;
using Atelia.EventJournal;

namespace Atelia.Galatea.Server;

internal static class GalateaDelegationStateBounds {
    internal const int MaximumCapturedArtifacts =
        TextExtractorBounds.MaximumToolCallCount;
    internal const int MaximumCandidateCount =
        GalateaDelegationDurableContract.MaximumCandidateCount;
    internal const int MaximumCandidateUtf8Bytes =
        GalateaDelegationDurableContract.MaximumCandidateUtf8Bytes;
    internal const int MaximumReplyNoticeCount =
        GalateaPlayerObservationEnvelope.MaximumNoticeCount;
    internal const int MaximumObservationUtf8Bytes =
        GalateaPlayerObservationEnvelope.MaximumRenderedUtf8Bytes;
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

internal sealed record GalateaDelegationStoreOwner(
    string UserId,
    string SessionRepositoryId,
    string RoutePolicyFingerprint
);

internal sealed record GalateaDelegationStoreBaseline(
    EventJournalPhysicalAppendFrontier CaptureFromPhysicalFrontier,
    string? SelectedHead
);

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
    IReadOnlyList<SendMailIntent> Intents
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
    string? FrozenRoutePolicyFingerprint,
    GalateaDurableMailState State,
    string? OperationId,
    string? RequestedThreadId,
    string? AcceptedThreadId,
    string? AcceptedTurnId,
    string? TerminalFinalSha256,
    string? TerminalStage,
    string? TerminalCode,
    int ReconcileAttemptCount,
    string? ReconcileLastCode,
    long? NextReconcileAtUnixTimeMilliseconds,
    long Revision
);

internal sealed record GalateaRouteBindingSnapshot(
    GalateaDelegationRouteState State,
    string? BindingOperationId,
    string? ThreadId,
    string RoutePolicyFingerprint,
    string? ActiveDispatchId,
    string? QuarantineCode,
    int EnsureAttemptCount,
    string? EnsureLastCode,
    long? NextEnsureAtUnixTimeMilliseconds,
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
    string? ConsumedActionAddress,
    long Revision
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
    IReadOnlyList<string> NoticeIds
);

internal sealed record GalateaDelegationStateSnapshot(
    GalateaDelegationStoreOwner Owner,
    GalateaDelegationStoreBaseline Baseline,
    GalateaDelegationStoreLimits Limits,
    long StoreRevision,
    long NextCompletionSequence,
    GalateaRouteBindingSnapshot Route,
    IReadOnlyList<GalateaActionCaptureSnapshot> Captures,
    IReadOnlyList<GalateaOutboundMailSnapshot> Mails,
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
