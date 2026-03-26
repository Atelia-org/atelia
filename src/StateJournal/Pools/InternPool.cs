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
internal sealed class InternPool<T, TComparer> : IMarkSweepPool<T> where T : notnull where TComparer : unmanaged, IStaticEqualityComparer<T> {
    internal interface IEntryVisitor {
        void Visit(SlotHandle handle, T value);
    }

    /// <summary>
    /// Compaction 回滚令牌：保存每次 MoveSlot 的 <see cref="SlotPool{T}.MoveRecord"/>，
    /// 用于 <see cref="RollbackCompaction"/> 在 shrink 最终提交前精确恢复 pool 状态。
    /// </summary>
    internal readonly struct CompactionJournal {
        public CompactionJournal(List<SlotPool<Entry>.MoveRecord> records) {
            Records = records;
        }

        public List<SlotPool<Entry>.MoveRecord> Records { get; }
    }

    private readonly struct NoOpSweepCollectHandler : ISweepCollectHandler<T> {
        public static void OnCollect(T value) { }
    }

    internal struct Entry {
        public T Value;
        public int Next;      // 同桶内下一个 entry 的 slot index，-1 = 链尾
        public int HashCode;  // 缓存（非负，已 & 0x7FFFFFFF）
    }

    private const int InitialBucketCount = 4;

    private readonly SlotPool<Entry> _slots;
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
    public InternPool() {
        _slots = new SlotPool<Entry>();
        _buckets = new int[InitialBucketCount];
        Array.Fill(_buckets, -1);
        _bucketMask = InitialBucketCount - 1;
        _reachable = new SlabBitmap();
    }
    /// <summary>从已重建的 SlotPool 构造（供 <see cref="Rebuild"/> 使用）。</summary>
    private InternPool(SlotPool<Entry> slots) {
        _slots = slots;
        _reachable = new SlabBitmap();

        // 根据 count 选择合适的 bucket size
        int bucketCount = InitialBucketCount;
        while (slots.Count * 4 > bucketCount * 3) { bucketCount *= 2; }
        _buckets = new int[bucketCount];
        Array.Fill(_buckets, -1);
        _bucketMask = bucketCount - 1;

        // 从 slot 数据重建 bucket chains
        foreach (int i in _slots.EnumerateOccupiedIndices()) {
            ref Entry e = ref _slots.GetValueRefUnchecked(i);
            int b = e.HashCode & _bucketMask;
            e.Next = _buckets[b];
            _buckets[b] = i;
        }

        SyncGrowth();
    }

    /// <summary>
    /// 从已有的 (SlotHandle, T) 映射批量重建 InternPool。
    /// 每个 handle 对应的 slot 被恢复并重建 bucket chain。
    /// </summary>
    /// <param name="entries">
    /// 要恢复的 (handle, value) 集合。调用方必须保证每个 handle.Index 唯一且值已去重。
    /// </param>
    public static InternPool<T, TComparer> Rebuild(ReadOnlySpan<(SlotHandle Handle, T Value)> entries) {
        if (entries.IsEmpty) { return new InternPool<T, TComparer>(); }

        // 构建 Entry 数组以包含 HashCode
        var entrySpan = new (SlotHandle Handle, Entry Value)[entries.Length];
        for (int i = 0; i < entries.Length; i++) {
            int hashCode = TComparer.GetHashCode(entries[i].Value) & 0x7FFFFFFF;
            entrySpan[i] = (entries[i].Handle, new Entry {
                Value = entries[i].Value,
                HashCode = hashCode,
                Next = -1, // 由构造函数中的 Rehash 重建
            });
        }

        var slotPool = SlotPool<Entry>.Rebuild(entrySpan);
        return new InternPool<T, TComparer>(slotPool);
    }
    // ───────────────────── Core: Intern ─────────────────────

    /// <summary>
    /// 去重获取 stable slot index。
    /// 若池中已存在等价值，返回已有 index；否则分配新 slot 并存入。O(1) 均摊。
    /// </summary>
    public SlotHandle Store(T value) {
        return StorePrehashed(value, TComparer.GetHashCode(value));
    }

    /// <summary>
    /// 使用调用方已计算好的哈希值执行去重存储，避免重复计算 hash。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SlotHandle StorePrehashed(T value, int hashCode) => StoreCore(value, hashCode & 0x7FFFFFFF);

    // ───────────────────── Lookup ─────────────────────

    /// <summary>查找池中是否存在等价值。若存在，通过 <paramref name="index"/> 返回其 slot index。</summary>
    public bool TryGetIndex(T value, out SlotHandle index) {
        return TryGetIndexPrehashed(value, TComparer.GetHashCode(value), out index);
    }

    /// <summary>
    /// 使用调用方已计算好的哈希值执行查找，避免重复计算 hash。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetIndexPrehashed(T value, int hashCode, out SlotHandle index) {
        hashCode &= 0x7FFFFFFF;
        int bucket = hashCode & _bucketMask;

        for (int i = _buckets[bucket]; i >= 0;) {
            ref Entry e = ref _slots.GetValueRefUnchecked(i);
            if (e.HashCode == hashCode && TComparer.Equals(e.Value, value)) {
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SlotHandle StoreCore(T value, int hashCode) {
        int bucket = hashCode & _bucketMask;

        for (int i = _buckets[bucket]; i >= 0;) {
            ref Entry e = ref _slots.GetValueRefUnchecked(i);
            if (e.HashCode == hashCode && TComparer.Equals(e.Value, value)) { return _slots.GetHandle(i); }
            i = e.Next;
        }

        var entry = new Entry {
            Value = value,
            HashCode = hashCode,
            Next = _buckets[bucket],
        };
        SlotHandle slot = _slots.Store(entry);
        _buckets[bucket] = slot.Index;

        SyncGrowth();

        if (_slots.Count * 4 > _buckets.Length * 3) {
            Rehash(_buckets.Length * 2);
        }

        return slot;
    }

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
    /// 尝试标记 handle 为可达。若 handle 无效或 slot 未占用，返回 false。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryMarkReachable(SlotHandle handle) {
        if (!_slots.Validate(handle)) { return false; }
        _reachable.Set(handle.Index);
        return true;
    }

    /// <summary>
    /// 查询 handle 在当前 Mark 阶段是否已被标记为可达。
    /// 仅在 <see cref="BeginMark"/> 和 <see cref="Sweep"/> 之间有意义。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsMarkedReachable(SlotHandle handle) {
        return _reachable.Test(handle.Index);
    }

    /// <summary>
    /// 回收所有不可达且已占用的 slot。返回实际释放的 slot 数量。
    /// </summary>
    /// <exception cref="InvalidOperationException">未先调用 <see cref="BeginMark"/>。</exception>
    public int Sweep() => Sweep<NoOpSweepCollectHandler>();

    /// <summary>
    /// 回收所有不可达且已占用的 slot。返回实际释放的 slot 数量。
    /// 每个将被回收的值会在释放前调用一次 <typeparamref name="THandler"/>。
    /// </summary>
    /// <typeparam name="THandler">静态回调策略类型。</typeparam>
    /// <exception cref="InvalidOperationException">未先调用 <see cref="BeginMark"/>。</exception>
    public int Sweep<THandler>() where THandler : struct, ISweepCollectHandler<T> {
        if (!_markPhaseActive) { throw new InvalidOperationException("Sweep must be called after BeginMark."); }
        _markPhaseActive = false;

        // reachable OR free = "safe"; zeros = unreachable AND occupied
        _reachable.Or(_slots.FreeBitmap);

        int freed = 0;
        foreach (int index in _reachable.EnumerateZerosReverse()) {
            THandler.OnCollect(_slots.GetValueRefUnchecked(index).Value);
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
        ref Entry entry = ref _slots.GetValueRefUnchecked(index); // 同时验证 occupied
        int hashCode = entry.HashCode;
        int next = entry.Next;
        int bucket = hashCode & _bucketMask;

        // 从桶链表中摘除 index
        int prev = -1;
        int current = _buckets[bucket];
        while (current != index) {
            Debug.Assert(current >= 0, "Entry not found in bucket chain — internal state corrupted.");
            prev = current;
            current = _slots.GetValueRefUnchecked(current).Next;
        }

        if (prev < 0) {
            _buckets[bucket] = next;
        }
        else {
            _slots.GetValueRefUnchecked(prev).Next = next;
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

    internal void VisitEntries<TVisitor>(ref TVisitor visitor)
        where TVisitor : IEntryVisitor, allows ref struct {
        foreach (int i in _slots.EnumerateOccupiedIndices()) {
            ref Entry entry = ref _slots.GetValueRefUnchecked(i);
            visitor.Visit(_slots.GetHandle(i), entry.Value);
        }
    }

    // ───────────────────── Rehash ─────────────────────

    /// <summary>扩容并重建 bucket 数组。遍历全部活跃 slot 重新分桶。</summary>
    private void Rehash(int newSize) {
        Debug.Assert(BitOperations.IsPow2(newSize));

        var newBuckets = new int[newSize];
        Array.Fill(newBuckets, -1);
        int newMask = newSize - 1;

        foreach (int i in _slots.EnumerateOccupiedIndices()) {
            ref Entry e = ref _slots.GetValueRefUnchecked(i);
            int b = e.HashCode & newMask;
            e.Next = newBuckets[b];
            newBuckets[b] = i;
        }

        _buckets = newBuckets;
        _bucketMask = newMask;
    }

    // ───────────────────── Compaction ─────────────────────

    /// <summary>
    /// 执行 compaction 并返回用于回滚的 undo token。
    /// 每次 move 后在 bucket chain 中做 index rewrite（不做全量 rehash）。
    /// </summary>
    internal CompactionJournal CompactWithUndo(int maxMoves) {
        Debug.Assert(!_markPhaseActive, "CompactWithUndo must be called after Sweep (mark phase must be inactive).");
        Debug.Assert(maxMoves >= 0);

        if (maxMoves == 0 || _slots.Count == 0) { return new CompactionJournal([]); }

        var records = new List<SlotPool<Entry>.MoveRecord>(Math.Min(maxMoves, _slots.Count));
        try {
            int moved = 0;
            foreach (var (holeIndex, dataIndex) in _slots.FreeBitmap.EnumerateCompactionMoves()) {
                var record = _slots.MoveSlotRecorded(dataIndex, holeIndex);
                records.Add(record);
                RewriteChainRef(dataIndex, holeIndex);
                if (++moved >= maxMoves) { break; }
            }
        }
        catch {
            RollbackCompaction(new CompactionJournal(records));
            throw;
        }

        return new CompactionJournal(records);
    }

    /// <summary>
    /// 精确回滚一次已应用的 compaction：反向逐条恢复 slot 布局、generation 和 bucket chain。
    /// </summary>
    internal void RollbackCompaction(CompactionJournal undoToken) {
        var records = undoToken.Records;
        if (records.Count > 0) {
            int maxToIndex = records[0].ToIndex;
            for (int i = 1; i < records.Count; i++) {
                if (records[i].ToIndex > maxToIndex) { maxToIndex = records[i].ToIndex; }
            }
            Debug.Assert(
                maxToIndex < _slots.Capacity,
                "RollbackCompaction must run before TrimExcessCapacity for the same compaction batch."
            );
        }

        // 逆序回滚每个 move（LIFO 顺序确保中间状态一致）
        for (int i = records.Count - 1; i >= 0; i--) {
            var record = records[i];
            // Undo slot move: entry 从 toIndex (hole) 回到 fromIndex (data)
            _slots.UndoMoveSlot(record);
            // Rewrite chain ref: toIndex → fromIndex (entry 已回到 fromIndex)
            RewriteChainRef(record.ToIndex, record.FromIndex);
        }
    }

    /// <summary>
    /// 主动裁剪尾部空 slab，释放当前 pool 的多余容量。
    /// </summary>
    internal void TrimExcessCapacity() {
        _slots.TrimExcess();
        SyncShrink();
    }

    /// <summary>
    /// MoveSlot(from→to) 后，在 bucket chain 中将指向 <paramref name="oldIndex"/> 的引用改写为 <paramref name="newIndex"/>。
    /// Entry 已在 newIndex 位置。O(chain_length)，均摊 O(1)。
    /// </summary>
    private void RewriteChainRef(int oldIndex, int newIndex) {
        ref Entry e = ref _slots.GetValueRefUnchecked(newIndex);
        int bucket = e.HashCode & _bucketMask;

        if (_buckets[bucket] == oldIndex) {
            _buckets[bucket] = newIndex;
            return;
        }

        // 沿链搜索前驱
        int current = _buckets[bucket];
        while (current >= 0) {
            ref Entry ce = ref _slots.GetValueRefUnchecked(current);
            if (ce.Next == oldIndex) {
                ce.Next = newIndex;
                return;
            }
            current = ce.Next;
        }

        Debug.Fail($"oldIndex {oldIndex} not found in bucket chain for bucket {bucket} — internal state corrupted.");
    }
}
