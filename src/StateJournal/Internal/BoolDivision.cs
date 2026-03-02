using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Atelia.StateJournal.Internal;

/// <summary>链表头 + 计数，与 <see cref="BoolDivision{TKey}"/> 共用的无泛型辅助结构。</summary>
internal struct Chain {
    public int Head;
    public int Count;

    /// <summary>Head = -1（空链），Count = 0 的初始值。</summary>
    public static Chain Empty => new() { Head = -1 };
}

/// <summary>将key划分为false和true两个可数可枚举集合。是一种key到bool的哈希表。</summary>
/// <typeparam name="TKey"></typeparam>
internal sealed class BoolDivision<TKey> : IBoolDivision<TKey> where TKey : notnull {
    [StructLayout(LayoutKind.Auto)]
    private struct Entry {
        public TKey Value;
        // 不存独立 bool——用 HashCode bit 31 编码子集归属（0=false, 1=true）。
        // 省内存、减 padding，代价是砍掉了 key→bool 的公开读取。
        public int HashCode; // lower 31 bits = 缓存哈希; bit 31 = 子集标志
        public int BucketNext; // 同桶链下一个 slot index, -1 = 链尾; 空闲 slot 复用为 free-list 链

        // for FalseKeys / TrueKeys 双向链表
        public int SubsetPrev, SubsetNext; // 空闲 slot: SubsetNext = FreeSlotMarker
    }

    /// <summary>HashCode bit 31 — 置 1 表示属于 true 子集。</summary>
    private const int SubsetBit = unchecked((int)0x80000000);
    private const int HashCodeMask = 0x7FFFFFFF;
    /// <summary>存入 SubsetNext 标记空闲 slot。合法的 SubsetNext 只会是 ≥ -1 的值。</summary>
    private const int FreeSlotMarker = -2;
    private const int InitialCapacity = 4; // 必须为 2 的幂

    private readonly IEqualityComparer<TKey> _comparer;
    private Entry[] _entries;
    private int[] _buckets; // _buckets[hash & mask] → chain head slot index, -1 = 空桶
    private int _bucketMask; // buckets.Length - 1（2^n sizing，用 & 代替 %）
    private int _nextSlot; // entries 高水位，[0, _nextSlot) 已分配或已释放
    private int _freeList = -1; // 空闲 slot 链表头
    private int _freeCount; // 空闲 slot 数量；不变量: _count + _freeCount == _nextSlot

    private Chain _falseChain = Chain.Empty;
    private Chain _trueChain = Chain.Empty;

    public int Capacity => _entries.Length;
    public int Count => _falseChain.Count + _trueChain.Count; // 活跃条目数
    public int FalseCount => _falseChain.Count;
    public int TrueCount => _trueChain.Count;

    public BoolDivision(IEqualityComparer<TKey>? comparer = null) {
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
        _entries = new Entry[InitialCapacity];
        _buckets = new int[InitialCapacity];
        Array.Fill(_buckets, -1);
        _bucketMask = InitialCapacity - 1;
    }

    /// <summary>将 key 放入 false 子集。若 key 不存在则新增；若已在 true 子集则 O(1) 移动。</summary>
    public void SetFalse(TKey key) => SetSubsetCore(key, 0, ref _trueChain, ref _falseChain);

    /// <summary>将 key 放入 true 子集。若 key 不存在则新增；若已在 false 子集则 O(1) 移动。</summary>
    public void SetTrue(TKey key) => SetSubsetCore(key, SubsetBit, ref _falseChain, ref _trueChain);

    /// <summary>从任一子集中移除 key。key 不存在时为 no-op。</summary>
    public void Remove(TKey key) {
        int hashCode = _comparer.GetHashCode(key) & HashCodeMask;
        int slot = FindKey(key, hashCode, out int prevSlot);
        if (slot < 0) { return; }

        ref Entry e = ref _entries[slot];

        // 从 bucket chain 中摘除
        if (prevSlot >= 0) { _entries[prevSlot].BucketNext = e.BucketNext; }
        else { _buckets[hashCode & _bucketMask] = e.BucketNext; }

        // 从子集链表中摘除
        if ((e.HashCode & SubsetBit) != 0) {
            UnlinkFromSubset(ref e, ref _trueChain);
        }
        else {
            UnlinkFromSubset(ref e, ref _falseChain);
        }

        // 回收 slot
        FreeSlot(slot);
    }

    /// <summary>清空所有条目，恢复到初始空状态（保留已分配的数组）。</summary>
    public void Clear() {
        if (Count == 0) { return; }
        Array.Clear(_entries, 0, _nextSlot);
        Array.Fill(_buckets, -1);
        _nextSlot = 0;
        _freeList = -1;
        _freeCount = 0;
        _falseChain = Chain.Empty;
        _trueChain = Chain.Empty;
    }

    public Enumerator FalseKeys => new(this, _falseChain.Head);
    public Enumerator TrueKeys => new(this, _trueChain.Head);

    // ────────── 内部实现 ──────────

    private void SetSubsetCore(TKey key, int dstSubsetBit, ref Chain fromChain, ref Chain toChain) {
        int hashCode = _comparer.GetHashCode(key) & HashCodeMask;

        // 阶段 1：查找已有条目
        int slot = FindKey(key, hashCode, out _);
        if (slot >= 0) {
            ref Entry e = ref _entries[slot];
            if ((e.HashCode & SubsetBit) == dstSubsetBit) { return; /* 已在目标子集，no-op */ }

            // O(1) 从当前子集摘除 → 插入目标子集
            UnlinkFromSubset(ref e, ref fromChain);
            LinkToSubset(slot, ref e, ref toChain);
            e.HashCode = hashCode | dstSubsetBit;
            return;
        }

        // 阶段 2：新增条目（扩容后重算桶位，故 bucketIndex 延迟到此处）
        if (Count * 4 > _entries.Length * 3) { Resize(); }

        int bucketIndex = hashCode & _bucketMask;
        slot = AllocateSlot();
        {
            ref Entry e = ref _entries[slot];
            e.Value = key;
            e.HashCode = hashCode | dstSubsetBit;
            e.BucketNext = _buckets[bucketIndex];
            _buckets[bucketIndex] = slot;

            LinkToSubset(slot, ref e, ref toChain);
        }
    }

    /// <summary>
    /// 在 bucket chain 中查找 key，返回 slot index（-1 = 不存在）。
    /// <paramref name="hashCode"/> MUST 为已掩码的低 31 位哈希。
    /// <paramref name="prevSlot"/> 返回链中前驱 slot（-1 = 链头或 key 不存在）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindKey(TKey key, int hashCode, out int prevSlot) {
        int prev = -1;
        int i = _buckets[hashCode & _bucketMask];
        while (i >= 0) {
            ref Entry e = ref _entries[i];
            if ((e.HashCode & HashCodeMask) == hashCode && _comparer.Equals(e.Value, key)) {
                prevSlot = prev;
                return i;
            }
            prev = i;
            i = e.BucketNext;
        }
        prevSlot = -1;
        return -1;
    }

    /// <summary>分配一个 slot（优先复用空闲 slot，否则从高水位推进）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AllocateSlot() {
        if (_freeCount > 0) {
            int index = _freeList;
            _freeList = _entries[index].BucketNext;
            _freeCount--;
            return index;
        }
        // 调用方已保证 _count < _entries.Length，
        // 且 _nextSlot == _count + _freeCount == _count < _entries.Length，所以此处不越界。
        return _nextSlot++;
    }

    /// <summary>回收一个 slot 到空闲链表。</summary>
    private void FreeSlot(int index) {
        ref Entry e = ref _entries[index];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) {
            e.Value = default!; // 释放引用，辅助 GC
        }
        e.BucketNext = _freeList;
        e.SubsetNext = FreeSlotMarker; // 标记空闲
        _freeList = index;
        _freeCount++;
    }

    /// <summary>将 slot 插入到目标子集双向链表的头部。</summary>
    private void LinkToSubset(int index, ref Entry e, ref Chain chain) {
        e.SubsetPrev = -1;
        e.SubsetNext = chain.Head;
        if (chain.Head >= 0) { _entries[chain.Head].SubsetPrev = index; }
        chain.Head = index;
        chain.Count++;
    }

    /// <summary>将 slot 从所属子集双向链表中摘除。</summary>
    private void UnlinkFromSubset(ref Entry e, ref Chain chain) {
        int prev = e.SubsetPrev, next = e.SubsetNext;

        if (prev >= 0) { _entries[prev].SubsetNext = next; }
        else { chain.Head = next; }

        if (next >= 0) { _entries[next].SubsetPrev = prev; }

        chain.Count--;
    }

    /// <summary>2× 扩容：重新分配 entries & buckets，重建桶链。子集链表和空闲链表不受影响。</summary>
    private void Resize() {
        int newSize = _entries.Length * 2;
        var newEntries = new Entry[newSize];
        Array.Copy(_entries, newEntries, _nextSlot);

        var newBuckets = new int[newSize];
        Array.Fill(newBuckets, -1);
        int newMask = newSize - 1;

        // 仅对活跃 slot 重建桶链；空闲 slot（SubsetNext == FreeSlotMarker）跳过。
        for (int j = 0; j < _nextSlot; j++) {
            ref Entry e = ref newEntries[j];
            if (e.SubsetNext == FreeSlotMarker) { continue; }

            int bucket = (e.HashCode & HashCodeMask) & newMask;
            e.BucketNext = newBuckets[bucket];
            newBuckets[bucket] = j;
        }

        _entries = newEntries;
        _buckets = newBuckets;
        _bucketMask = newMask;
    }

    // ────────── 枚举器 ──────────

    public ref struct Enumerator {
        readonly BoolDivision<TKey> _root;
        int _nextIndex;  // 下一个待返回的 slot index
        int _currentIndex; // 当前已返回的 slot index（供 Current 使用）

        internal Enumerator(BoolDivision<TKey> root, int head) {
            _root = root;
            _nextIndex = head;
            _currentIndex = -1;
        }

        public TKey Current {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _root._entries[_currentIndex].Value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() {
            if (_nextIndex < 0) { return false; }
            _currentIndex = _nextIndex;
            _nextIndex = _root._entries[_nextIndex].SubsetNext;
            return true;
        }

        public Enumerator GetEnumerator() => this;
    }
}
