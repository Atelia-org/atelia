using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

// ai:test `tests/StateJournal.Tests/Pools/InternPoolTests.cs`
/// <summary>
/// 带内建 Mark-Sweep GC 的去重池（Intern Pool）。
/// 每个 distinct value 只存储一份，返回 stable slot index 作为 handle。
/// </summary>
/// <remarks>
/// 核心操作 <see cref="Store"/> 实现值去重：若池中已存在等价值则返回已有 index，
/// 否则分配新 slot。值一经 intern 即不可变（集合 mutable，元素 immutable）。
///
/// 生命周期管理：内建 Mark-Sweep GC，通过 <see cref="BeginMark"/> →
/// <see cref="MarkReachable"/> → <see cref="Sweep"/> 三阶段回收不可达值。
/// Sweep 会自动维护哈希桶链的一致性，调用方无需（也不能）手动释放。
///
/// 内部结构：<c>SlotPool&lt;Entry&gt;</c> 提供 slab 式 alloc/free/index-access，
/// 独立的 <c>int[] _buckets</c> + 桶内链表提供 O(1) 均摊的哈希查找。
/// 本质上是 .NET Dictionary 的 <c>buckets[] + entries[]</c> 结构，
/// 只是把 <c>entries[]</c> 替换为 SlotPool，获得稳定 index 和尾部自动回收。
///
/// GC 实现：与 <see cref="GcPool{T}"/> 采用相同的 <see cref="SlabBitmap"/> 位图模式，
/// 但 Sweep 时额外执行哈希桶链摘除，确保去重索引与存储的一致性。
/// 当前假设 Mark-Sweep 是 stop-the-world 的，
/// 即 <see cref="BeginMark"/> 和 <see cref="Sweep"/> 之间不应调用 <see cref="Store"/>。
/// </remarks>
/// <typeparam name="T">Interned 值类型，必须正确实现 GetHashCode / Equals（或提供 IEqualityComparer）。</typeparam>
internal sealed class InternPool<T> : IMarkSweepPool<T> where T : notnull {

    private struct Entry {
        public T Value;
        public int Next;      // 同桶内下一个 entry 的 slot index，-1 = 链尾
        public int HashCode;  // 缓存（非负，已 & 0x7FFFFFFF）
    }

    private const int InitialBucketCount = 4;

    private readonly SlotPool<Entry> _slots;
    private readonly IEqualityComparer<T> _comparer;
    private int[] _buckets;    // _buckets[hash & mask] → chain head slot index, -1 = 空桶
    private int _bucketMask;   // buckets.Length - 1（2^n sizing，用 & 代替 %）

    // ── GC 基础设施（与 GcPool 相同的 SlabBitmap 位图模式）──
    private readonly SlabBitmap _reachable;
    private bool _markPhaseActive;

    /// <summary>池中去重后的活跃值数量。</summary>
    public int Count => _slots.Count;

    /// <summary>
    /// 底层 SlotPool 的容量上界。
    /// 可迭代 <c>[0, Capacity)</c> 配合 <see cref="Validate"/> 遍历全部活跃 slot。
    /// </summary>
    public int Capacity => _slots.Capacity;

    /// <summary>创建一个空的 <see cref="InternPool{T}"/>。</summary>
    /// <param name="comparer">值相等比较器，null 时使用 <see cref="EqualityComparer{T}.Default"/>。</param>
    public InternPool(IEqualityComparer<T>? comparer = null) {
        _slots = new SlotPool<Entry>();
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _buckets = new int[InitialBucketCount];
        Array.Fill(_buckets, -1);
        _bucketMask = InitialBucketCount - 1;
        _reachable = new SlabBitmap();
    }

    // ───────────────────── Core: Intern ─────────────────────

    /// <summary>
    /// 去重获取 stable slot index。
    /// 若池中已存在等价值，返回已有 index；否则分配新 slot 并存入。O(1) 均摊。
    /// </summary>
    public SlotHandle Store(T value) {
        int hashCode = _comparer.GetHashCode(value) & 0x7FFFFFFF;
        int bucket = hashCode & _bucketMask;

        // 在链表中查找已有
        for (int i = _buckets[bucket]; i >= 0;) {
            ref Entry e = ref _slots.GetValueRef(i);
            if (e.HashCode == hashCode && _comparer.Equals(e.Value, value)) { return _slots.GetHandle(i); /* 去重命中 */ }
            i = e.Next;
        }

        // 未命中 → 分配新 slot
        var entry = new Entry {
            Value = value,
            HashCode = hashCode,
            Next = _buckets[bucket],
        };
        SlotHandle slot = _slots.Store(entry);
        _buckets[bucket] = slot.Index;

        // SlotPool 可能扩容了新 slab，同步 _reachable 位图
        SyncGrowth();

        // 按需扩容 bucket（load factor ≈ 1.0）
        if (_slots.Count > _buckets.Length) {
            Rehash(_buckets.Length * 2);
        }

        return slot;
    }

    // ───────────────────── Lookup ─────────────────────

    /// <summary>查找池中是否存在等价值。若存在，通过 <paramref name="index"/> 返回其 slot index。</summary>
    public bool TryGetIndex(T value, out SlotHandle index) {
        int hashCode = _comparer.GetHashCode(value) & 0x7FFFFFFF;
        int bucket = hashCode & _bucketMask;

        for (int i = _buckets[bucket]; i >= 0;) {
            ref Entry e = ref _slots.GetValueRef(i);
            if (e.HashCode == hashCode && _comparer.Equals(e.Value, value)) {
                index = _slots.GetHandle(i);
                return true;
            }
            i = e.Next;
        }

        index = default;
        return false;
    }

    /// <summary>检查池中是否存在等价值。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(T value) => TryGetIndex(value, out _);

    // ───────────────────── Access by index ─────────────────────

    /// <summary>按 slot index 读取 interned 值。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出范围。</exception>
    /// <exception cref="InvalidOperationException">slot 未被占用。</exception>
    public T this[SlotHandle index] {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _slots.GetValueRef(index).Value;
    }

    /// <summary>按 slot index 获取 interned 值的只读引用，避免值类型拷贝。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出范围。</exception>
    /// <exception cref="InvalidOperationException">slot 未被占用。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T GetRef(int index) => ref _slots.GetValueRef(index).Value;

    /// <summary>尝试读取 slot 的值。若 index 无效或 slot 未占用，返回 false。O(1)。</summary>
    public bool TryGetValue(SlotHandle index, out T value) {
        if (_slots.TryGetValue(index, out Entry entry)) {
            value = entry.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>检查指定 index 的 slot 是否已被占用。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出范围。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Validate(SlotHandle index) => _slots.Validate(index);

    // ───────────────────── Mark-Sweep GC ─────────────────────

    /// <summary>
    /// 开始标记阶段：将所有 slot 标记为不可达。
    /// 调用后，对每个可达 handle 调用 <see cref="MarkReachable"/>。
    /// </summary>
    public void BeginMark() {
        SyncGrowth();
        _reachable.ClearAll();
        _markPhaseActive = true;
    }

    /// <summary>
    /// 标记 handle 为可达。在 <see cref="BeginMark"/> 和 <see cref="Sweep"/> 之间调用。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkReachable(SlotHandle handle) {
        Debug.Assert(_slots.Validate(handle), "Stale or invalid handle passed to MarkReachable.");
        _reachable.Set(handle.Index);
    }

    /// <summary>
    /// 回收所有不可达且已占用的 slot。返回实际释放的 slot 数量。
    /// </summary>
    /// <remarks>
    /// 算法：
    /// - <c>_reachable |= freeBitmap</c>：将空闲 slot 标记为安全（1）。
    /// - 用 <see cref="SlabBitmap.EnumerateZerosReverse"/> 逆序迭代结果中的每个 clear bit
    ///   （= 已占用且不可达），从高 index 向低释放，使尾部 slab 更早触发回收。
    /// - 每个被 sweep 的 slot 会先从哈希桶链中摘除，再释放底层 slot，
    ///   确保去重索引与存储的一致性。
    /// </remarks>
    /// <exception cref="InvalidOperationException">未先调用 <see cref="BeginMark"/>。</exception>
    public int Sweep() {
        if (!_markPhaseActive) { throw new InvalidOperationException("Sweep must be called after BeginMark."); }
        _markPhaseActive = false;

        // reachable OR free = "safe"; zeros = unreachable AND occupied
        _reachable.Or(_slots.FreeBitmap);

        int freed = 0;
        foreach (int index in _reachable.EnumerateZerosReverse()) {
            Free(index);
            freed++;
        }

        // pool.Free 可能触发尾部 slab 收缩，同步 _reachable
        SyncShrink();

        return freed;
    }

    // ───────────────────── Internal lifecycle ─────────────────────

    /// <summary>
    /// 释放一个 slot：从哈希桶链中摘除，然后释放底层 slot。
    /// 仅由 <see cref="Sweep"/> 内部调用。O(1) 均摊。
    /// </summary>
    private void Free(int index) {
        // 读取 entry 信息（必须在 _slots.Free 之前）
        ref Entry entry = ref _slots.GetValueRef(index); // 同时验证 occupied
        int hashCode = entry.HashCode;
        int next = entry.Next;
        int bucket = hashCode & _bucketMask;

        // 从桶链表中摘除 index
        int prev = -1;
        int current = _buckets[bucket];
        while (current != index) {
            Debug.Assert(current >= 0, "Entry not found in bucket chain — internal state corrupted.");
            prev = current;
            current = _slots.GetValueRef(current).Next;
        }

        if (prev < 0) {
            _buckets[bucket] = next;
        }
        else {
            _slots.GetValueRef(prev).Next = next;
        }

        // 释放 slot（SlotPool 会自动回收尾部空页）
        _slots.Free(index);
    }

    // ───────────────────── Sync ─────────────────────

    private void SyncGrowth() {
        while (_reachable.SlabCount < _slots.FreeBitmap.SlabCount) { _reachable.GrowSlabAllZero(); }
    }

    private void SyncShrink() {
        while (_reachable.SlabCount > _slots.FreeBitmap.SlabCount) { _reachable.ShrinkLastSlab(); }
    }

    // ───────────────────── Rehash ─────────────────────

    /// <summary>扩容并重建 bucket 数组。遍历全部活跃 slot 重新分桶。</summary>
    private void Rehash(int newSize) {
        Debug.Assert(BitOperations.IsPow2(newSize));

        var newBuckets = new int[newSize];
        Array.Fill(newBuckets, -1);
        int newMask = newSize - 1;

        int cap = _slots.Capacity;
        for (int i = 0; i < cap; i++) {
            if (!_slots.IsOccupied(i)) { continue; }

            ref Entry e = ref _slots.GetValueRef(i);
            int b = e.HashCode & newMask;
            e.Next = newBuckets[b];
            newBuckets[b] = i;
        }

        _buckets = newBuckets;
        _bucketMask = newMask;
    }
}
