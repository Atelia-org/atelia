using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Central read facade for SessionJournal projections and recovery paths.
/// Counts logical EventJournal API reads so tests can assert semantic complexity without
/// depending on storage cache hits or wall-clock timing.
/// </summary>
internal sealed class SessionJournalEventReader(EventJournal.EventJournal journal) {
    private readonly EventJournal.EventJournal _journal =
        journal ?? throw new ArgumentNullException(nameof(journal));
    private long _headerPreviewReadCount;
    private long _payloadReadCount;
    private long _logicalPayloadByteCount;
    private long _currentLiveLogicalPayloadBytes;
    private long _peakLiveLogicalPayloadBytes;
    private long _chronologicalChainReadCount;
    private long _chronologicalEventCount;

    public AteliaResult<EventFrameHeader> ReadEventHeaderPreview(EventAddress address) {
        Interlocked.Increment(ref _headerPreviewReadCount);
        return _journal.ReadEventHeaderPreview(address);
    }

    public AteliaResult<SessionJournalEventFrame> ReadEvent(EventAddress address) {
        Interlocked.Increment(ref _payloadReadCount);
        AteliaResult<EventFrame> result = _journal.ReadEvent(address);
        if (result.IsFailure) {
            return result.Error!;
        }

        EventFrame frame = result.Unwrap();
        long logicalPayloadBytes = frame.Header.PayloadLength;
        try {
            Interlocked.Add(
                ref _logicalPayloadByteCount,
                logicalPayloadBytes
            );
            long current = Interlocked.Add(
                ref _currentLiveLogicalPayloadBytes,
                logicalPayloadBytes
            );
            UpdatePeakLiveLogicalPayloadBytes(current);
            return new SessionJournalEventFrame(
                frame,
                logicalPayloadBytes,
                this
            );
        }
        catch {
            Interlocked.Add(
                ref _currentLiveLogicalPayloadBytes,
                -logicalPayloadBytes
            );
            frame.Dispose();
            throw;
        }
    }

    public AteliaResult<IReadOnlyList<EventAddress>> ReadChronologicalChain(
        EventAddress head,
        bool checkedRead = false,
        int? maxDepth = null,
        bool detectCycles = true,
        CancellationToken cancellationToken = default
    ) {
        Interlocked.Increment(ref _chronologicalChainReadCount);
        AteliaResult<IReadOnlyList<EventAddress>> result = _journal.ReadChronologicalChain(
            head,
            checkedRead,
            maxDepth,
            detectCycles,
            cancellationToken
        );
        if (result.IsSuccess) {
            Interlocked.Add(ref _chronologicalEventCount, result.Unwrap().Count);
        }
        return result;
    }

    public SessionJournalReadDiagnostics CaptureDiagnostics()
        => new(
            HeaderPreviewReadCount: Interlocked.Read(ref _headerPreviewReadCount),
            PayloadReadCount: Interlocked.Read(ref _payloadReadCount),
            LogicalPayloadByteCount: Interlocked.Read(
                ref _logicalPayloadByteCount
            ),
            ChronologicalChainReadCount: Interlocked.Read(ref _chronologicalChainReadCount),
            ChronologicalEventCount: Interlocked.Read(ref _chronologicalEventCount),
            FullProjectionInvocationCount: 0
        );

    public SessionJournalPayloadLifetimeDiagnostics CapturePayloadLifetimeDiagnostics()
        => new(
            CurrentLiveLogicalPayloadBytes: Interlocked.Read(
                ref _currentLiveLogicalPayloadBytes
            ),
            PeakLiveLogicalPayloadBytes: Interlocked.Read(
                ref _peakLiveLogicalPayloadBytes
            )
        );

    internal void ReleaseLogicalPayloadBytes(long logicalPayloadBytes) {
        long current = Interlocked.Add(
            ref _currentLiveLogicalPayloadBytes,
            -logicalPayloadBytes
        );
        if (current < 0) {
            throw new InvalidOperationException(
                "SessionJournal logical payload lifetime accounting became negative."
            );
        }
    }

    private void UpdatePeakLiveLogicalPayloadBytes(long observed) {
        long peak = Interlocked.Read(ref _peakLiveLogicalPayloadBytes);
        while (observed > peak) {
            long prior = Interlocked.CompareExchange(
                ref _peakLiveLogicalPayloadBytes,
                observed,
                peak
            );
            if (prior == peak) {
                return;
            }
            peak = prior;
        }
    }
}

/// <summary>
/// SessionJournal-owned lease over an EventJournal frame. The reader uses this wrapper
/// to observe deterministic logical payload lifetime without changing EventJournal's
/// public frame contract.
/// </summary>
internal sealed class SessionJournalEventFrame : IDisposable {
    private EventFrame? _frame;
    private readonly long _logicalPayloadBytes;
    private readonly SessionJournalEventReader _owner;

    internal SessionJournalEventFrame(
        EventFrame frame,
        long logicalPayloadBytes,
        SessionJournalEventReader owner
    ) {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _logicalPayloadBytes = logicalPayloadBytes;
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public EventAddress Address
        => RequireFrame().Address;

    public EventFrameHeader Header
        => RequireFrame().Header;

    public ReadOnlySpan<byte> Payload
        => RequireFrame().Payload;

    public void Dispose() {
        EventFrame? frame = Interlocked.Exchange(ref _frame, null);
        if (frame is null) {
            return;
        }

        try {
            frame.Dispose();
        }
        finally {
            _owner.ReleaseLogicalPayloadBytes(_logicalPayloadBytes);
        }
    }

    private EventFrame RequireFrame()
        => _frame ?? throw new ObjectDisposedException(
            nameof(SessionJournalEventFrame)
        );
}
