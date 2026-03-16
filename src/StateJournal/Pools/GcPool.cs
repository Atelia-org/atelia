using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

/// <summary>
/// Sweep 回调的静态策略接口：用于在回收 slot 前对值执行零捕获处理。
/// </summary>
internal interface ISweepCollectHandler<T> where T : notnull {
    static abstract void OnCollect(T value);
}

// ai:test `tests/StateJournal.Tests/Pools/GcPoolTests.cs`
/// <summary>
/// 基于 Mark-Sweep 的 GC 值池。包装 <see cref="SlotPool{T}"/>，
/// 通过 <see cref="SlabBitmap"/> 实现高效的可达性标记与批量回收。
/// </summary>
/// <remarks>
/// 使用方式：
/// - <see cref="Store"/> 存入值，获得 handle。
/// - <see cref="BeginMark"/> 开始标记阶段。
/// - 对所有可达 handle 调用 <see cref="MarkReachable"/>。
/// - <see cref="Sweep"/> 回收所有不可达 handle。
///
/// Sweep 优化：Mark 阶段使用 <c>_reachable</c> 位图（初始全 clear），
/// 标记可达时 Set 对应位。Sweep 时先 <c>Or(freeBitmap)</c> 将空闲 slot 标为安全，
/// 再用 <see cref="SlabBitmap.EnumerateZerosReverse"/> 逆序迭代所有 `0`（= 不可达且已占用）的 slot，
/// 避免逐 slot if-check。
///
/// 注意：当前实现假设 Mark-Sweep 是 stop-the-world 的，
/// 即 <see cref="BeginMark"/> 和 <see cref="Sweep"/> 之间不应调用 <see cref="Store"/>。
/// 如需支持并发分配，需要额外的策略（如 write-barrier）。
/// </remarks>
/// <typeparam name="T">值类型，必须是 notnull。</typeparam>
internal sealed class GcPool<T> : IMarkSweepPool<T> where T : notnull {
    /// <summary>
    /// Compaction 回滚令牌：保存每次 MoveSlot 的 <see cref="SlotPool{T}.MoveRecord"/>，
    /// 用于 <see cref="RollbackCompaction"/> 在 shrink 最终提交前精确恢复 pool 状态。
    /// </summary>
    internal readonly struct CompactionJournal {
        public CompactionJournal(List<SlotPool<T>.MoveRecord> records) {
            Records = records;
        }

        public List<SlotPool<T>.MoveRecord> Records { get; }
    }

    private readonly struct NoOpSweepCollectHandler : ISweepCollectHandler<T> {
        public static void OnCollect(T value) { }
    }

    private readonly SlotPool<T> _pool;
    private readonly SlabBitmap _reachable;
    private bool _markPhaseActive;
    private static readonly AsyncLocal<CompactionFaultInjection?> s_compactionFaultInjection = new();

    /// <summary>活跃 slot 数量。</summary>
    public int Count => _pool.Count;

    /// <summary>容量上界。</summary>
    public int Capacity => _pool.Capacity;

    /// <summary>创建一个空的 <see cref="GcPool{T}"/>。</summary>
    public GcPool() {
        _pool = new SlotPool<T>();
        _reachable = new SlabBitmap();
    }

    /// <summary>从已有 SlotPool 构造（供 Rebuild 使用）。</summary>
    private GcPool(SlotPool<T> pool) {
        _pool = pool;
        _reachable = new SlabBitmap();
        SyncGrowth();
    }

    /// <summary>
    /// 从已有的 (SlotHandle, T) 映射批量重建 GcPool。
    /// 每个 handle 对应的 slot 被标记为 occupied 并写入值和 generation，
    /// 其余 slot 保持 free 状态，后续 <see cref="Store"/> 从最低空闲 index 分配。
    /// </summary>
    /// <param name="entries">
    /// 要恢复的 (handle, value) 集合。允许 index=0。
    /// 调用方必须保证每个 handle.Index 唯一（当前实现不做去重校验）。
    /// </param>
    public static GcPool<T> Rebuild(ReadOnlySpan<(SlotHandle Handle, T Value)> entries) {
        var pool = SlotPool<T>.Rebuild(entries);
        return new GcPool<T>(pool);
    }

    // ───────────────────── Store / Read ─────────────────────

    /// <summary>存入值，返回 handle。O(1) 均摊。</summary>
    public SlotHandle Store(T value) {
        SlotHandle handle = _pool.Store(value);
        SyncGrowth();
        return handle;
    }

    /// <summary>按 handle 读取值。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">handle 超出范围。</exception>
    /// <exception cref="InvalidOperationException">handle 对应 slot 未占用或 generation 不匹配。</exception>
    public T this[SlotHandle handle] {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _pool[handle];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _pool[handle] = value;
    }

    /// <summary>尝试读取 handle 对应的值。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(SlotHandle handle, out T value) => _pool.TryGetValue(handle, out value);

    /// <summary>验证 handle 是否有效（在范围内、slot 已占用、且 generation 匹配）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Validate(SlotHandle handle) => _pool.Validate(handle);

    // ───────────────────── Manual Free ─────────────────────

    /// <summary>
    /// 手动释放一个独占 slot。O(1)，随后可能触发尾部 slab 收缩。
    /// </summary>
    /// <remarks>
    /// 适用于独占所有权场景（如数值 Slot），在值被替换时可立即回收旧 Slot，
    /// 无需等到下一轮 Mark-Sweep。在 mark 阶段调用也是安全的：
    /// Sweep 的 <c>Or(freeBitmap)</c> 会将已释放 slot 标为 safe，不会 double-free。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">handle 超出范围。</exception>
    /// <exception cref="InvalidOperationException">generation 不匹配（过期 Handle）或 double-free。</exception>
    public void Free(SlotHandle handle) {
        _pool.Free(handle);
        SyncShrink();
    }

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
        Debug.Assert(_pool.Validate(handle), "Stale or invalid handle passed to MarkReachable.");
        _reachable.Set(handle.Index);
    }

    /// <summary>
    /// 尝试标记 handle 为可达。若已标记过则返回 false（用于 GC 图遍历的环检测）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryMarkReachable(SlotHandle handle) {
        Debug.Assert(_pool.Validate(handle), "Stale or invalid handle passed to TryMarkReachable.");
        int index = handle.Index;
        if (_reachable.Test(index)) { return false; } // 已标记
        _reachable.Set(index);
        return true;
    }

    /// <summary>
    /// 尝试读取 handle 对应值，并在未标记时标记为可达。
    /// 返回值表示 handle 是否存在；<paramref name="firstVisit"/> 表示是否是首次标记。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValueAndMarkFirstReachable(SlotHandle handle, out T value, out bool firstVisit) {
        if (!_pool.TryGetValue(handle, out value)) {
            firstVisit = false;
            return false;
        }

        int index = handle.Index;
        if (_reachable.Test(index)) {
            firstVisit = false;
            return true;
        }

        _reachable.Set(index);
        firstVisit = true;
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
    /// <remarks>
    /// 算法：
    /// - <c>_reachable |= freeBitmap</c>：将空闲 slot 标记为安全（1）。
    /// - 用 <see cref="SlabBitmap.EnumerateZerosReverse"/> 逆序迭代结果中的每个 clear bit（= 已占用且不可达），从高 index 向低释放，使尾部 slab 更早触发回收。
    ///
    /// 批量位运算 + tzcnt 迭代的组合使回收速度远优于逐 slot 检查。
    /// </remarks>
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
        _reachable.Or(_pool.FreeBitmap);

        int freed = 0;
        foreach (int index in _reachable.EnumerateZerosReverse()) {
            THandler.OnCollect(_pool[index]);
            _pool.Free(index);
            freed++;
        }

        // pool.Free 可能触发尾部 slab 收缩，同步 _unreachable
        SyncShrink();

        return freed;
    }

    // ───────────────────── Sync ─────────────────────

    private void SyncGrowth() {
        while (_reachable.SlabCount < _pool.FreeBitmap.SlabCount) { _reachable.GrowSlabAllZero(); }
    }

    private void SyncShrink() {
        while (_reachable.SlabCount > _pool.FreeBitmap.SlabCount) { _reachable.ShrinkLastSlab(); }
    }

    // ───────────────────── Compaction ─────────────────────

    /// <summary>
    /// 执行 compaction 并返回用于回滚的 undo token。
    /// Token 中保存每个 move 的原始 generation，支持在 shrink 最终提交前精确恢复。
    /// </summary>
    /// <remarks>
    /// 目标语义下，此方法属于纯内存内部算法：
    /// 在参数合法且 pool 预先满足自身不变量时，应做到“除灾难性运行时异常外不抛异常”；
    /// 若抛出普通异常，优先视为实现 bug / 状态破坏并 fail-fast。
    /// </remarks>
    internal CompactionJournal CompactWithUndo(int maxMoves) {
        Debug.Assert(!_markPhaseActive, "CompactWithUndo must be called after Sweep (mark phase must be inactive).");
        Debug.Assert(maxMoves >= 0);

        if (maxMoves == 0 || _pool.Count == 0) { return new CompactionJournal([]); }

        var records = new List<SlotPool<T>.MoveRecord>(maxMoves);
        try {
            int moved = 0;
            foreach (var (holeIndex, dataIndex) in _pool.FreeBitmap.EnumerateCompactionMoves()) {
                var record = _pool.MoveSlotRecorded(dataIndex, holeIndex);
                records.Add(record);
                if (records.Count == 1) { ThrowIfCompactionFaultInjected(); }
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
    /// 精确回滚一次已应用的 compaction：反向逐条恢复 slot 布局和 generation。
    /// 回滚后所有移动前的 <see cref="SlotHandle"/> 重新可用。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="CompactWithUndo"/> 相同，目标是“在 undo token 自洽时 no-throw except bug”。
    /// 此方法与 <see cref="TrimExcessCapacity"/> 存在时序争用：
    /// 一旦同一批 compaction 的尾部空 slab 已被 <see cref="TrimExcessCapacity"/> 收缩，
    /// 高位 <c>toIndex</c> 可能已不再可寻址，rollback 能力随之失效。
    /// 内部协作约定是：调用方必须在确认“不再需要对此批 compaction 执行 rollback”之后，
    /// 才能调用 <see cref="TrimExcessCapacity"/>。
    /// </remarks>
    internal void RollbackCompaction(CompactionJournal undoToken) {
        var records = undoToken.Records;
        if (records.Count > 0) {
            int maxToIndex = records[0].ToIndex;
            for (int i = 1; i < records.Count; i++) {
                if (records[i].ToIndex > maxToIndex) { maxToIndex = records[i].ToIndex; }
            }
            Debug.Assert(
                maxToIndex < _pool.Capacity,
                "RollbackCompaction must run before TrimExcessCapacity for the same compaction batch."
            );
        }

        // 逆序回滚每个 move（LIFO 顺序确保中间状态一致）
        for (int i = records.Count - 1; i >= 0; i--) {
            _pool.UndoMoveSlot(records[i]);
        }
    }

    /// <summary>
    /// 主动裁剪尾部空 slab，释放当前 pool 的多余容量。
    /// </summary>
    /// <remarks>
    /// 该操作与 compaction 本身并不强绑定；即使未发生新的 compaction，也可以单独调用以回收容量。
    ///
    /// 但它与 <see cref="RollbackCompaction"/> 存在时序争用：
    /// 若某批 compaction 的 rollback 窗口尚未关闭，调用此方法可能收缩掉 rollback 仍需访问的高位 slab，
    /// 从而破坏该批 compaction 的 rollback 能力。
    ///
    /// 内部协作约定是：只有在调用方已确认“不再需要针对当前 compaction batch 执行 rollback”之后，
    /// 才调用此方法。这里不做运行时异常检查，仅保留调试期契约说明。
    /// </remarks>
    internal void TrimExcessCapacity() {
        _pool.TrimExcess();
        SyncShrink();
    }

    internal static IDisposable InjectCompactionFaultScope(Func<Exception> exceptionFactory) {
        ArgumentNullException.ThrowIfNull(exceptionFactory);

        var previous = s_compactionFaultInjection.Value;
        s_compactionFaultInjection.Value = new CompactionFaultInjection(exceptionFactory);
        return new CompactionFaultScope(previous);
    }

    private static void ThrowIfCompactionFaultInjected() {
        var injection = s_compactionFaultInjection.Value;
        if (injection is null || !injection.Armed) { return; }
        injection.Armed = false;
        throw injection.ExceptionFactory();
    }

    private sealed class CompactionFaultInjection(Func<Exception> exceptionFactory) {
        public Func<Exception> ExceptionFactory { get; } = exceptionFactory;
        public bool Armed { get; set; } = true;
    }

    private sealed class CompactionFaultScope(CompactionFaultInjection? previous) : IDisposable {
        private bool _disposed;

        public void Dispose() {
            if (_disposed) { return; }
            s_compactionFaultInjection.Value = previous;
            _disposed = true;
        }
    }
}
