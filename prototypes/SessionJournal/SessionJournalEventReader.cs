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
    private long _chronologicalChainReadCount;
    private long _chronologicalEventCount;

    public AteliaResult<EventFrameHeader> ReadEventHeaderPreview(EventAddress address) {
        Interlocked.Increment(ref _headerPreviewReadCount);
        return _journal.ReadEventHeaderPreview(address);
    }

    public AteliaResult<EventFrame> ReadEvent(EventAddress address) {
        Interlocked.Increment(ref _payloadReadCount);
        return _journal.ReadEvent(address);
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
            ChronologicalChainReadCount: Interlocked.Read(ref _chronologicalChainReadCount),
            ChronologicalEventCount: Interlocked.Read(ref _chronologicalEventCount),
            FullProjectionInvocationCount: 0
        );
}
