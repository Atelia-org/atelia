using System.Buffers;
using System.Numerics;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.ReadCache;

/// <summary>Page-based read cache for reverse-direction access.</summary>
/// <remarks>
/// What: A classic page cache tuned for reverse-direction access (RBF ScanReverse reads 20 B trailers at monotonically decreasing offsets).
///
/// Eviction: Circular FIFO — the oldest page slot is reused first.
/// No per-hit bookkeeping: sequential (reverse) workloads derive no benefit from LRU, and FIFO is O(1) with zero hit-path overhead.
///
/// Fill: Demand-filled on miss. A miss loads a contiguous block of pages into FIFO slots.
///
/// Invalidation: None. RBF files are append-only — once written, data is immutable. Cached pages never go stale.
///
/// Memory layout: A single flat <c>byte[]</c> allocation (<c>slotCount × pageSize</c>).
/// Page metadata lives in parallel <c>long[]</c> and <c>int[]</c> arrays.
/// Slot lookup is a linear scan over <c>_pageOf[]</c> — branchless and L1-friendly for typical slot counts (8–32).
///
/// Threading: Not thread-safe. RBF's single-writer / builder-blocks-reads model makes concurrent reads impossible by contract.
/// </remarks>
internal sealed class GeneralReadCache : RandomAccessReader {

    /// <summary>Sentinel value: slot is empty (holds no page).</summary>
    private const long NoPage = -1;

    // ── Configuration (immutable after ctor) ─────────────────────────

    private readonly int _pageSize;
    private readonly int _pageShift; // log₂(pageSize) for fast ÷
    private readonly int _pageMask; // pageSize − 1 for fast %
    private readonly int _slotCount;
    private readonly int _readBlockPages;

    // ── Page table ───────────────────────────────────────────────────

    private readonly byte[] _buf; // flat storage: slotCount × pageSize
    private readonly long[] _pageOf; // per-slot file page index, or NoPage
    private readonly int[] _validLen; // per-slot valid byte count (≤ pageSize)

    // ── Mutable state ────────────────────────────────────────────────

    private int _hand; // circular FIFO eviction cursor
    // ── Construction ─────────────────────────────────────────────────

    /// <summary>Creates a reverse read cache.</summary>
    /// <param name="file">File handle — non-owning; caller manages its lifetime.</param>
    /// <param name="pageSize">Bytes per page. MUST be a positive power of 2. Default: 4 096 (4 KB, matching typical OS page / sector size).</param>
    /// <param name="slotCount">Number of page slots. Total cache memory = <c>slotCount × pageSize</c>. Default: 16 (→ 64 KB).</param>
    /// <param name="readBlockPages">Number of pages to read per cache miss. Clamped to <c>slotCount</c>. Default: 1.</param>
    public GeneralReadCache(
        SafeFileHandle file,
        int pageSize = 4096,
        int slotCount = 16,
        int readBlockPages = 1
    ) : base(file) {
        if (pageSize <= 0 || !BitOperations.IsPow2(pageSize)) { throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Must be a positive power of 2."); }
        if (slotCount <= 0) { throw new ArgumentOutOfRangeException(nameof(slotCount), slotCount, "Must be positive."); }
        if (readBlockPages <= 0) { throw new ArgumentOutOfRangeException(nameof(readBlockPages), readBlockPages, "Must be positive."); }

        _pageSize = pageSize;
        _pageShift = BitOperations.Log2((uint)pageSize);
        _pageMask = pageSize - 1;
        _slotCount = slotCount;
        _readBlockPages = Math.Min(readBlockPages, slotCount);

        _buf = new byte[slotCount * pageSize];
        _pageOf = new long[slotCount];
        _validLen = new int[slotCount];
        Array.Fill(_pageOf, NoPage);

    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads up to <c>buffer.Length</c> bytes starting at <paramref name="offset"/>.
    /// Cross-page reads are handled transparently. Returns the number of bytes actually copied (may be less if the read reaches past the physical EOF).
    /// </remarks>
    protected override int ReadWithCache(long offset, Span<byte> buffer) {
        // Dispose状态和参数已经由基类在外层检查过了

        // ── Determine page span ──────────────────────────────────────
        long firstPage = offset >> _pageShift;
        long lastPage = (offset + buffer.Length - 1) >> _pageShift;

        // ── Copy from cached / freshly-loaded pages ──────────────────
        int copied = 0;
        long pos = offset;

        for (long p = firstPage; p <= lastPage; p++) {
            int slot = EnsureLoaded(p);
            int pageOff = (int)(pos & _pageMask);
            int avail = _validLen[slot] - pageOff;
            if (avail <= 0) { break; } // EOF within this page

            int n = Math.Min(avail, buffer.Length - copied);
            SlotSpan(slot, pageOff, n).CopyTo(buffer.Slice(copied));
            copied += n;
            pos += n;

            if (copied == buffer.Length) { break; } // request fully satisfied
        }

        return copied;
    }

    /// <inheritdoc />
    protected override void DisposeCache() {
        // _buf / _pageOf / _validLen are plain managed arrays — GC handles them.
    }

    // ── Page management (private) ────────────────────────────────────

    /// <summary>Returns the slot index that holds <paramref name="page"/>, loading from disk on cache miss.</summary>
    private int EnsureLoaded(long page) {
        // Linear probe over _pageOf[].
        // - For ≤ 32 slots the metadata array fits in 1–2 cache lines.
        // - Beats Dictionary: no hashing, no heap node, no branch-heavy lookup.
        // - Worst case: slotCount comparisons — negligible vs. one syscall.
        for (int i = 0; i < _slotCount; i++) {
            if (_pageOf[i] == page) { return i; }
        }

        return FetchBlock(page);
    }

    /// <summary>Reads a block of contiguous pages into FIFO slots, advances the cursor, and returns the slot for <paramref name="page"/>.</summary>
    private int FetchBlock(long page) {
        int blockPages = _readBlockPages;
        int firstSlot = _hand;
        int blockBytes = blockPages * _pageSize;
        long fileOffset = page << _pageShift;

        int bytesRead;
        if (firstSlot + blockPages <= _slotCount) {
            bytesRead = RawRead(fileOffset, SlotSpan(firstSlot, 0, blockBytes));
        }
        else {
            byte[] scratch = ArrayPool<byte>.Shared.Rent(blockBytes);
            try {
                Span<byte> scratchSpan = scratch.AsSpan(0, blockBytes);
                bytesRead = RawRead(fileOffset, scratchSpan);

                int firstRunPages = _slotCount - firstSlot;
                int firstRunBytes = firstRunPages * _pageSize;
                scratchSpan.Slice(0, firstRunBytes).CopyTo(SlotSpan(firstSlot, 0, firstRunBytes));

                int secondRunPages = blockPages - firstRunPages;
                int secondRunBytes = secondRunPages * _pageSize;
                scratchSpan.Slice(firstRunBytes, secondRunBytes).CopyTo(SlotSpan(0, 0, secondRunBytes));
            }
            finally {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        PopulateSlotMetadata(page, firstSlot, blockPages, bytesRead);
        _hand = (firstSlot + blockPages) % _slotCount;
        return firstSlot;
    }

    private void PopulateSlotMetadata(long startPage, int firstSlot, int blockPages, int bytesRead) {
        for (int i = 0; i < blockPages; i++) {
            int slot = (firstSlot + i) % _slotCount;
            long page = startPage + i;
            _pageOf[slot] = page;

            int remaining = bytesRead - (i * _pageSize);
            if (remaining <= 0) {
                _validLen[slot] = 0;
            }
            else if (remaining >= _pageSize) {
                _validLen[slot] = _pageSize;
            }
            else {
                _validLen[slot] = remaining;
            }
        }
    }

    /// <summary>Returns a <see cref="Span{T}"/> into the flat buffer for a given slot.</summary>
    private Span<byte> SlotSpan(int slot, int offset, int length) => _buf.AsSpan(slot * _pageSize + offset, length);
}
