using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

/// <summary>
/// 基于定长 Slab + 二级 Bitmap 的 Slot 分配器（Slab Allocator 变体）。
/// 提供 O(1) 的 Alloc / Free / 索引访问，并支持尾部空页自动回收。
/// </summary>
/// <remarks>
/// 设计要点：
/// - 位图操作委托给 <see cref="SlabBitmap"/>，其中 set bit（1）= free slot。
/// - 二级 summary bitmap 记录哪些 Slab 有空闲 slot，
///   Alloc 时通过 <see cref="SlabBitmap.FindFirstOne"/> 定位。
/// - Alloc 优先分配低 index 的 slot，自然压实数据向低地址端，
///   使尾部 Slab 更易变为全空从而可回收。
/// - Free 后自动检查：若尾部连续 ≥2 个 Slab 全空，则保留 1 个作防抖缓冲、释放其余（双阈值滞回策略）。
///   调用 <see cref="TrimExcess"/> 可强制释放所有空余容量（含缓冲 Slab）。
/// - 空闲状态由独立 bitmap 追踪，不嵌入 slot 内存，T 无尺寸约束。
/// </remarks>
/// <typeparam name="T">Slot 值类型，必须是 notnull。</typeparam>
internal sealed class SlotPool<T> where T : notnull {
    public const int MinSlabShift = SlabBitmap.MinSlabShift;
    public const int MaxSlabShift = SlabBitmap.MaxSlabShift;
    public const int DefaultSlabShift = 10;

    private readonly int _slabShift; // log2(slabSize)
    private readonly int _slabSize;
    private readonly int _slabMask;  // slabSize - 1

    private T[][] _slabs;          // slot 值
    internal readonly SlabBitmap _freeBitmap;
    private int _count; // 活跃 slot 数量

    /// <summary>已分配的活跃 slot 数量。</summary>
    public int Count => _count;

    /// <summary>已分配的总 slot 容量。</summary>
    public int Capacity => _freeBitmap.Capacity;

    /// <summary>当前 SlabSize = 2^SlabShift。</summary>
    public int SlabSize => _slabSize;

    /// <summary>底层空闲位图，供同 assembly 内的组合层（如 GcPool）访问。</summary>
    internal SlabBitmap FreeBitmap => _freeBitmap;

    /// <summary>创建一个空的 <see cref="SlotPool{T}"/>。</summary>
    /// <param name="slabShift">SlabSize = 2^slabShift。</param>
    public SlotPool(int slabShift = DefaultSlabShift) {
        if (slabShift < MinSlabShift || slabShift > MaxSlabShift) { throw new ArgumentOutOfRangeException(nameof(slabShift), slabShift, $"slabShift must be in [{MinSlabShift}, {MaxSlabShift}]."); }

        _slabShift = slabShift;
        _slabSize = 1 << slabShift;
        _slabMask = _slabSize - 1;

        _slabs = new T[4][];
        _freeBitmap = new SlabBitmap(slabShift);
        _count = 0;
    }

    /// <summary>分配一个 slot 并存入值，返回全局 index。O(1) 均摊。</summary>
    // [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Alloc(T value) {
        int globalIdx = _freeBitmap.FindFirstOne();
        if (globalIdx < 0) {
            int newSlabIdx = GrowOneSlab();
            globalIdx = newSlabIdx << _slabShift; // 新 slab 全 set，首位确定
        }

        _freeBitmap.Clear(globalIdx);
        _slabs[globalIdx >> _slabShift][globalIdx & _slabMask] = value;
        _count++;
        return globalIdx;
    }

    /// <summary>释放一个 slot。O(1)，随后尝试回收尾部空页（保留 1 个空 Slab 作防抖缓冲）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出当前容量范围。</exception>
    /// <exception cref="InvalidOperationException">对未占用的 slot 进行 Free（double-free）。</exception>
    public void Free(int index) {
        if ((uint)index >= (uint)_freeBitmap.Capacity) { throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_freeBitmap.Capacity})."); }

        if (_freeBitmap.Test(index)) { throw new InvalidOperationException($"Double free detected: slot {index} is already free."); }

        _freeBitmap.Set(index);

        // 清除值引用，协助 GC（T 可能是引用类型）
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            _slabs[index >> _slabShift][index & _slabMask] = default!;
        }

        _count--;

        // 尾部连续空页回收
        TryShrinkTrailingSlabs();
    }

    /// <summary>获取 slot 的值的可变引用。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出当前容量范围。</exception>
    /// <exception cref="InvalidOperationException">访问未占用的 slot。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetValueRef(int index) {
        ThrowIfNotOccupied(index);
        return ref _slabs[index >> _slabShift][index & _slabMask];
    }

    /// <summary>读取或更新已占用 slot 的值。O(1)。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出当前容量范围。</exception>
    /// <exception cref="InvalidOperationException">访问未占用的 slot。</exception>
    public T this[int index] {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get {
            ThrowIfNotOccupied(index);
            return _slabs[index >> _slabShift][index & _slabMask];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set {
            ThrowIfNotOccupied(index);
            _slabs[index >> _slabShift][index & _slabMask] = value;
        }
    }

    /// <summary>尝试读取 slot 的值。若 index 超出范围或 slot 未占用，返回 false。O(1)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(int index, out T value) {
        if ((uint)index < (uint)_freeBitmap.Capacity && !_freeBitmap.Test(index)) {
            value = _slabs[index >> _slabShift][index & _slabMask];
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>检查指定 index 的 slot 是否已分配（occupied）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 超出当前容量范围。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOccupied(int index) {
        if ((uint)index >= (uint)_freeBitmap.Capacity) { throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_freeBitmap.Capacity})."); }
        return !_freeBitmap.Test(index);
    }

    // ───────────────────── Validation ─────────────────────

    /// <summary>验证 index 在范围内且对应 slot 已占用，否则抛出异常。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfNotOccupied(int index) {
        if ((uint)index >= (uint)_freeBitmap.Capacity) { throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_freeBitmap.Capacity})."); }
        if (_freeBitmap.Test(index)) { throw new InvalidOperationException($"Slot {index} is not occupied (already freed or never allocated)."); }
    }

    // ───────────────────── Capacity management ─────────────────────

    /// <summary>增长一个新页，所有 slot 标记为 free，返回新页的 slab index。</summary>
    private int GrowOneSlab() {
        int slabIdx = _freeBitmap.SlabCount;

        if (slabIdx >= _slabs.Length) {
            Array.Resize(ref _slabs, _slabs.Length * 2);
        }

        _slabs[slabIdx] = new T[_slabSize];
        _freeBitmap.GrowSlabAllOne();

        return slabIdx;
    }

    /// <summary>
    /// 回收尾部连续的全空 Slab，但保留 1 个全空 Slab 作为防抖缓冲。
    /// 双阈值滞回策略：只有尾部 ≥2 个连续全空 Slab 才开始收缩，收缩后至少保留 1 个空 Slab。
    /// </summary>
    private void TryShrinkTrailingSlabs() {
        while (_freeBitmap.SlabCount >= 2
               && _freeBitmap.GetOneCount(_freeBitmap.SlabCount - 1) == _slabSize
               && _freeBitmap.GetOneCount(_freeBitmap.SlabCount - 2) == _slabSize) {
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
               && _freeBitmap.GetOneCount(_freeBitmap.SlabCount - 1) == _slabSize) {
            int last = _freeBitmap.SlabCount - 1;
            _slabs[last] = null!;
            _freeBitmap.ShrinkLastSlab();
        }
    }
}
