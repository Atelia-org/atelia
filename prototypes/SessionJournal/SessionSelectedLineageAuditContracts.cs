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

    internal SessionSelectedLineageAuditSession(
        SessionJournalEngine owner,
        SessionSelectedLineageAuditCapture capture
    ) {
        _owner = owner;
        Capture = capture;
        _nextAddress = capture.CapturedHead;
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

    public SessionSelectedLineageForwardRange PrefixThrough(
        EventAddress endInclusive
    ) {
        int index = -1;
        for (int candidate = 0;
             candidate < Entries.Count;
             candidate++) {
            if (Entries[candidate].Address == endInclusive) {
                index = candidate;
                break;
            }
        }
        if (index < 0) {
            throw new ArgumentException(
                "Requested end is outside this forward range.",
                nameof(endInclusive)
            );
        }
        return new SessionSelectedLineageForwardRange(
            _owner,
            StartExclusive,
            Array.AsReadOnly([
                .. Entries.Take(index + 1)
            ]),
            IsFinal && index == Entries.Count - 1
        );
    }
}

/// <summary>
/// Sequential root-to-captured-head reader over one revalidated sealed spool.
/// It begins immediately after the SessionCreated bootstrap boundary.
/// </summary>
public sealed class SessionSelectedLineageForwardCursor : IDisposable {
    private readonly SessionJournalEngine _owner;
    private readonly ISessionSelectedLineageAuditPageSnapshot _snapshot;
    private readonly IEnumerator<SessionSelectedLineageAuditEntry>
        _entries;
    private EventAddress _nextParent;
    private bool _finished;
    private bool _disposed;

    internal SessionSelectedLineageForwardCursor(
        SessionJournalEngine owner,
        ISessionSelectedLineageAuditPageSnapshot snapshot,
        SessionSelectedLineageAuditAuthority authority,
        IEnumerator<SessionSelectedLineageAuditEntry> entries,
        EventAddress nextParent
    ) {
        _owner = owner;
        _snapshot = snapshot;
        Authority = authority;
        _entries = entries;
        _nextParent = nextParent;
    }

    public SessionSelectedLineageAuditAuthority Authority { get; }

    public SessionSelectedLineageForwardRange? ReadNextRange(
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) => _owner.ReadNextSelectedLineageForwardRange(
        this,
        maxRawEventCount,
        cancellationToken
    );

    public SessionHistoryPlanningWindow Materialize(
        SessionSelectedLineageForwardRange range,
        SessionHistoryPlanningSeed startSeed,
        CancellationToken cancellationToken = default
    ) => _owner.MaterializeSelectedLineageForwardRange(
        this,
        range,
        startSeed,
        cancellationToken
    );

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _entries.Dispose();
        _snapshot.Dispose();
        _disposed = true;
    }

    internal SessionJournalEngine Owner => _owner;
    internal EventAddress NextParent => _nextParent;
    internal bool Finished => _finished;
    internal bool IsDisposed => _disposed;

    internal void Advance(
        EventAddress nextParent,
        bool finished
    ) {
        _nextParent = nextParent;
        _finished = finished;
    }

    internal bool MoveNext(
        out SessionSelectedLineageAuditEntry entry
    ) {
        if (_entries.MoveNext()) {
            entry = _entries.Current;
            return true;
        }
        entry = null!;
        return false;
    }
}
