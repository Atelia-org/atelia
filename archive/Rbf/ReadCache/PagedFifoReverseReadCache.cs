using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Rbf.ReadCache;

/// <summary>
/// Paged FIFO cache with reverse-direction prefetch.
/// </summary>
/// <remarks>
/// - Cache policy: FIFO eviction, no recency updates on hit.
/// - Prefetch: when page indices decrease across calls, prefetch lower pages.
/// - Threading: not thread-safe.
/// </remarks>
internal sealed class PagedFifoReverseReadCache : RandomAccessReader {
    private readonly int _pageSize;
    private readonly int _capacityPages;
    private readonly int _prefetchPages;
    private readonly ArrayPool<byte> _pool;
    private readonly Slot[] _slots;
    private readonly Dictionary<long, int> _pageToSlot;
    private readonly Queue<int> _fifo;
    private readonly Stack<int> _free;

    private long _lastPageIndex;

    private struct Slot {
        public long PageIndex;
        public int ValidLength;
        public byte[]? Buffer;
        public bool IsValid;
    }

    public PagedFifoReverseReadCache(
        SafeFileHandle file,
        int pageSize,
        int capacityPages,
        int prefetchPages = 2,
        ArrayPool<byte>? pool = null
    ) :base(file) {
        if (pageSize <= 0) { throw new ArgumentOutOfRangeException(nameof(pageSize)); }
        if (capacityPages <= 0) { throw new ArgumentOutOfRangeException(nameof(capacityPages)); }
        if (prefetchPages < 0) { throw new ArgumentOutOfRangeException(nameof(prefetchPages)); }

        _pageSize = pageSize;
        _capacityPages = capacityPages;
        _prefetchPages = prefetchPages;
        _pool = pool ?? ArrayPool<byte>.Shared;

        _slots = new Slot[_capacityPages];
        _pageToSlot = new Dictionary<long, int>(_capacityPages);
        _fifo = new Queue<int>(_capacityPages);
        _free = new Stack<int>(_capacityPages);

        for (int i = _capacityPages - 1; i >= 0; i--) {
            _free.Push(i);
        }

        _lastPageIndex = -1;
    }

    protected override int ReadWithCache(long offset, Span<byte> buffer) {
        // Dispose状态和参数已经由基类在外层检查过了

        long firstPageIndex = offset / _pageSize;
        long lastByteOffset = offset + buffer.Length - 1;
        long lastPageIndex = lastByteOffset / _pageSize;

        int totalRead = 0;
        long currentOffset = offset;

        for (long pageIndex = firstPageIndex; pageIndex <= lastPageIndex; pageIndex++) {
            Slot slot = GetPage(pageIndex);
            if (!slot.IsValid || slot.Buffer == null) { break; }

            long pageStart = pageIndex * (long)_pageSize;
            int pageOffset = (int)(currentOffset - pageStart);
            if (pageOffset < 0 || pageOffset >= _pageSize) { break; }

            int available = slot.ValidLength - pageOffset;
            if (available <= 0) { break; }

            int remaining = buffer.Length - totalRead;
            int toCopy = available < remaining ? available : remaining;

            slot.Buffer.AsSpan(pageOffset, toCopy).CopyTo(buffer.Slice(totalRead, toCopy));

            totalRead += toCopy;
            currentOffset += toCopy;

            int pageRemaining = _pageSize - pageOffset;
            if (toCopy < pageRemaining) { break; }
            if (totalRead == buffer.Length) { break; }
        }

        if (totalRead > 0) {
            if (_lastPageIndex >= 0 && firstPageIndex < _lastPageIndex) {
                PrefetchReverse(firstPageIndex);
            }
            _lastPageIndex = firstPageIndex;
        }

        return totalRead;
    }

    protected override void DisposeCache() {
        // 防止重复Dispose由外层的基类负责

        for (int i = 0; i < _slots.Length; i++) {
            if (_slots[i].Buffer != null) {
                _pool.Return(_slots[i].Buffer!);
                _slots[i].Buffer = null;
            }
            _slots[i].IsValid = false;
        }

        _pageToSlot.Clear();
        _fifo.Clear();
        _free.Clear();
    }

    private Slot GetPage(long pageIndex) {
        if (_pageToSlot.TryGetValue(pageIndex, out int slotIndex)) { return _slots[slotIndex]; }

        slotIndex = LoadPage(pageIndex);
        return _slots[slotIndex];
    }

    private int LoadPage(long pageIndex) {
        int slotIndex;
        if (_free.Count > 0) {
            slotIndex = _free.Pop();
        }
        else {
            slotIndex = _fifo.Dequeue();
            if (_slots[slotIndex].IsValid) {
                _pageToSlot.Remove(_slots[slotIndex].PageIndex);
            }
        }

        ref Slot slot = ref _slots[slotIndex];
        if (slot.Buffer == null) {
            slot.Buffer = _pool.Rent(_pageSize);
        }

        long pageOffset = pageIndex * (long)_pageSize;
        int bytesRead = RawRead(pageOffset, slot.Buffer.AsSpan(0, _pageSize));

        slot.PageIndex = pageIndex;
        slot.ValidLength = bytesRead;
        slot.IsValid = true;

        _pageToSlot[pageIndex] = slotIndex;
        _fifo.Enqueue(slotIndex);

        return slotIndex;
    }

    private void PrefetchReverse(long currentPageIndex) {
        if (_prefetchPages <= 0) { return; }

        long pageIndex = currentPageIndex - 1;
        for (int i = 0; i < _prefetchPages; i++, pageIndex--) {
            if (pageIndex < 0) { break; }
            if (_pageToSlot.ContainsKey(pageIndex)) { continue; }

            LoadPage(pageIndex);
        }
    }
}
