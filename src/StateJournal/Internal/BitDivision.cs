using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// 将 key 划分为 false 和 true 两个可数可枚举集合。
/// 使用 bitmap（而非双向链表）追踪子集归属，内存更紧凑、枚举更 cache 友好。
/// </summary>
internal sealed class BitDivision<TKey> : IBoolDivision<TKey> where TKey : notnull {
    [StructLayout(LayoutKind.Auto)]
    private struct Entry {
        public TKey Value;
        public int HashCode;   // lower 31 bits = cached hash
        public int BucketNext; // same-bucket chain, -1 = end; free slot: reused as free-list link
    }

    private const int HashCodeMask = 0x7FFFFFFF;
    private const int InitialCapacity = 4; // must be power of 2

    private readonly IEqualityComparer<TKey> _comparer;
    private Entry[] _entries;
    private int[] _buckets;       // _buckets[hash & mask] → chain head slot, -1 = empty
    private int _bucketMask;
    private int _nextSlot;        // high-water mark for slot allocation
    private int _freeList = -1;
    private int _freeCount;

    private ulong[] _occupied;    // bit set = slot is alive
    private ulong[] _subset;      // bit set = belongs to true subset (valid only when occupied)
    private int _falseCount, _trueCount;

    public int Capacity => _entries.Length;
    public int Count => _falseCount + _trueCount;
    public int FalseCount => _falseCount;
    public int TrueCount => _trueCount;

    public BitDivision(IEqualityComparer<TKey>? comparer = null) {
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
        _entries = new Entry[InitialCapacity];
        _buckets = new int[InitialCapacity];
        Array.Fill(_buckets, -1);
        _bucketMask = InitialCapacity - 1;
        int words = (InitialCapacity + 63) >> 6;
        _occupied = new ulong[words];
        _subset = new ulong[words];
    }

    /// <summary>将 key 放入 false 子集。若 key 不存在则新增；若已在 true 子集则翻转 1 bit 移动。</summary>
    public void SetFalse(TKey key) => SetSubsetCore(key, isTrueSubset: false);

    /// <summary>将 key 放入 true 子集。若 key 不存在则新增；若已在 false 子集则翻转 1 bit 移动。</summary>
    public void SetTrue(TKey key) => SetSubsetCore(key, isTrueSubset: true);

    /// <summary>从任一子集中移除 key。key 不存在时为 no-op。</summary>
    public void Remove(TKey key) {
        int hashCode = _comparer.GetHashCode(key) & HashCodeMask;
        int bucketIndex = hashCode & _bucketMask;

        int prev = -1;
        int i = _buckets[bucketIndex];
        while (i >= 0) {
            ref Entry e = ref _entries[i];
            if (e.HashCode == hashCode && _comparer.Equals(e.Value, key)) {
                // 先按 bitmap 判断子集归属，更新计数
                int word = i >> 6;
                ulong bit = 1UL << (i & 63);
                if ((_subset[word] & bit) != 0) { _trueCount--; }
                else { _falseCount--; }

                // 从 bucket chain 中摘除
                if (prev >= 0) { _entries[prev].BucketNext = e.BucketNext; }
                else { _buckets[bucketIndex] = e.BucketNext; }

                FreeSlot(i); // 同时清理 bitmap bits
                return;
            }
            prev = i;
            i = e.BucketNext;
        }
    }

    /// <summary>清空所有条目，恢复到初始空状态（保留已分配的数组）。</summary>
    public void Clear() {
        if (Count == 0) { return; }
        Array.Clear(_entries, 0, _nextSlot);
        Array.Fill(_buckets, -1);
        Array.Clear(_occupied);
        Array.Clear(_subset);
        _nextSlot = 0;
        _freeList = -1;
        _freeCount = 0;
        _falseCount = 0;
        _trueCount = 0;
    }

    public Enumerator FalseKeys => new(this, mask: ulong.MaxValue);
    public Enumerator TrueKeys => new(this, mask: 0UL);

    // ────────── 内部实现 ──────────

    private void SetSubsetCore(TKey key, bool isTrueSubset) {
        int hashCode = _comparer.GetHashCode(key) & HashCodeMask;
        int bucketIndex = hashCode & _bucketMask;

        // 查找已有条目
        int i = _buckets[bucketIndex];
        while (i >= 0) {
            ref Entry e = ref _entries[i];
            if (e.HashCode == hashCode && _comparer.Equals(e.Value, key)) {
                int word = i >> 6;
                ulong bit = 1UL << (i & 63);
                bool currentlyTrue = (_subset[word] & bit) != 0;
                if (currentlyTrue == isTrueSubset) { return; /* 已在目标子集，no-op */ }

                // 翻转 1 bit 完成子集间移动
                _subset[word] ^= bit;
                if (isTrueSubset) {
                    _falseCount--;
                    _trueCount++;
                }
                else {
                    _trueCount--;
                    _falseCount++;
                }
                return;
            }
            i = e.BucketNext;
        }

        // 新增条目
        if (Count * 4 > _entries.Length * 3) {
            Resize();
            bucketIndex = hashCode & _bucketMask;
        }

        int slot = AllocateSlot();
        ref Entry newEntry = ref _entries[slot];
        newEntry.Value = key;
        newEntry.HashCode = hashCode;
        newEntry.BucketNext = _buckets[bucketIndex];
        _buckets[bucketIndex] = slot;

        // 设置 bitmap
        int w = slot >> 6;
        ulong b = 1UL << (slot & 63);
        _occupied[w] |= b;
        if (isTrueSubset) {
            _subset[w] |= b;
            _trueCount++;
        }
        else {
            // _subset bit 已为 0（FreeSlot 清理过 或 从未设置）
            _falseCount++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AllocateSlot() {
        if (_freeCount > 0) {
            int index = _freeList;
            _freeList = _entries[index].BucketNext;
            _freeCount--;
            return index;
        }
        return _nextSlot++;
    }

    private void FreeSlot(int index) {
        ref Entry e = ref _entries[index];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) {
            e.Value = default!;
        }
        e.BucketNext = _freeList;
        _freeList = index;
        _freeCount++;

        // 清理 bitmap bits，确保 slot 复用时无残留
        int word = index >> 6;
        ulong bit = 1UL << (index & 63);
        _occupied[word] &= ~bit;
        _subset[word] &= ~bit;
    }

    /// <summary>2× 扩容：重建桶链，按需扩展 bitmap 数组。</summary>
    private void Resize() {
        int newSize = _entries.Length * 2;
        var newEntries = new Entry[newSize];
        Array.Copy(_entries, newEntries, _nextSlot);

        var newBuckets = new int[newSize];
        Array.Fill(newBuckets, -1);
        int newMask = newSize - 1;

        // 利用 occupied bitmap 扫描活跃 slot 重建桶链
        int usedWordCount = (_nextSlot + 63) >> 6;
        for (int w = 0; w < usedWordCount; w++) {
            ulong bits = _occupied[w];
            while (bits != 0) {
                int bit = BitOperations.TrailingZeroCount(bits);
                int j = (w << 6) | bit;
                ref Entry e = ref newEntries[j];
                int bucket = e.HashCode & newMask;
                e.BucketNext = newBuckets[bucket];
                newBuckets[bucket] = j;
                bits &= bits - 1; // clear lowest set bit
            }
        }

        // 扩展 bitmap 数组
        int newWordCount = (newSize + 63) >> 6;
        Array.Resize(ref _occupied, newWordCount);
        Array.Resize(ref _subset, newWordCount);

        _entries = newEntries;
        _buckets = newBuckets;
        _bucketMask = newMask;
    }

    // ────────── 枚举器 ──────────

    public ref struct Enumerator {
        readonly BitDivision<TKey> _root;
        readonly ulong _mask; // 0 = true 子集，ulong.MaxValue = false 子集
        int _wordIndex;
        ulong _currentWord; // 当前 word 中剩余待枚举的 bits
        int _currentSlot;

        internal Enumerator(BitDivision<TKey> root, ulong mask) {
            _root = root;
            _mask = mask;
            _wordIndex = 0;
            _currentSlot = -1;
            // 预计算第一个 word
            _currentWord = root._occupied.Length > 0
                ? ComputeWord(root, mask, 0)
                : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ComputeWord(BitDivision<TKey> root, ulong mask, int w) => root._occupied[w] & (root._subset[w] ^ mask);

        public TKey Current {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _root._entries[_currentSlot].Value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() {
            if (_currentWord != 0) {
                int bit = BitOperations.TrailingZeroCount(_currentWord);
                _currentSlot = (_wordIndex << 6) | bit;
                _currentWord &= _currentWord - 1; // clear lowest set bit
                return true;
            }
            return MoveNextRare();
        }

        /// <summary>慢路径：前进到下一个非空 word。</summary>
        private bool MoveNextRare() {
            var root = _root;
            while (++_wordIndex < root._occupied.Length) {
                _currentWord = ComputeWord(root, _mask, _wordIndex);
                if (_currentWord != 0) {
                    int bit = BitOperations.TrailingZeroCount(_currentWord);
                    _currentSlot = (_wordIndex << 6) | bit;
                    _currentWord &= _currentWord - 1;
                    return true;
                }
            }
            return false;
        }

        public Enumerator GetEnumerator() => this;
    }
}
