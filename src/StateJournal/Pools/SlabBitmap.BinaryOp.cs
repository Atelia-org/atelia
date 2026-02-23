using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Pools;

// ai:facade `src/StateJournal/Pools/SlabBitmap.cs`
// ai:test `tests/StateJournal.Tests/SlabBitmapTests.BinaryOp.cs`
partial class SlabBitmap {
    // ───────────────────── Bulk bitwise (in-place) ─────────────────────

    /// <summary>
    /// 二元 bit 操作的静态接口。每个 struct 实现编码一种位运算和其 extra-slab 语义。
    /// JIT 对每个 struct 类型参数生成独立特化，<see cref="Apply"/> 被内联为单条指令，
    /// <see cref="ClearExtraSlabs"/> 作为常量折叠消除死分支。
    /// </summary>
    private interface IBinaryBitOp {
        static abstract ulong Apply(ulong a, ulong b);
        /// <summary>
        /// 当 <c>this</c> 有多余 slab（超出 <c>other</c> 范围）时是否清零。
        /// 隐含 <c>other</c> 多余位为 0：And → <c>x &amp; 0 = 0</c>（清零），
        /// Or/Xor/AndNot → 结果为 <c>x</c>（不变）。
        /// JIT 将此常量折叠，消除死分支。
        /// </summary>
        static virtual bool ClearExtraSlabs => false;
        /// <summary>
        /// 当 <c>this</c> 有多余 slab 时是否填满全 1。
        /// 隐含 <c>other</c> 多余位为 0：OrNot → <c>x | ~0 = 全1</c>。
        /// JIT 将此常量折叠，消除死分支。
        /// </summary>
        static virtual bool FillExtraSlabs => false;
    }

    private readonly struct AndOp : IBinaryBitOp {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Apply(ulong a, ulong b) => a & b;
        public static bool ClearExtraSlabs => true;
    }

    private readonly struct OrOp : IBinaryBitOp {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Apply(ulong a, ulong b) => a | b;
    }

    private readonly struct XorOp : IBinaryBitOp {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Apply(ulong a, ulong b) => a ^ b;
    }

    private readonly struct AndNotOp : IBinaryBitOp {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Apply(ulong a, ulong b) => a & ~b;
    }

    private readonly struct OrNotOp : IBinaryBitOp {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Apply(ulong a, ulong b) => a | ~b;
        public static bool FillExtraSlabs => true;
    }

    /// <summary>
    /// 泛型二元位运算循环。对 common slabs 逐 word 调用 <typeparamref name="TOp"/>.Apply，
    /// 同时计算 PopCount 更新 <c>_oneCounts</c> 和 summary。
    /// 多余 slab 的处理由 <see cref="IBinaryBitOp.ClearExtraSlabs"/> /
    /// <see cref="IBinaryBitOp.FillExtraSlabs"/> 常量决定。
    /// </summary>
    private void BulkBinary<TOp>(SlabBitmap other) where TOp : struct, IBinaryBitOp {
        ValidateCompatible(other);
        int common = Math.Min(_slabCount, other._slabCount);

        for (int s = 0; s < common; s++) {
            var tw = _data[s];
            var ow = other._data[s];
            int count = 0;
            for (int w = 0; w < _wordsPerSlab; w++) {
                ulong r = TOp.Apply(tw[w], ow[w]);
                tw[w] = r;
                count += BitOperations.PopCount(r);
            }
            _oneCounts[s] = count;
            UpdateSlabHasOne(s, count);
            UpdateSlabAllOne(s, count);
        }

        if (TOp.ClearExtraSlabs) {
            for (int s = common; s < _slabCount; s++) {
                _data[s].AsSpan().Clear();
                _oneCounts[s] = 0;
                ClearSlabHasOne(s);
                ClearSlabAllOne(s);
            }
        }

        if (TOp.FillExtraSlabs) {
            for (int s = common; s < _slabCount; s++) {
                _data[s].AsSpan().Fill(ulong.MaxValue);
                _oneCounts[s] = _slabSize;
                SetSlabHasOne(s);
                SetSlabAllOne(s);
            }
        }
    }

    // ───────────────────── Binary queries (non-mutating) ─────────────────────

    public partial bool Intersects(SlabBitmap other) {
        ValidateCompatible(other);
        int common = Math.Min(_slabCount, other._slabCount);
        int summaryLen = (common + 63) >> 6;
        for (int i = 0; i < summaryLen; i++) {
            ulong mask = _slabHasOne[i] & other._slabHasOne[i];
            while (mask != 0) {
                int bit = BitOperations.TrailingZeroCount(mask);
                int s = (i << 6) | bit;
                if (s >= common) break;
                mask &= mask - 1;
                var tw = _data[s];
                var ow = other._data[s];
                for (int w = 0; w < _wordsPerSlab; w++) {
                    if ((tw[w] & ow[w]) != 0) return true;
                }
            }
        }
        return false;
    }

    public partial bool IsSubsetOf(SlabBitmap other) {
        ValidateCompatible(other);
        int common = Math.Min(_slabCount, other._slabCount);
        int summaryLen = (common + 63) >> 6;
        // common slabs: 只检查 this 有 1 且 other 不全满的 slab
        for (int i = 0; i < summaryLen; i++) {
            ulong mask = _slabHasOne[i] & ~other._slabAllOne[i];
            while (mask != 0) {
                int bit = BitOperations.TrailingZeroCount(mask);
                int s = (i << 6) | bit;
                if (s >= common) break;
                mask &= mask - 1;
                var tw = _data[s];
                var ow = other._data[s];
                for (int w = 0; w < _wordsPerSlab; w++) {
                    if ((tw[w] & ~ow[w]) != 0) return false;
                }
            }
        }
        // this 多余 slab 中若有任何 1，则不是子集（other 隐含为 0）
        int extraSummaryStart = common >> 6;
        int extraSummaryEnd = (_slabCount + 63) >> 6;
        for (int i = extraSummaryStart; i < extraSummaryEnd; i++) {
            ulong mask = _slabHasOne[i];
            // 排除 common 范围内的 bit（它们已在上面检查过）
            if (i == extraSummaryStart) {
                int skipBits = common & 63;
                if (skipBits > 0) mask &= ulong.MaxValue << skipBits;
            }
            // 排除超出 _slabCount 的 bit
            while (mask != 0) {
                int bit = BitOperations.TrailingZeroCount(mask);
                int s = (i << 6) | bit;
                if (s >= _slabCount) break;
                return false; // this 多余 slab 有 1 → 不是子集
            }
        }
        return true;
    }

    public partial int CountAnd(SlabBitmap other) {
        ValidateCompatible(other);
        int common = Math.Min(_slabCount, other._slabCount);
        int summaryLen = (common + 63) >> 6;
        int total = 0;
        for (int i = 0; i < summaryLen; i++) {
            ulong mask = _slabHasOne[i] & other._slabHasOne[i];
            while (mask != 0) {
                int bit = BitOperations.TrailingZeroCount(mask);
                int s = (i << 6) | bit;
                if (s >= common) break;
                mask &= mask - 1;
                var tw = _data[s];
                var ow = other._data[s];
                for (int w = 0; w < _wordsPerSlab; w++) {
                    total += BitOperations.PopCount(tw[w] & ow[w]);
                }
            }
        }
        return total;
    }

    // ───────────────────── Copy ─────────────────────

    public partial void CopyFrom(SlabBitmap other) {
        ValidateCompatible(other);
        if (_slabCount != other._slabCount) {
            throw new ArgumentException(
                $"SlabBitmaps must have the same slab count ({_slabCount} vs {other._slabCount}).",
                nameof(other)
            );
        }
        for (int s = 0; s < _slabCount; s++) {
            other._data[s].AsSpan().CopyTo(_data[s]);
            _oneCounts[s] = other._oneCounts[s];
        }
        int summaryLen = (_slabCount + 63) >> 6;
        if (summaryLen > 0) {
            other._slabHasOne.AsSpan(0, summaryLen).CopyTo(_slabHasOne);
            other._slabAllOne.AsSpan(0, summaryLen).CopyTo(_slabAllOne);
        }
    }

    // ───────────────────── Validation ─────────────────────

    private void ValidateCompatible(SlabBitmap other) {
        if (_slabShift != other._slabShift) {
            throw new ArgumentException(
                $"SlabBitmaps must have the same slabShift ({_slabShift} vs {other._slabShift}).",
                nameof(other)
            );
        }
    }
}
