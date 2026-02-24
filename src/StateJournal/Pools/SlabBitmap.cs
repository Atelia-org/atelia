using System.Numerics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

// ai:impl `src/StateJournal/Pools/SlabBitmap.Impl.cs`
// ai:impl `src/StateJournal/Pools/SlabBitmap.BinaryOp.cs`
// ai:impl `src/StateJournal/Pools/SlabBitmap.Enumerator.cs`
/// <summary>
/// 分页位图（Slab Bitmap）。以固定大小的 Slab（2^slabShift 位）为单位管理一长串 bit，
/// 支持高效的逐位操作、批量位运算（And / Or / Xor / AndNot / Not）和快速迭代。
/// </summary>
/// <remarks>
/// 内部采用二级结构：per-slab <c>ulong[]</c> 存储实际位数据，
/// per-slab <c>_oneCounts</c> 记录每页 `1` 数量，
/// 汇总 bitmap（_slabHasOne）记录哪些 Slab 含有 `1`（bit=1），
/// _slabAllOne bitmap 记录哪些 Slab 全满（bit=1），
/// 使 <see cref="FindFirstOne"/> / <see cref="FindLastZero"/> / <see cref="EnumerateZerosReverse"/> 能以 O(slabCount/64) 跳过不相关 Slab。
///
/// 通过 <see cref="GrowSlabAllZero"/> / <see cref="GrowSlabAllOne"/> / <see cref="ShrinkLastSlab"/> 动态扩缩，
/// 与 <see cref="SlotPool{T}"/> 的 Slab 生命周期对齐。
/// 批量位运算在主循环中顺带计算 PopCount，零额外开销。
/// </remarks>
internal sealed partial class SlabBitmap {

    public const int MinSlabShift = 6;
    public const int MaxSlabShift = 20;

    private readonly int _slabShift;
    private readonly int _slabSize;      // 2^_slabShift = bits per slab
    private readonly int _slabMask;      // _slabSize - 1
    private readonly int _wordsPerSlab;  // _slabSize / 64

    private ulong[][] _data;       // per-slab bit words
    private int[] _oneCounts;      // per-slab popcount of `1`s
    private ulong[] _slabHasOne;      // _slabHasOne[i] bit j = 1 → slab (i*64+j) has ≥1 `1`
    private ulong[] _slabAllOne;  // _slabAllOne[i] bit j = 1 → slab (i*64+j) is all `1`s

    private int _slabCount;
    private int _capacity;         // _slabCount * _slabSize

    /// <summary>每 slab 的 bit 数 = 2^<see cref="SlabShift"/>。</summary>
    public int SlabSize => _slabSize;

    /// <summary>log2(SlabSize)。</summary>
    public int SlabShift => _slabShift;

    /// <summary>当前已分配的 slab 数量。</summary>
    public int SlabCount => _slabCount;

    /// <summary>总 bit 容量 = SlabCount × SlabSize。</summary>
    public int Capacity => _capacity;

    /// <summary>每 slab 含有多少个 <c>ulong</c> 字。</summary>
    public int WordsPerSlab => _wordsPerSlab;

    /// <summary>创建一个空的 <see cref="SlabBitmap"/>。</summary>
    /// <param name="slabShift">SlabSize = 2^slabShift。</param>
    public SlabBitmap(int slabShift) {
        if (slabShift < MinSlabShift || slabShift > MaxSlabShift) {
            throw new ArgumentOutOfRangeException(nameof(slabShift), slabShift,
                $"slabShift must be in [{MinSlabShift}, {MaxSlabShift}]."
            );
        }

        _slabShift = slabShift;
        _slabSize = 1 << slabShift;
        _slabMask = _slabSize - 1;
        _wordsPerSlab = _slabSize >> 6;

        _data = new ulong[4][];
        _oneCounts = new int[4];
        _slabHasOne = new ulong[1]; // 覆盖 64 页
        _slabAllOne = new ulong[1];
        _slabCount = 0;
        _capacity = 0;
    }

    // ───────────────────── Slab lifecycle ─────────────────────

    /// <summary>增长一个新 slab。并且所有bit置0。</summary>
    public void GrowSlabAllZero() => GrowSlab(allOne: false);

    /// <summary>增长一个新 slab。并且所有bit置1。</summary>
    public void GrowSlabAllOne() => GrowSlab(allOne: true);

    /// <summary>移除最后一个 slab。</summary>
    /// <exception cref="InvalidOperationException">没有 slab 可移除。</exception>
    public partial void ShrinkLastSlab();

    // ───────────────────── Per-bit operations ─────────────────────

    /// <summary>测试 <paramref name="index"/> 处的 bit 是否为 1。</summary>
    public partial bool Test(int index);

    /// <summary>将 <paramref name="index"/> 处的 bit 置 1（幂等）。</summary>;
    public partial void Set(int index);

    /// <summary>将 <paramref name="index"/> 处的 bit 置 0（幂等）。</summary>;
    public partial void Clear(int index);

    // ───────────────────── Bulk set / clear ─────────────────────

    /// <summary>将所有已分配 slab 中的 bit 全部置 1。</summary>
    public partial void SetAll();

    /// <summary>将所有已分配 slab 中的 bit 全部置 0。</summary>
    public partial void ClearAll();

    /// <summary>this &amp;= other。超出 <paramref name="other"/> 范围的 slab 被清零。</summary>
    public void And(SlabBitmap other) => BulkBinary<AndOp>(other);

    /// <summary>this |= other。</summary>
    public void Or(SlabBitmap other) => BulkBinary<OrOp>(other);

    /// <summary>this ^= other。</summary>
    public void Xor(SlabBitmap other) => BulkBinary<XorOp>(other);

    /// <summary>this &amp;= ~other。超出 <paramref name="other"/> 范围的 slab 不变。</summary>
    public void AndNot(SlabBitmap other) => BulkBinary<AndNotOp>(other);

    /// <summary>this |= ~other。超出 <paramref name="other"/> 范围的 slab 被置为全 1。</summary>
    public void OrNot(SlabBitmap other) => BulkBinary<OrNotOp>(other);

    /// <summary>this = ~this（翻转所有 bit）。</summary>
    public partial void Not();

    /// <summary>this = other（精确复制内容，要求 slab 数量相同）。</summary>
    public partial void CopyFrom(SlabBitmap other);

    // ───────────────────── Find ─────────────────────

    /// <summary>
    /// 正序查找第一个 `1`（bit=1），返回其全局 index。找不到返回 -1。
    /// 利用 _slabHasOne bitmap 跳过全零 slab。
    /// </summary>
    public partial int FindFirstOne();

    /// <summary>
    /// 逆序查找最后一个 `0`（bit=0），返回其全局 index。找不到返回 -1。
    /// 利用 _slabAllOne bitmap 逆序跳过全满 slab。
    /// </summary>
    public partial int FindLastZero();

    // ───────────────────── Enumeration ─────────────────────

    /// <summary>
    /// 返回一个正序（从低 index 到高 index）遍历所有 `1` 的枚举器。
    /// 零分配（ref struct），可直接 foreach。利用 _slabHasOne 跳过全零 slab。
    /// </summary>
    public OnesForwardEnumerator EnumerateOnes() => new(this);

    /// <summary>
    /// 返回一个逆序（从高 index 到低 index）遍历所有 `0` 的枚举器。
    /// 零分配（ref struct），可直接 foreach。利用 _slabAllOne 跳过全满 slab。
    /// </summary>
    /// <remarks>
    /// 逆序迭代使尾部 slab 的 clear bit 更早被发现，
    /// 适用于需要从尾部开始收缩的场景。
    /// </remarks>
    public ZerosReverseEnumerator EnumerateZerosReverse() => new(this);

    // ───────────────────── Compaction ─────────────────────

    /// <summary>
    /// 返回一个 Two-Finger 压缩移动计划枚举器。不修改 bitmap。
    /// 每次 yield <c>(One, Zero)</c> 对：正序找 set bit(1, 低 index) 与逆序找 clear bit(0, 高 index)。
    /// 当两个游标相遇（One ≥ Zero）时迭代终止。
    /// </summary>
    /// <remarks>
    /// 迭代完成后 bitmap 的最终状态是确定的（前 N 位为 0，后面为 1），
    /// 调用者可据此直接重建或批量更新 bitmap，无需逐步变异。
    /// </remarks>
    public CompactionEnumerator EnumerateCompactionMoves() => new(this);

    // ───────────────────── Query ─────────────────────

    /// <summary>返回指定 slab 中 `1` 的数量。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOneCount(int slabIdx) => _oneCounts[slabIdx];

    /// <summary>返回所有 slab 中 `1` 的总数。</summary>
    public int TotalOneCount() {
        int total = 0;
        for (int s = 0; s < _slabCount; s++) { total += _oneCounts[s]; }
        return total;
    }

    // ───────────────────── Binary queries (non-mutating) ─────────────────────

    /// <summary>(this ∩ other) ≠ ∅。利用 _oneCounts 跳过全零 slab，找到即返回。</summary>
    public partial bool Intersects(SlabBitmap other);

    /// <summary>this ⊆ other。可短路。this 多余 slab 中若有 1 则返回 false。</summary>
    public partial bool IsSubsetOf(SlabBitmap other);

    /// <summary>(this ∩ other) = ∅。等价于 <c>!Intersects(other)</c>。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDisjointWith(SlabBitmap other) => !Intersects(other);

    /// <summary>|this ∩ other|（交集的基数）。利用 _oneCounts 跳过全零 slab。</summary>
    public partial int CountAnd(SlabBitmap other);

    // ───────────────────── OnesForwardEnumerator ─────────────────────

    /// <summary>
    /// 正序枚举 <see cref="SlabBitmap"/> 中所有 `1` 的 ref struct 枚举器。
    /// 内部通过 _slabHasOne bitmap 跳过全零 slab，零分配，可直接 foreach。
    /// </summary>
    public ref partial struct OnesForwardEnumerator {
        /// <summary>当前 `1` 的全局 index。</summary>
        public int Current => _current;

        /// <summary>支持 foreach。</summary>
        public OnesForwardEnumerator GetEnumerator() => this;

        /// <summary>推进到下一个 `1`。</summary>
        public partial bool MoveNext();
    }

    // ───────────────────── ZerosReverseEnumerator ─────────────────────

    /// <summary>
    /// 逆序枚举 <see cref="SlabBitmap"/> 中所有 `0` 的 ref struct 枚举器。
    /// 内部通过 _slabAllOne bitmap 跳过全满 slab，零分配，可直接 foreach。
    /// </summary>
    public ref partial struct ZerosReverseEnumerator {
        /// <summary>当前 `0` 的全局 index。</summary>
        public int Current => _current;

        /// <summary>支持 foreach。</summary>
        public ZerosReverseEnumerator GetEnumerator() => this;

        /// <summary>推进到下一个 `0`。</summary>
        public partial bool MoveNext();
    }

    // ───────────────────── CompactionEnumerator ─────────────────────

    /// <summary>
    /// Two-Finger 压缩枚举器。正序找 set bit(1) 与逆序找 clear bit(0) 交替推进，
    /// 输出 <c>(One, Zero)</c> 对直到两个游标相遇。不修改 bitmap，零分配。
    /// </summary>
    public ref partial struct CompactionEnumerator {
        /// <summary>
        /// 当前配对。<c>One</c> = set bit index (bit=1, 低位端)，<c>Zero</c> = clear bit index (bit=0, 高位端)。
        /// 始终满足 <c>One &lt; Zero</c>。
        /// </summary>
        public (int One, int Zero) Current => _current;

        /// <summary>支持 foreach。</summary>
        public CompactionEnumerator GetEnumerator() => this;

        /// <summary>推进到下一对移动。</summary>
        public partial bool MoveNext();
    }
}
