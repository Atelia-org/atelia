using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

// ai:impl `src/StateJournal/Pools/SlabBitmap.Impl.cs`
// ai:impl `src/StateJournal/Pools/SlabBitmap.BinaryOp.cs`
// ai:impl `src/StateJournal/Pools/SlabBitmap.Enumerator.cs`
/// <summary>
/// 三级分页位图（3-Level Slab Bitmap）。以固定大小的 Slab（4096 位 = 64 ulong）为单位管理一长串 bit，
/// 支持高效的逐位操作、批量位运算（And / Or / Xor / AndNot / Not）和快速迭代。
/// </summary>
/// <remarks>
/// 内部采用三级结构：
/// - L0: per-slab <c>ulong[64]</c> 存储实际位数据（与 Slab 生命周期对齐）。
/// - L1: per-slab <c>ulong</c>（_l1HasOne / _l1HasZero），每 bit 汇总 L0 中 1 个 word 的状态。
/// - L2: 全局 <c>ulong[]</c>（_l2HasOne / _l2HasZero），每 bit 汇总 1 个 Slab 的状态。
///
/// 三级结构使 <see cref="FindFirstOne"/> / <see cref="FindLastZero"/> 以 3 次 TZCNT/LZCNT 完成查找，
/// 与 Slab 大小无关。per-slab <c>_oneCounts</c> 记录每页 `1` 数量，用于快速短路判断。
///
/// L0 数组的生命周期与 Slab 对齐（grow/shrink 同步）；
/// L1/L2 数组只增长不收缩（grows-only），Shrink 时清零对应 bit，换取实现简单性和访存局部性。
///
/// 通过 <see cref="GrowSlabAllZero"/> / <see cref="GrowSlabAllOne"/> / <see cref="ShrinkLastSlab"/> 动态扩缩，
/// 与 <see cref="SlotPool{T}"/> 的 Slab 生命周期对齐。
/// 批量位运算在主循环中顺带计算 PopCount 并重建 L1，零额外开销。
/// </remarks>
internal sealed partial class SlabBitmap {

    /// <summary>log2(SlabSize) = 12。</summary>
    public const int SlabShift = 12;

    /// <summary>每 slab 的 bit 数 = 4096。</summary>
    public const int SlabSize = 1 << SlabShift;

    /// <summary>每 slab 含有的 <c>ulong</c> 字数 = 64。</summary>
    public const int WordsPerSlab = SlabSize >> 6;

    /// <summary>用于替代除法取余 = 4095。</summary>
    public const int SlabMask = SlabSize - 1;

    // ── L0: per-slab bit words (allocated/deallocated with slab lifecycle) ──
    private ulong[][] _data;
    private int[] _oneCounts;      // per-slab popcount of `1`s

    // ── L1: per-slab word-level summary (grows-only, never shrinks) ──
    private ulong[] _l1HasOne;     // bit j = 1 → _data[s][j] ≠ 0
    private ulong[] _l1HasZero;    // bit j = 1 → _data[s][j] ≠ ulong.MaxValue

    // ── L2: global slab-level summary (grows-only, never shrinks) ──
    private ulong[] _l2HasOne;     // bit (s&63) of [s>>6] = 1 → slab s has ≥1 `1`
    private ulong[] _l2HasZero;    // bit (s&63) of [s>>6] = 1 → slab s has ≥1 `0`

    private int _slabCount;
    private int _capacity;         // _slabCount * SlabSize

    /// <summary>当前已分配的 slab 数量。</summary>
    public int SlabCount => _slabCount;

    /// <summary>总 bit 容量 = SlabCount × SlabSize。</summary>
    public int Capacity => _capacity;

    /// <summary>创建一个空的 <see cref="SlabBitmap"/>。</summary>
    public SlabBitmap() {
        _data = new ulong[4][];
        _oneCounts = new int[4];
        _l1HasOne = new ulong[4];
        _l1HasZero = new ulong[4];
        _l2HasOne = new ulong[1]; // 覆盖 64 slabs
        _l2HasZero = new ulong[1];
        _slabCount = 0;
        _capacity = 0;
    }

    // ───────────────────── Slab lifecycle ─────────────────────

    /// <summary>增长一个新 slab。并且所有bit置0。</summary>
    public void GrowSlabAllZero() => GrowSlab(allOne: false);

    /// <summary>增长一个新 slab。并且所有bit置1。</summary>
    public void GrowSlabAllOne() => GrowSlab(allOne: true);

    /// <summary>移除最后一个 slab。L0 数组释放，L1/L2 仅清零不收缩。</summary>
    /// <exception cref="InvalidOperationException">没有 slab 可移除。</exception>
    public partial void ShrinkLastSlab();

    // ───────────────────── Per-bit operations ─────────────────────

    /// <summary>测试 <paramref name="index"/> 处的 bit 是否为 1。</summary>
    public partial bool Test(int index);

    /// <summary>将 <paramref name="index"/> 处的 bit 置 1（幂等）。含 L0→L1→L2 传播。</summary>
    public partial void Set(int index);

    /// <summary>将 <paramref name="index"/> 处的 bit 置 0（幂等）。含 L0→L1→L2 传播。</summary>
    public partial void Clear(int index);

    // ───────────────────── Bulk set / clear ─────────────────────

    /// <summary>将所有已分配 slab 中的 bit 全部置 1。</summary>
    public partial void SetAll();

    /// <summary>将所有已分配 slab 中的 bit 全部置 0。</summary>
    public partial void ClearAll();

    /// <summary>this &amp;= other。超出 <paramref name="other"/> 范围的 slab 被清零。</summary>
    public void And(SlabBitmap other) => BulkBinary<AndOp>(other);

    /// <summary>this |= other。超出 <c>this</c> 范围的 <paramref name="other"/> slab 被忽略（in-place 语义，不扩展 <c>this</c>）。</summary>
    public void Or(SlabBitmap other) => BulkBinary<OrOp>(other);

    /// <summary>this ^= other。超出 <c>this</c> 范围的 <paramref name="other"/> slab 被忽略（in-place 语义，不扩展 <c>this</c>）。</summary>
    public void Xor(SlabBitmap other) => BulkBinary<XorOp>(other);

    /// <summary>this &amp;= ~other。超出 <paramref name="other"/> 范围的 slab 不变。</summary>
    public void AndNot(SlabBitmap other) => BulkBinary<AndNotOp>(other);

    /// <summary>this |= ~other。超出 <paramref name="other"/> 范围的 slab 被置为全 1。超出 <c>this</c> 范围的 <paramref name="other"/> slab 被忽略（in-place 语义，不扩展 <c>this</c>）。</summary>
    public void OrNot(SlabBitmap other) => BulkBinary<OrNotOp>(other);

    /// <summary>this = ~this（翻转所有 bit）。</summary>
    public partial void Not();

    /// <summary>this = other（精确复制内容，要求 slab 数量相同）。</summary>
    public partial void CopyFrom(SlabBitmap other);

    // ───────────────────── Find ─────────────────────

    /// <summary>
    /// 正序查找第一个 `1`（bit=1），返回其全局 index。找不到返回 -1。
    /// 利用 L2 跳过全零 slab，L1 跳过全零 word，共 3 次 TZCNT。
    /// </summary>
    public partial int FindFirstOne();

    /// <summary>
    /// 逆序查找最后一个 `0`（bit=0），返回其全局 index。找不到返回 -1。
    /// 利用 L2 逆序跳过全满 slab，L1 逆序跳过全满 word，共 3 次 LZCNT。
    /// </summary>
    public partial int FindLastZero();

    // ───────────────────── Enumeration ─────────────────────

    /// <summary>
    /// 返回一个正序（从低 index 到高 index）遍历所有 `1` 的枚举器。
    /// 零分配（ref struct），可直接 foreach。利用 L2 跳过全零 slab，L1 跳过全零 word。
    /// </summary>
    public OnesForwardEnumerator EnumerateOnes() => new(this);

    /// <summary>
    /// 返回一个正序（从低 index 到高 index）遍历所有 `0` 的枚举器。
    /// 零分配（ref struct），可直接 foreach。利用 L2 跳过全满 slab，L1 跳过全满 word。
    /// </summary>
    public ZerosForwardEnumerator EnumerateZeros() => new(this);

    /// <summary>
    /// 返回一个逆序（从高 index 到低 index）遍历所有 `0` 的枚举器。
    /// 零分配（ref struct），可直接 foreach。利用 L2 跳过全满 slab，L1 跳过全满 word。
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
    /// 枚举器本身不修改 bitmap。如果调用者按移动计划执行全部交换，
    /// bitmap 的逻辑最终状态将是前 N 位为 0、后面为 1，
    /// 调用者可据此直接重建或批量更新 bitmap，无需逐步变异。
    /// </remarks>
    public CompactionEnumerator EnumerateCompactionMoves() => new(this);

    // ───────────────────── Query ─────────────────────

    /// <summary>返回指定 slab 中 `1` 的数量。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOneCount(int slabIdx) {
        Debug.Assert((uint)slabIdx < (uint)_slabCount, $"SlabIdx {slabIdx} out of range [0, {_slabCount}).");
        return _oneCounts[slabIdx];
    }

    /// <summary>返回所有 slab 中 `1` 的总数。</summary>
    public int TotalOneCount() {
        int total = 0;
        for (int s = 0; s < _slabCount; s++) { total += _oneCounts[s]; }
        return total;
    }

    // ───────────────────── Binary queries (non-mutating) ─────────────────────

    /// <summary>(this ∩ other) ≠ ∅。利用 L1/L2 跳过不相关 slab/word，找到即返回。</summary>
    public partial bool Intersects(SlabBitmap other);

    /// <summary>this ⊆ other。可短路。this 多余 slab 中若有 1 则返回 false。</summary>
    public partial bool IsSubsetOf(SlabBitmap other);

    /// <summary>(this ∩ other) = ∅。等价于 <c>!Intersects(other)</c>。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDisjointWith(SlabBitmap other) => !Intersects(other);

    /// <summary>|this ∩ other|（交集的基数）。利用 L1 跳过全零 word。</summary>
    public partial int CountAnd(SlabBitmap other);

    // ───────────────────── OnesForwardEnumerator ─────────────────────

    /// <summary>
    /// 正序枚举 <see cref="SlabBitmap"/> 中所有 `1` 的 ref struct 枚举器。
    /// 内部通过 L2 跳过全零 slab，L1 跳过全零 word，零分配，可直接 foreach。
    /// </summary>
    public ref partial struct OnesForwardEnumerator {
        /// <summary>当前 `1` 的全局 index。</summary>
        public int Current => _current;

        /// <summary>支持 foreach。</summary>
        public OnesForwardEnumerator GetEnumerator() => this;

        /// <summary>推进到下一个 `1`。</summary>
        public partial bool MoveNext();
    }

    // ───────────────────── ZerosForwardEnumerator ─────────────────────

    /// <summary>
    /// 正序枚举 <see cref="SlabBitmap"/> 中所有 `0` 的 ref struct 枚举器。
    /// 内部通过 L2 跳过全满 slab，L1 跳过全满 word，零分配，可直接 foreach。
    /// </summary>
    public ref partial struct ZerosForwardEnumerator {
        /// <summary>当前 `0` 的全局 index。</summary>
        public int Current => _current;

        /// <summary>支持 foreach。</summary>
        public ZerosForwardEnumerator GetEnumerator() => this;

        /// <summary>推进到下一个 `0`。</summary>
        public partial bool MoveNext();
    }

    // ───────────────────── ZerosReverseEnumerator ─────────────────────

    /// <summary>
    /// 逆序枚举 <see cref="SlabBitmap"/> 中所有 `0` 的 ref struct 枚举器。
    /// 内部通过 L2 跳过全满 slab，L1 跳过全满 word，零分配，可直接 foreach。
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
