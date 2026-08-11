using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Immutable identity of one explicit, offline selected-lineage audit.
/// It fixes the exact Ref head and the governing setup provenance observed at
/// that head. It contains no event body or projected history content.
/// </summary>
public sealed record SessionSelectedLineageAuditCapture(
    string BranchName,
    RefId BranchRefId,
    EventAddress CapturedHead
);

/// <summary>
/// Minimal checked raw-event identity retained by a rebuild execution aid.
/// Event bodies, prompts, projected messages, and policy decisions are
/// deliberately excluded.
/// </summary>
public sealed record SessionSelectedLineageAuditEntry(
    EventAddress Address,
    EventAddress? Parent,
    ulong SequenceNumber,
    SessionEventKind Kind,
    int BodySchemaVersion,
    uint LogicalPayloadBytes,
    string PayloadSha256
);

/// <summary>
/// One exact head-to-oldest audit page. A non-null continuation is the exact
/// Parent that must begin the next page.
/// </summary>
public sealed record SessionSelectedLineageAuditPage(
    long Ordinal,
    EventAddress PageHead,
    IReadOnlyList<SessionSelectedLineageAuditEntry> HeadToOldest,
    EventAddress? Continuation
);

public static class SessionSelectedLineageAuditLimits {
    public const int MaximumPageEventCount = 1024;
    public const int MaximumForwardRangeEventCount = 65_536;
}

public enum SessionSelectedLineageAuditChangeKind {
    RawHeadChanged = 1,
    SourceChanged = 2
}

/// <summary>
/// Typed stale-source failure for an explicit selected-lineage audit. Raw
/// corruption and invalid SessionJournal semantics remain fail-fast data
/// errors and are never relabeled as a rebuild request.
/// </summary>
public sealed class SessionSelectedLineageAuditChangedException
    : InvalidOperationException {
    internal SessionSelectedLineageAuditChangedException(
        SessionSelectedLineageAuditChangeKind kind,
        EventAddress expectedHead,
        EventAddress? observedHead,
        string detail
    ) : base(detail) {
        Kind = kind;
        ExpectedHead = expectedHead;
        ObservedHead = observedHead;
    }

    public SessionSelectedLineageAuditChangeKind Kind { get; }
    public EventAddress ExpectedHead { get; }
    public EventAddress? ObservedHead { get; }
}

/// <summary>
/// Bounded-memory, engine-lifetime-bound capture of one exact selected Parent
/// lineage. Pages are checked independently and are not retained by this
/// object after the caller persists or consumes them.
/// </summary>
public sealed class SessionSelectedLineageAuditSession {
    private readonly SessionJournalEngine _owner;
    private EventAddress? _nextAddress;
    private ulong? _childSequenceExclusive;
    private long _nextOrdinal;
    private long _eventCount;
    private long _logicalPayloadBytes;
    private int _maximumResidentEntryCount;
    private EventAddress? _rootAddress;
    private EventAddress? _sessionCreatedAddress;
    private SessionContextAnchorSetupReferences? _sessionCreatedSetups;
    private SessionContextSetupReference? _headRuntimeSetup;
    private SessionContextSetupReference? _headSystemPromptSetup;
    private bool _authorityIssued;
    private readonly bool _ownerBoundLifecycleAudit;

    internal SessionSelectedLineageAuditSession(
        SessionJournalEngine owner,
        SessionSelectedLineageAuditCapture capture,
        bool ownerBoundLifecycleAudit = false
    ) {
        _owner = owner;
        Capture = capture;
        _nextAddress = capture.CapturedHead;
        _ownerBoundLifecycleAudit = ownerBoundLifecycleAudit;
    }

    public SessionSelectedLineageAuditCapture Capture { get; }
    public bool IsCaptureComplete => _nextAddress is null;
    public long CommittedPageCount => _nextOrdinal;
    public long EventCount => _eventCount;
    public long LogicalPayloadBytes => _logicalPayloadBytes;

    /// <summary>
    /// Largest page retained by this session. It is exposed so bounded-memory
    /// behavior can be regression-tested without wall-clock or GC heuristics.
    /// </summary>
    public int MaximumResidentEntryCount => _maximumResidentEntryCount;

    public SessionSelectedLineageAuditPage ReadNextPage(
        int maxEventCount,
        CancellationToken cancellationToken = default
    ) => _owner.ReadSelectedLineageAuditPage(
        this,
        maxEventCount,
        cancellationToken
    );

    public SessionSelectedLineageAuditAuthority Complete(
        CancellationToken cancellationToken = default
    ) => _owner.CompleteSelectedLineageAudit(
        this,
        cancellationToken
    );

    internal EventAddress? NextAddress => _nextAddress;
    internal SessionJournalEngine Owner => _owner;
    internal ulong? ChildSequenceExclusive => _childSequenceExclusive;
    internal bool AuthorityIssued => _authorityIssued;
    internal bool OwnerBoundLifecycleAudit => _ownerBoundLifecycleAudit;
    internal EventAddress? RootAddress => _rootAddress;
    internal EventAddress? SessionCreatedAddress => _sessionCreatedAddress;
    internal SessionContextAnchorSetupReferences? SessionCreatedSetups =>
        _sessionCreatedSetups;
    internal SessionContextAnchorSetupReferences? HeadSetups =>
        _headRuntimeSetup is { } runtime
        && _headSystemPromptSetup is { } prompt
            ? new SessionContextAnchorSetupReferences(runtime, prompt)
            : null;

    internal void AcceptPage(
        SessionSelectedLineageAuditPage page,
        ulong oldestSequence,
        EventAddress? sessionCreatedAddress,
        SessionContextAnchorSetupReferences? sessionCreatedSetups,
        SessionContextSetupReference? pageRuntimeSetup,
        SessionContextSetupReference? pageSystemPromptSetup
    ) {
        checked {
            _eventCount += page.HeadToOldest.Count;
            foreach (SessionSelectedLineageAuditEntry entry
                     in page.HeadToOldest) {
                _logicalPayloadBytes += entry.LogicalPayloadBytes;
            }
        }
        _maximumResidentEntryCount = Math.Max(
            _maximumResidentEntryCount,
            page.HeadToOldest.Count
        );
        _nextAddress = page.Continuation;
        _childSequenceExclusive = page.Continuation is null
            ? null
            : oldestSequence;
        _nextOrdinal = checked(_nextOrdinal + 1);
        if (page.Continuation is null) {
            _rootAddress = page.HeadToOldest[^1].Address;
        }
        if (sessionCreatedAddress is { } created) {
            if (_sessionCreatedAddress is not null) {
                throw new InvalidDataException(
                    "Selected SessionJournal lineage contains multiple "
                    + "SessionCreated events."
                );
            }
            _sessionCreatedAddress = created;
            _sessionCreatedSetups = sessionCreatedSetups
                ?? throw new InvalidDataException(
                    "SessionCreated audit entry has no setup provenance."
                );
        }
        _headRuntimeSetup ??= pageRuntimeSetup;
        _headSystemPromptSetup ??= pageSystemPromptSetup;
    }

    internal void MarkAuthorityIssued() => _authorityIssued = true;
}

/// <summary>
/// In-memory authority issued only after a complete exact-head audit. Durable
/// spool files are evidence used to reproduce this object; they are never an
/// authority by themselves.
/// </summary>
public sealed class SessionSelectedLineageAuditAuthority {
    private readonly SessionJournalEngine _owner;

    internal SessionSelectedLineageAuditAuthority(
        SessionJournalEngine owner,
        SessionSelectedLineageAuditCapture capture,
        EventAddress rootAddress,
        SessionHistoryPlanningSeed bootstrapSeed,
        SessionContextAnchorSetupReferences headSetups,
        SessionExecutionState executionStateAtCapturedHead,
        long eventCount,
        long logicalPayloadBytes,
        int maximumResidentEntryCount
    ) {
        _owner = owner;
        Capture = capture;
        RootAddress = rootAddress;
        BootstrapSeed = bootstrapSeed;
        HeadSetups = headSetups;
        ExecutionStateAtCapturedHead = executionStateAtCapturedHead;
        EventCount = eventCount;
        LogicalPayloadBytes = logicalPayloadBytes;
        MaximumResidentEntryCount = maximumResidentEntryCount;
    }

    public SessionSelectedLineageAuditCapture Capture { get; }
    public EventAddress RootAddress { get; }
    public SessionHistoryPlanningSeed BootstrapSeed { get; }
    public SessionContextAnchorSetupReferences HeadSetups { get; }
    public SessionExecutionState ExecutionStateAtCapturedHead { get; }
    public long EventCount { get; }
    public long LogicalPayloadBytes { get; }
    public int MaximumResidentEntryCount { get; }
    internal SessionJournalEngine Owner => _owner;

}

/// <summary>
/// Stable, sealed view of one content-free audit spool. Implementations must
/// hold whatever lock or immutable file handles are needed to make every
/// enumeration observe the same bytes for this object's lifetime.
/// </summary>
public interface ISessionSelectedLineageAuditPageSnapshot : IDisposable {
    SessionSelectedLineageAuditCapture Capture { get; }
    long PageCount { get; }

    IEnumerable<SessionSelectedLineageAuditPage>
        ReadHeadToOldestPages();

    IEnumerable<SessionSelectedLineageAuditPage>
        ReadOldestToHeadPages();
}

/// <summary>
/// Opaque, content-free complete selected-lineage snapshot captured by the
/// mutable SessionJournal owner only during its lifecycle callback. It never
/// grants authority through a read view and cannot outlive that callback as a
/// usable cursor source.
/// </summary>
public sealed class SessionSelectedLineageAuditSnapshot :
    ISessionSelectedLineageAuditPageSnapshot {
    private readonly SessionJournalEngine _owner;
    private readonly IReadOnlyList<SessionSelectedLineageAuditPage> _pages;
    private int _disposed;

    internal SessionSelectedLineageAuditSnapshot(
        SessionJournalEngine owner,
        SessionSelectedLineageAuditCapture capture,
        IReadOnlyList<SessionSelectedLineageAuditPage> pages
    ) {
        _owner = owner;
        Capture = capture;
        _pages = pages;
    }

    public SessionSelectedLineageAuditCapture Capture { get; }
    public long PageCount => _pages.Count;

    public SessionSelectedLineageForwardCursor OpenForwardCursor(
        CancellationToken cancellationToken = default
    ) {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        return _owner.OpenLifecycleSelectedLineageForwardCursor(
            this, cancellationToken);
    }

    IEnumerable<SessionSelectedLineageAuditPage>
        ISessionSelectedLineageAuditPageSnapshot.ReadHeadToOldestPages() {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        return _pages;
    }

    IEnumerable<SessionSelectedLineageAuditPage>
        ISessionSelectedLineageAuditPageSnapshot.ReadOldestToHeadPages() {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        return _pages.Reverse();
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    internal SessionJournalEngine Owner => _owner;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}

public abstract record SessionSelectedLineageAuditSnapshotCaptureResult {
    private SessionSelectedLineageAuditSnapshotCaptureResult() { }

    public sealed record Available(SessionSelectedLineageAuditSnapshot Snapshot)
        : SessionSelectedLineageAuditSnapshotCaptureResult;
    public sealed record LimitExceeded(int MaximumEvents, long ObservedEvents)
        : SessionSelectedLineageAuditSnapshotCaptureResult;
    public sealed record Busy : SessionSelectedLineageAuditSnapshotCaptureResult;
    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : SessionSelectedLineageAuditSnapshotCaptureResult;
}

/// <summary>
/// Opaque membership-bound range emitted only by a forward cursor whose
/// complete sealed page snapshot was revalidated against raw authority.
/// </summary>
public sealed class SessionSelectedLineageForwardRange {
    private readonly SessionSelectedLineageAuditAuthority _owner;

    internal SessionSelectedLineageForwardRange(
        SessionSelectedLineageAuditAuthority owner,
        EventAddress startExclusive,
        IReadOnlyList<SessionSelectedLineageAuditEntry> entries,
        bool isFinal
    ) {
        _owner = owner;
        StartExclusive = startExclusive;
        Entries = entries;
        IsFinal = isFinal;
    }

    public EventAddress StartExclusive { get; }
    public EventAddress EndInclusive => Entries[^1].Address;
    public IReadOnlyList<SessionSelectedLineageAuditEntry> Entries {
        get;
    }
    public bool IsFinal { get; }
    internal SessionSelectedLineageAuditAuthority Owner => _owner;

}

public sealed record SessionSelectedLineageForwardConsumption(
    SessionHistoryPlanningWindow Window,
    SessionSelectedLineageForwardRange? RemainingRange
);

public enum SessionSelectedLineageBoundaryProbeDecision {
    Continue = 1,
    Match = 2,
    Stop = 3
}

public sealed record SessionSelectedLineageBoundaryProbeResult(
    EventAddress? LatestMatchingBoundary,
    bool Stopped
);

/// <summary>
/// Sequential root-to-captured-head reader over one revalidated sealed spool.
/// It begins immediately after the SessionCreated bootstrap boundary.
/// </summary>
public sealed class SessionSelectedLineageForwardCursor : IDisposable {
    private readonly SessionJournalEngine _owner;
    private readonly ISessionSelectedLineageAuditPageSnapshot _snapshot;
    private readonly IEnumerator<SessionSelectedLineageAuditEntry>
        _entries;
    private SessionHistoryPlanningSeed _currentSeed;
    private SessionSelectedLineageForwardRange? _pendingRange;
    private SessionSelectedLineageForwardRange? _previewedRange;
    private SessionHistoryPlanningWindow? _previewedWindow;
    private bool _finished;
    private bool _forwardEnumerationInvalid;
    private bool _inspectionExhausted;
    private bool _disposed;

    internal SessionSelectedLineageForwardCursor(
        SessionJournalEngine owner,
        ISessionSelectedLineageAuditPageSnapshot snapshot,
        SessionSelectedLineageAuditAuthority authority,
        IEnumerator<SessionSelectedLineageAuditEntry> entries,
        SessionHistoryPlanningSeed bootstrapSeed,
        bool ownerBoundLifecycleAudit = false
    ) {
        _owner = owner;
        _snapshot = snapshot;
        Authority = authority;
        _entries = entries;
        _currentSeed = bootstrapSeed;
        OwnerBoundLifecycleAudit = ownerBoundLifecycleAudit;
        _finished = bootstrapSeed.Address
            == authority.Capture.CapturedHead;
    }

    public SessionSelectedLineageAuditAuthority Authority { get; }
    public EventAddress CurrentBoundary => _currentSeed.Address;
    public SessionContextAnchorSetupReferences CurrentSetups
        => _currentSeed.Setups;

    /// <summary>
    /// Checks this cursor's owner-bound repository, Ref, and immutable audit
    /// head without trusting a separately supplied read view. An exhausted
    /// inspection is not reusable as authority for a new operation.
    /// </summary>
    public bool IsBoundTo(
        string repositoryPath,
        RefId refId,
        EventAddress capturedHead
    ) => _owner.IsSelectedLineageForwardCursorBoundTo(
        this,
        repositoryPath,
        refId,
        capturedHead
    );

    /// <summary>
    /// Reads the current head through the exact offline engine that issued
    /// this cursor. Offline promotion fences must use this owner-bound read.
    /// This remains available after one-shot inspection exhaustion so the
    /// operation that consumed the inspection can perform its final
    /// ledger-lock raw-head fence.
    /// </summary>
    public EventAddress? ReadCurrentHead()
        => _owner.ReadSelectedLineageForwardCursorCurrentHead(this);

    public SessionSelectedLineageForwardRange? ReadNextRange(
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) => _owner.ReadNextSelectedLineageForwardRange(
        this,
        maxRawEventCount,
        cancellationToken
    );

    /// <summary>
    /// Replaces the exact current pending suffix with an authority-bound
    /// range extended from the same boundary. The total cap includes every
    /// event already retained by <paramref name="exactPending"/> and cannot
    /// be smaller than that suffix. If cancellation or validation fails
    /// after consuming the underlying forward enumeration, this cursor is
    /// fail-closed and the caller must reopen it from its sealed snapshot.
    /// </summary>
    public SessionSelectedLineageForwardRange ExtendPendingRange(
        SessionSelectedLineageForwardRange exactPending,
        int maxTotalRawEventCount,
        CancellationToken cancellationToken = default
    ) => _owner.ExtendPendingSelectedLineageForwardRange(
        this,
        exactPending,
        maxTotalRawEventCount,
        cancellationToken
    );

    public SessionHistoryPlanningWindow Materialize(
        SessionSelectedLineageForwardRange range,
        CancellationToken cancellationToken = default
    ) => _owner.MaterializeSelectedLineageForwardRange(
        this,
        range,
        cancellationToken
    );

    /// <summary>
    /// Materializes the exact pending range without advancing the cursor, so
    /// an offline rebuild policy can select a replay-safe admission inside a
    /// bounded range.
    /// </summary>
    public SessionHistoryPlanningWindow Preview(
        SessionSelectedLineageForwardRange range,
        CancellationToken cancellationToken = default
    ) => _owner.PreviewSelectedLineageForwardRange(
        this,
        range,
        cancellationToken
    );

    /// <summary>
    /// Advances only through one replay-safe prefix of a previewed range and
    /// returns the still-authority-bound suffix, if any.
    /// </summary>
    public SessionSelectedLineageForwardConsumption
        ConsumePreviewedPrefix(
        SessionSelectedLineageForwardRange range,
        EventAddress endInclusive,
        CancellationToken cancellationToken = default
    ) => _owner.ConsumePreviewedSelectedLineagePrefix(
        this,
        range,
        endInclusive,
        cancellationToken
    );

    /// <summary>
    /// Replays content-free audited entries to one exact selected-lineage
    /// boundary. This is used only to resume an explicit rebuild from a
    /// previously Published admission. It rejects a cursor already consumed
    /// by a membership inspection.
    /// </summary>
    public void SeekToBoundary(
        EventAddress boundary,
        SessionContextAnchorSetupReferences setups,
        CancellationToken cancellationToken = default
    ) => _owner.SeekSelectedLineageForwardCursor(
        this,
        boundary,
        setups,
        cancellationToken
    );

    /// <summary>
    /// Performs one content-free forward membership pass and returns the last
    /// audited boundary contained in <paramref name="candidates"/>. The
    /// cursor becomes inspection-exhausted after this call and cannot be used
    /// for materialization.
    /// </summary>
    public EventAddress? FindLatestMatchingBoundary(
        IReadOnlySet<EventAddress> candidates,
        CancellationToken cancellationToken = default
    ) => _owner.FindLatestSelectedLineageBoundary(
        this,
        candidates,
        cancellationToken
    );

    /// <summary>
    /// Performs one content-free bootstrap-to-captured-head pass. The probe
    /// sees only already-validated selected-lineage addresses and may retain
    /// its latest match without supplying a candidate collection. Normal
    /// completion, Stop, callback failure, and cancellation all exhaust this
    /// cursor for further materialization; callback failure/cancellation also
    /// fail-close the underlying forward enumeration.
    /// </summary>
    public SessionSelectedLineageBoundaryProbeResult ProbeBoundaries(
        Func<
            EventAddress,
            SessionSelectedLineageBoundaryProbeDecision
        > probe,
        CancellationToken cancellationToken = default
    ) => _owner.ProbeSelectedLineageForwardBoundaries(
        this,
        probe,
        cancellationToken
    );

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _entries.Dispose();
        if (!OwnerBoundLifecycleAudit) {
            // Ordinary read-only offline cursors retain their historical
            // ownership contract. Lifecycle cursors borrow the shared sealed
            // snapshot from Online's AuditContext so reconciliation and
            // suffix construction may open independent cursors in one pass.
            _snapshot.Dispose();
        }
        _disposed = true;
    }

    internal SessionJournalEngine Owner => _owner;
    internal bool OwnerBoundLifecycleAudit { get; }
    internal SessionHistoryPlanningSeed CurrentSeed => _currentSeed;
    internal SessionSelectedLineageForwardRange? PendingRange =>
        _pendingRange;
    internal bool Finished => _finished;
    internal bool IsForwardEnumerationInvalid =>
        _forwardEnumerationInvalid;
    internal bool InspectionExhausted => _inspectionExhausted;
    internal bool IsDisposed => _disposed;
    internal SessionSelectedLineageForwardRange? PreviewedRange =>
        _previewedRange;
    internal SessionHistoryPlanningWindow? PreviewedWindow =>
        _previewedWindow;

    internal void SetPending(
        SessionSelectedLineageForwardRange range
    ) {
        if (_pendingRange is not null) {
            throw new InvalidOperationException(
                "A forward range is already pending materialization."
            );
        }
        _pendingRange = range;
    }

    internal void ReplacePending(
        SessionSelectedLineageForwardRange expected,
        SessionSelectedLineageForwardRange replacement
    ) {
        if (!ReferenceEquals(_pendingRange, expected)
            || _previewedRange is not null
            || _previewedWindow is not null) {
            throw new InvalidOperationException(
                "Only the exact unpreviewed pending range may be replaced."
            );
        }
        _pendingRange = replacement;
    }

    internal void InvalidateForwardEnumeration()
        => _forwardEnumerationInvalid = true;

    internal void Advance(
        SessionSelectedLineageForwardRange range,
        SessionHistoryPlanningSeed? nextSeed
    ) {
        if (!ReferenceEquals(_pendingRange, range)) {
            throw new InvalidOperationException(
                "Only the exact pending forward range may advance this cursor."
            );
        }
        if (!range.IsFinal && nextSeed is null) {
            throw new InvalidOperationException(
                "A non-final forward range requires its next planning seed."
            );
        }
        _currentSeed = nextSeed ?? _currentSeed;
        _pendingRange = null;
        _previewedRange = null;
        _previewedWindow = null;
        _finished = range.IsFinal;
    }

    internal void SetPreview(
        SessionSelectedLineageForwardRange range,
        SessionHistoryPlanningWindow window
    ) {
        if (!ReferenceEquals(_pendingRange, range)) {
            throw new InvalidOperationException(
                "Only the exact pending range may be previewed."
            );
        }
        if (_previewedRange is not null
            && !ReferenceEquals(_previewedRange, range)) {
            throw new InvalidOperationException(
                "Another forward range preview is already active."
            );
        }
        _previewedRange = range;
        _previewedWindow = window;
    }

    internal void AdvancePrefix(
        SessionSelectedLineageForwardRange range,
        SessionHistoryPlanningSeed? nextSeed,
        SessionSelectedLineageForwardRange? remaining
    ) {
        if (!ReferenceEquals(_pendingRange, range)
            || !ReferenceEquals(_previewedRange, range)
            || _previewedWindow is null) {
            throw new InvalidOperationException(
                "Only the exact previewed pending range may advance."
            );
        }
        if (remaining is not null && nextSeed is null) {
            throw new InvalidOperationException(
                "A remaining suffix requires a next planning seed."
            );
        }
        _currentSeed = nextSeed ?? _currentSeed;
        _pendingRange = remaining;
        _previewedRange = null;
        _previewedWindow = null;
        _finished = remaining is null && range.IsFinal;
    }

    internal void Seek(
        SessionHistoryPlanningSeed seed,
        bool finished
    ) {
        if (_pendingRange is not null || _previewedRange is not null) {
            throw new InvalidOperationException(
                "Cannot seek while a forward range is pending."
            );
        }
        _currentSeed = seed;
        _finished = finished;
    }

    internal void CompleteInspection() {
        if (_pendingRange is not null || _previewedRange is not null) {
            throw new InvalidOperationException(
                "Cannot complete inspection while a range is pending."
            );
        }
        _inspectionExhausted = true;
        _finished = true;
    }

    internal bool MoveNext(
        out SessionSelectedLineageAuditEntry entry
    ) {
        if (_forwardEnumerationInvalid) {
            throw new InvalidOperationException(
                "The forward cursor must be reopened after an interrupted enumeration."
            );
        }
        if (_entries.MoveNext()) {
            entry = _entries.Current;
            return true;
        }
        entry = null!;
        return false;
    }
}
