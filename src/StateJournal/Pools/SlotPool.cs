using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

// ai:test `tests/StateJournal.Tests/Pools/SlotPoolTests.cs`
/// <summary>
/// 基于定长 Slab + 二级 Bitmap 的 Slot 分配器（Slab Allocator 变体）。
/// 提供 O(1) 的 Alloc / Free / 索引访问，并支持尾部空页自动回收。
/// </summary>
/// <remarks>
/// 设计要点：
/// - 位图操作委托给 <see cref="SlabBitmap"/>，其中 set bit（1）= free slot。
/// - 二级 summary bitmap 记录哪些 Slab 有空闲 slot，
/// Alloc 时通过 <see cref="SlabBitmap.FindFirstOne"/> 定位。
/// - Alloc 优先分配低 index 的 slot，自然压实数据向低地址端，
/// 使尾部 Slab 更易变为全空从而可回收。
/// - Free 后自动检查：若尾部连续 ≥2 个 Slab 全空，则保留 1 个作防抖缓冲、释放其余（双阈值滞回策略）。
/// 调用 <see cref="TrimExcess"/> 可强制释放所有空余容量（含缓冲 Slab）。
/// - 空闲状态由独立 bitmap 追踪，不嵌入 slot 内存，T 无尺寸约束。
/// - 每 slot 独立存储 8-bit generation 计数器（<c>byte[][]</c>），
/// <see cref="SlotHandle"/> 将 generation + index 打包为 32-bit 胖指针，
/// Free 时递增 generation 以检测过期访问（Stale Handle Detection）。
/// generation 数组不随 Slab 收缩而释放，确保跨 shrink/grow 周期的 ABA 防护。
/// </remarks>
/// <typeparam name="T">Slot 值类型，必须是 notnull。</typeparam>
internal sealed class SlotPool<T> : IValuePool<T> where T : notnull {

    private T[][] _slabs;          // slot 值
    private byte[][] _generations; // 每 slot 的 generation 计数器（独立 slab 存储，不随 Slab 收缩而释放）
    internal readonly SlabBitmap _freeBitmap;
    private int _count; // 活跃 slot 数量

    /// <summary>已分配的活跃 slot 数量。</summary>
    public int Count => _count;

    /// <summary>已分配的总 slot 容量。</summary>
    public int Capacity => _freeBitmap.Capacity;

    /// <summary>当前 SlabSize = 2^SlabShift。</summary>
    public int SlabSize => SlabBitmap.SlabSize;

    /// <summary>底层空闲位图，供同 assembly 内的组合层（如 GcPool）访问。</summary>
    internal SlabBitmap FreeBitmap => _freeBitmap;

    /// <summary>创建一个空的 <see cref="SlotPool{T}"/>。</summary>
    public SlotPool() {
        _slabs = new T[4][];
        _generations = new byte[4][];
        _freeBitmap = new SlabBitmap();
        _count = 0;
    }

    /// <summary>
    /// 从已有的 (SlotHandle, T) 映射批量重建 SlotPool。
    /// 每个 handle 对应的 slot 被标记为 occupied 并写入值和 generation，
    /// 其余 slot 保持 free 状态。
    /// </summary>
    /// <param name="entries">
    /// 要恢复的 (handle, value) 集合。允许 index=0。
    /// 调用方必须保证每个 handle.Index 唯一（当前实现不做去重校验）。
    /// </param>
    internal static SlotPool<T> Rebuild(ReadOnlySpan<(SlotHandle Handle, T Value)> entries) {
        if (entries.IsEmpty) { return new SlotPool<T>(); }

        int maxIndex = 0;
        foreach (var (handle, _) in entries) {
            if (handle.Index > maxIndex) { maxIndex = handle.Index; }
        }

        var pool = new SlotPool<T>();
        int requiredSlabs = (maxIndex >> SlabBitmap.SlabShift) + 1;
        for (int i = 0; i < requiredSlabs; i++) { pool.GrowOneSlab(); }

        foreach (var (handle, value) in entries) {
            int index = handle.Index;
            int slabIdx = index >> SlabBitmap.SlabShift;
            int offset = index & SlabBitmap.SlabMask;

            pool._slabs[slabIdx][offset] = value;
            pool._generations[slabIdx][offset] = handle.Generation;
            pool._freeBitmap.Clear(index);
        }

        pool._count = entries.Length;
        pool.TryShrinkTrailingSlabs();
        return pool;
    }

    /// <summary>分配一个 slot 并存入值，返回包含 generation 的 <see cref="SlotHandle"/>。O(1) 均摊。</summary>
    /// <exception cref="InvalidOperationException">池容量超出 <see cref="SlotHandle.MaxIndex"/>。</exception>
    public SlotHandle Store(T value) {
        int globalIdx = _freeBitmap.FindFirstOne();
        if (globalIdx < 0) {
            int nextIndex = _freeBitmap.SlabCount << SlabBitmap.SlabShift;
            if (nextIndex > SlotHandle.MaxIndex) {
                throw new InvalidOperationException(
                    $"Pool index {nextIndex} exceeds SlotHandle.MaxIndex ({SlotHandle.MaxIndex})."
                );
            }

            int newSlabIdx = GrowOneSlab();
            globalIdx = newSlabIdx << SlabBitmap.SlabShift; // 新 slab 全 set，首位确定
        }

        if (globalIdx > SlotHandle.MaxIndex) {
            throw new InvalidOperationException(
                $"Pool index {globalIdx} exceeds SlotHandle.MaxIndex ({SlotHandle.MaxIndex})."
            );
        }

        _freeBitmap.Clear(globalIdx);
        int slabIdx = globalIdx >> SlabBitmap.SlabShift;
        int offset = globalIdx & SlabBitmap.SlabMask;
        _slabs[slabIdx][offset] = value;
        byte gen = _generations[slabIdx][offset];
        _count++;
        if (globalIdx ==0 && gen == 0) {
            _generations[slabIdx][offset] = ++gen;
        }
        return new SlotHandle(gen, globalIdx);
    }

    /// <summary>释放一个 slot（不校验 generation）。O(1)，随后尝试回收尾部空页。</summary>
    /// <remarks>供内部组件（如 <see cref="GcPool{T}"/> 的 Sweep）使用裸 index 释放。
    /// 外部调用方应优先使用 <see cref="Free(SlotHandle)"/> 以获得 ABA 保护。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">index 超出当前容量范围。</exception>
    /// <exception cref="InvalidOperationException">对未占用的 slot 进行 Free（double-free）。</exception>
    internal void Free(int index) {
        if ((uint)index >= (uint)_freeBitmap.Capacity) { throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_freeBitmap.Capacity})."); }

        if (_freeBitmap.Test(index)) { throw new InvalidOperationException($"Double free detected: slot {index} is already free."); }

        FreeCore(index);
    }

    /// <summary>释放一个 slot（校验 generation）。O(1)，随后尝试回收尾部空页。</summary>
    /// <exception cref="ArgumentOutOfRangeException">handle 的 index 超出当前容量范围。</exception>
    /// <exception cref="InvalidOperationException">generation 不匹配（过期 Handle）或 double-free。</exception>
    public void Free(SlotHandle handle) {
        int index = handle.Index;
        if ((uint)index >= (uint)_freeBitmap.Capacity) { throw new ArgumentOutOfRangeException(nameof(handle), $"Handle index {index} must be in [0, {_freeBitmap.Capacity})."); }

        if (_freeBitmap.Test(index)) { throw new InvalidOperationException($"Double free detected: slot {index} is already free."); }

        byte storedGen = _generations[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask];
        if (storedGen != handle.Generation) {
            throw new InvalidOperationException(
                $"Stale handle: generation mismatch at index {index} (handle={handle.Generation}, stored={storedGen})."
            );
        }

        FreeCore(index);
    }

    /// <summary>释放核心逻辑：递增 generation、标记 free、清除引用、计数、尾部回收。</summary>
    private void FreeCore(int index) {
        int slabIdx = index >> SlabBitmap.SlabShift;
        int offset = index & SlabBitmap.SlabMask;

        // 递增 generation（8-bit 自然回绕 255→0）
        _generations[slabIdx][offset]++;

        _freeBitmap.Set(index);

        // 清除值引用，协助 GC（T 可能是引用类型）
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            _slabs[slabIdx][offset] = default!;
        }

        _count--;

        // 尾部连续空页回收
        TryShrinkTrailingSlabs();
    }

    /// <summary>记录一次 MoveSlot 的完整信息，用于精确回滚。</summary>
    internal readonly struct MoveRecord {
        public MoveRecord(int fromIndex, int toIndex, byte fromGenBefore, byte toGenBefore) {
            FromIndex = fromIndex;
            ToIndex = toIndex;
            FromGenBefore = fromGenBefore;
            ToGenBefore = toGenBefore;
        }

        public int FromIndex { get; }
        public int ToIndex { get; }
        public byte FromGenBefore { get; }
        public byte ToGenBefore { get; }

        /// <summary>移动后目标位置的 <see cref="SlotHandle"/>。</summary>
        public SlotHandle NewHandle => new(unchecked((byte)(ToGenBefore + 1)), ToIndex);

        /// <summary>移动前源位置的 <see cref="SlotHandle"/>。</summary>
        public SlotHandle OldHandle => new(FromGenBefore, FromIndex);
    }

    /// <summary>
    /// 将 fromIndex 的值移动到 toIndex。
    /// toIndex 必须是 free slot；fromIndex 在移动后变为 free。
    /// 目标位置 generation 自增 1（语义等价于"释放旧占用者 + 重新分配"），使指向旧占用者的 stale handle 失效。
    /// 源位置 generation 也自增（标准 Free 行为）。
    /// </summary>
    /// <param name="fromIndex">要移动的源 slot index（必须 occupied）。</param>
    /// <param name="toIndex">目标 slot index（必须 free）。</param>
    /// <returns>目标位置的新 <see cref="SlotHandle"/>（携带自增后的 generation）。</returns>
    internal SlotHandle MoveSlot(int fromIndex, int toIndex) {
        MoveSlotCore(fromIndex, toIndex, out _);
        return new SlotHandle(_generations[toIndex >> SlabBitmap.SlabShift][toIndex & SlabBitmap.SlabMask], toIndex);
    }

    /// <summary>
    /// 将 fromIndex 的值移动到 toIndex，并返回可用于精确回滚的 <see cref="MoveRecord"/>。
    /// </summary>
    internal MoveRecord MoveSlotRecorded(int fromIndex, int toIndex) {
        MoveSlotCore(fromIndex, toIndex, out var record);
        return record;
    }

    private void MoveSlotCore(int fromIndex, int toIndex, out MoveRecord record) {
        if ((uint)fromIndex >= (uint)_freeBitmap.Capacity) { throw new ArgumentOutOfRangeException(nameof(fromIndex), fromIndex, $"Index must be in [0, {_freeBitmap.Capacity})."); }
        if ((uint)toIndex >= (uint)_freeBitmap.Capacity) { throw new ArgumentOutOfRangeException(nameof(toIndex), toIndex, $"Index must be in [0, {_freeBitmap.Capacity})."); }
        if (_freeBitmap.Test(fromIndex)) { throw new InvalidOperationException($"Source slot {fromIndex} is not occupied."); }
        if (!_freeBitmap.Test(toIndex)) { throw new InvalidOperationException($"Target slot {toIndex} is not free."); }

        int fromSlab = fromIndex >> SlabBitmap.SlabShift;
        int fromOff = fromIndex & SlabBitmap.SlabMask;
        int toSlab = toIndex >> SlabBitmap.SlabShift;
        int toOff = toIndex & SlabBitmap.SlabMask;

        // 记录移动前的 generation（用于精确回滚）
        byte fromGenBefore = _generations[fromSlab][fromOff];
        byte toGenBefore = _generations[toSlab][toOff];
        record = new MoveRecord(fromIndex, toIndex, fromGenBefore, toGenBefore);

        // 复制值到目标位置
        _slabs[toSlab][toOff] = _slabs[fromSlab][fromOff];

        // 目标位置 generation++（使指向该位置旧占用者的 handle 失效）
        unchecked { _generations[toSlab][toOff]++; }

        // 标记目标位置为 occupied
        _freeBitmap.Clear(toIndex);

        // 释放源位置（generation++、标记 free、清除引用）—— 但不触发 TryShrinkTrailingSlabs，也不减 _count（总活跃数不变）
        _generations[fromSlab][fromOff]++;
        _freeBitmap.Set(fromIndex);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            _slabs[fromSlab][fromOff] = default!;
        }

        // _count 不变（一进一出）
    }

    /// <summary>
    /// 精确回滚一次 <see cref="MoveSlotRecorded"/> 操作：
    /// 将值从 toIndex 移回 fromIndex，并恢复双端 generation 到移动前的值。
    /// 调用方需保证在回滚前已通过 <see cref="EnsureCapacity"/> 恢复了足够的 slab 容量。
    /// </summary>
    internal void UndoMoveSlot(MoveRecord record) {
        int fromIndex = record.FromIndex;
        int toIndex = record.ToIndex;

        Debug.Assert((uint)toIndex < (uint)_freeBitmap.Capacity, $"Target index {toIndex} out of capacity {_freeBitmap.Capacity} during undo.");
        Debug.Assert((uint)fromIndex < (uint)_freeBitmap.Capacity, $"Source index {fromIndex} out of capacity {_freeBitmap.Capacity} during undo.");
        Debug.Assert(!_freeBitmap.Test(toIndex), $"Target slot {toIndex} should be occupied during undo.");
        Debug.Assert(_freeBitmap.Test(fromIndex), $"Source slot {fromIndex} should be free during undo.");

        int fromSlab = fromIndex >> SlabBitmap.SlabShift;
        int fromOff = fromIndex & SlabBitmap.SlabMask;
        int toSlab = toIndex >> SlabBitmap.SlabShift;
        int toOff = toIndex & SlabBitmap.SlabMask;

        // 将值从目标位移回源位
        _slabs[fromSlab][fromOff] = _slabs[toSlab][toOff];

        // 精确恢复双端 generation
        _generations[fromSlab][fromOff] = record.FromGenBefore;
        _generations[toSlab][toOff] = record.ToGenBefore;

        // 翻转 bitmap：源位恢复为 occupied，目标位恢复为 free
        _freeBitmap.Clear(fromIndex);
        _freeBitmap.Set(toIndex);

        // 清除目标位的值引用
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            _slabs[toSlab][toOff] = default!;
        }

        // _count 不变（一进一出）
    }

    /// <summary>
    /// 确保 pool 容量至少为 <paramref name="minCapacity"/>（按 SlabSize 对齐增长）。
    /// 用于 compaction 回滚前恢复被 <see cref="TrimExcess"/> 收缩的 slab。
    /// </summary>
    internal void EnsureCapacity(int minCapacity) {
        while (_freeBitmap.Capacity < minCapacity) {
            GrowOneSlab();
        }
    }

    /// <summary>获取 slot 的值的可变引用。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出当前容量范围。</exception>
    /// <exception cref="InvalidOperationException">访问未占用的 slot。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetValueRef(int index) {
        ThrowIfNotOccupied(index);
        return ref _slabs[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetValueRefUnchecked(int index) {
#if DEBUG
        ThrowIfNotOccupied(index);
#endif
        return ref _slabs[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask];
    }

    /// <summary>按 index 创建已占用 slot 的 handle（仅内部使用）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SlotHandle GetHandle(int index) {
        Debug.Assert(IsOccupied(index), "Slot is not occupied.");
        byte gen = _generations[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask];
        return new SlotHandle(gen, index);
    }

    /// <summary>获取 slot 的值的可变引用（校验 generation）。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">handle 超出当前容量范围。</exception>
    /// <exception cref="InvalidOperationException">slot 未占用或 generation 不匹配。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetValueRef(SlotHandle handle) {
        ThrowIfNotOccupiedOrStale(handle);
        int index = handle.Index;
        return ref _slabs[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask];
    }

    /// <summary>读取或更新已占用 slot 的值。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出当前容量范围。</exception>
    /// <exception cref="InvalidOperationException">访问未占用的 slot。</exception>
    internal T this[int index] {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get {
            ThrowIfNotOccupied(index);
            return _slabs[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set {
            ThrowIfNotOccupied(index);
            _slabs[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask] = value;
        }
    }

    /// <summary>读取或更新已占用 slot 的值（校验 generation）。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">handle 超出当前容量范围。</exception>
    /// <exception cref="InvalidOperationException">slot 未占用或 generation 不匹配。</exception>
    public T this[SlotHandle handle] {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get {
            ThrowIfNotOccupiedOrStale(handle);
            int index = handle.Index;
            return _slabs[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set {
            ThrowIfNotOccupiedOrStale(handle);
            int index = handle.Index;
            _slabs[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask] = value;
        }
    }

    /// <summary>尝试读取 slot 的值。若 index 超出范围或 slot 未占用，返回 false。O(1)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetValue(int index, out T value) {
        if ((uint)index < (uint)_freeBitmap.Capacity && !_freeBitmap.Test(index)) {
            value = _slabs[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask];
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>尝试读取 slot 的值（校验 generation）。若 handle 无效、slot 未占用或 generation 不匹配，返回 false。O(1)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(SlotHandle handle, out T value) {
        int index = handle.Index;
        if ((uint)index < (uint)_freeBitmap.Capacity
            && !_freeBitmap.Test(index)
            && _generations[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask] == handle.Generation) {
            value = _slabs[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask];
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>检查指定 index 的 slot 是否已分配（occupied）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出当前容量范围。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsOccupied(int index) {
        if ((uint)index >= (uint)_freeBitmap.Capacity) { throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_freeBitmap.Capacity})."); }
        return !_freeBitmap.Test(index);
    }

    /// <summary>
    /// 正序枚举所有已占用 slot 的 index。
    /// 基于 free-bitmap 的零位枚举，能跳过整段空闲区域，避免按容量全扫。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal OccupiedIndexEnumerator EnumerateOccupiedIndices() => new(_freeBitmap);

    /// <summary>
    /// 检查 Handle 是否仍然有效：index 在范围内、slot 已占用、且 generation 匹配。
    /// 与 <see cref="IsOccupied(int)"/> 不同，对越界 index 返回 false 而非抛异常。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Validate(SlotHandle handle) {
        int index = handle.Index;
        return (uint)index < (uint)_freeBitmap.Capacity
            && !_freeBitmap.Test(index)
            && _generations[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask] == handle.Generation;
    }

    /// <summary>
    /// 正序枚举 <see cref="SlotPool{T}"/> 中所有已占用 slot index 的零分配枚举器。
    /// </summary>
    internal ref struct OccupiedIndexEnumerator {
        private SlabBitmap.ZerosForwardEnumerator _zeros;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal OccupiedIndexEnumerator(SlabBitmap freeBitmap) {
            _zeros = freeBitmap.EnumerateZeros();
        }

        public int Current => _zeros.Current;

        public OccupiedIndexEnumerator GetEnumerator() => this;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _zeros.MoveNext();
    }

    // ───────────────────── Validation ─────────────────────

    /// <summary>验证 index 在范围内且对应 slot 已占用，否则抛出异常。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfNotOccupied(int index) {
        if ((uint)index >= (uint)_freeBitmap.Capacity) { throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_freeBitmap.Capacity})."); }
        if (_freeBitmap.Test(index)) { throw new InvalidOperationException($"Slot {index} is not occupied (already freed or never allocated)."); }
    }

    /// <summary>验证 handle 对应 slot 已占用且 generation 匹配，否则抛出异常。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfNotOccupiedOrStale(SlotHandle handle) {
        int index = handle.Index;
        if ((uint)index >= (uint)_freeBitmap.Capacity) { throw new ArgumentOutOfRangeException(nameof(handle), $"Handle index {index} must be in [0, {_freeBitmap.Capacity})."); }
        if (_freeBitmap.Test(index)) { throw new InvalidOperationException($"Slot {index} is not occupied (already freed or never allocated)."); }
        byte storedGen = _generations[index >> SlabBitmap.SlabShift][index & SlabBitmap.SlabMask];
        if (storedGen != handle.Generation) {
            throw new InvalidOperationException(
                $"Stale handle: generation mismatch at index {index} (handle={handle.Generation}, stored={storedGen})."
            );
        }
    }

    // ───────────────────── Capacity management ─────────────────────

    /// <summary>增长一个新页，所有 slot 标记为 free，返回新页的 slab index。</summary>
    private int GrowOneSlab() {
        int slabIdx = _freeBitmap.SlabCount;

        if (slabIdx >= _slabs.Length) {
            Array.Resize(ref _slabs, _slabs.Length * 2);
            Array.Resize(ref _generations, _generations.Length * 2);
        }

        _slabs[slabIdx] = new T[SlabBitmap.SlabSize];
        // 仅首次分配 generation 数组；收缩后重新增长时复用已有数组（保留 generation 历史，防止 ABA）
        _generations[slabIdx] ??= new byte[SlabBitmap.SlabSize];
        _freeBitmap.GrowSlabAllOne();

        return slabIdx;
    }

    /// <summary>
    /// 回收尾部连续的全空 Slab，但保留 1 个全空 Slab 作为防抖缓冲。
    /// 双阈值滞回策略：只有尾部 ≥2 个连续全空 Slab 才开始收缩，收缩后至少保留 1 个空 Slab。
    /// </summary>
    private void TryShrinkTrailingSlabs() {
        while (_freeBitmap.SlabCount >= 2
               && _freeBitmap.GetOneCount(_freeBitmap.SlabCount - 1) == SlabBitmap.SlabSize
               && _freeBitmap.GetOneCount(_freeBitmap.SlabCount - 2) == SlabBitmap.SlabSize) {
            int last = _freeBitmap.SlabCount - 1;
            _slabs[last] = null!;
            _freeBitmap.ShrinkLastSlab();
        }
    }

    /// <summary>
    /// 强制释放所有空余容量，包括防抖缓冲的空 Slab。
    /// 调用后 Capacity 收缩到恰好容纳所有已占用 slot 的最小 Slab 数量。
    /// </summary>
    public void TrimExcess() {
        while (_freeBitmap.SlabCount > 0
               && _freeBitmap.GetOneCount(_freeBitmap.SlabCount - 1) == SlabBitmap.SlabSize) {
            int last = _freeBitmap.SlabCount - 1;
            _slabs[last] = null!;
            _freeBitmap.ShrinkLastSlab();
        }
    }
}
