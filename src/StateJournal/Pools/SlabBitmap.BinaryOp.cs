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
        /// </summary>
        static virtual bool ClearExtraSlabs => false;
        /// <summary>
        /// 当 <c>this</c> 有多余 slab 时是否填满全 1。
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
    /// 泛型二元位运算循环。对 common slabs 逐 word 调用 Apply，
    /// 同时计算 PopCount 并重建 L1。L2 通过 oneCount 更新。
    /// </summary>
    private void BulkBinary<TOp>(SlabBitmap other) where TOp : struct, IBinaryBitOp {
        int common = Math.Min(_slabCount, other._slabCount);

        for (int s = 0; s < common; s++) {
            var tw = _data[s];
            var ow = other._data[s];
            int count = 0;
            ulong hasOneAcc = 0;
            ulong hasZeroAcc = 0;
            for (int w = 0; w < WordsPerSlab; w++) {
                ulong r = TOp.Apply(tw[w], ow[w]);
                tw[w] = r;
                count += BitOperations.PopCount(r);
                if (r != 0) { hasOneAcc |= 1UL << w; }
                if (r != ulong.MaxValue) { hasZeroAcc |= 1UL << w; }
            }
            _oneCounts[s] = count;
            _l1HasOne[s] = hasOneAcc;
            _l1HasZero[s] = hasZeroAcc;
            UpdateL2HasOne(s, count);
            UpdateL2HasZero(s, count);
        }

        if (TOp.ClearExtraSlabs) {
            for (int s = common; s < _slabCount; s++) {
                _data[s].AsSpan().Clear();
                _oneCounts[s] = 0;
                _l1HasOne[s] = 0;
                _l1HasZero[s] = ulong.MaxValue;
                ClearL2HasOne(s);
                SetL2HasZero(s);
            }
        }

        if (TOp.FillExtraSlabs) {
            for (int s = common; s < _slabCount; s++) {
                _data[s].AsSpan().Fill(ulong.MaxValue);
                _oneCounts[s] = SlabSize;
                _l1HasOne[s] = ulong.MaxValue;
                _l1HasZero[s] = 0;
                SetL2HasOne(s);
                ClearL2HasZero(s);
            }
        }
    }

    // ───────────────────── Binary queries (non-mutating) ─────────────────────

    public partial bool Intersects(SlabBitmap other) {
        int common = Math.Min(_slabCount, other._slabCount);
        int summaryLen = (common + 63) >> 6;
        for (int i = 0; i < summaryLen; i++) {
            ulong slabMask = _l2HasOne[i] & other._l2HasOne[i];
            while (slabMask != 0) {
                int bit = BitOperations.TrailingZeroCount(slabMask);
                int s = (i << 6) | bit;
                if (s >= common) { break; }
                slabMask &= slabMask - 1;
                // Use L1 to skip zero words
                ulong wordMask = _l1HasOne[s] & other._l1HasOne[s];
                while (wordMask != 0) {
                    int w = BitOperations.TrailingZeroCount(wordMask);
                    wordMask &= wordMask - 1;
                    if ((_data[s][w] & other._data[s][w]) != 0) { return true; }
                }
            }
        }
        return false;
    }

    public partial bool IsSubsetOf(SlabBitmap other) {
        int common = Math.Min(_slabCount, other._slabCount);
        int summaryLen = (common + 63) >> 6;
        // common slabs: check words where this has ones and other is not all-one
        for (int i = 0; i < summaryLen; i++) {
            ulong slabMask = _l2HasOne[i] & other._l2HasZero[i];
            while (slabMask != 0) {
                int bit = BitOperations.TrailingZeroCount(slabMask);
                int s = (i << 6) | bit;
                if (s >= common) { break; }
                slabMask &= slabMask - 1;
                // Use L1: check words where this has ones and other is not all-one
                ulong wordMask = _l1HasOne[s] & other._l1HasZero[s];
                while (wordMask != 0) {
                    int w = BitOperations.TrailingZeroCount(wordMask);
                    wordMask &= wordMask - 1;
                    if ((_data[s][w] & ~other._data[s][w]) != 0) { return false; }
                }
            }
        }
        // this 多余 slab 中若有任何 1，则不是子集（other 隐含为 0）
        int extraL2Start = common >> 6;
        int extraL2End = (_slabCount + 63) >> 6;
        for (int i = extraL2Start; i < extraL2End; i++) {
            ulong mask = _l2HasOne[i];
            if (i == extraL2Start) {
                int skipBits = common & 63;
                if (skipBits > 0) { mask &= ulong.MaxValue << skipBits; }
            }
            if (mask != 0) {
                int bit = BitOperations.TrailingZeroCount(mask);
                int s = (i << 6) | bit;
                if (s < _slabCount) { return false; } // this 多余 slab 有 1 → 不是子集
                break; // 更高的 L2 word 只会映射到更大的 slab 索引，全部 >= _slabCount
            }
        }
        return true;
    }

    public partial int CountAnd(SlabBitmap other) {
        int common = Math.Min(_slabCount, other._slabCount);
        int summaryLen = (common + 63) >> 6;
        int total = 0;
        for (int i = 0; i < summaryLen; i++) {
            ulong slabMask = _l2HasOne[i] & other._l2HasOne[i];
            while (slabMask != 0) {
                int bit = BitOperations.TrailingZeroCount(slabMask);
                int s = (i << 6) | bit;
                if (s >= common) { break; }
                slabMask &= slabMask - 1;
                // Use L1 to skip zero words
                ulong wordMask = _l1HasOne[s] & other._l1HasOne[s];
                while (wordMask != 0) {
                    int w = BitOperations.TrailingZeroCount(wordMask);
                    wordMask &= wordMask - 1;
                    total += BitOperations.PopCount(_data[s][w] & other._data[s][w]);
                }
            }
        }
        return total;
    }

    // ───────────────────── Copy ─────────────────────

    public partial void CopyFrom(SlabBitmap other) {
        if (_slabCount != other._slabCount) {
            throw new ArgumentException(
                $"SlabBitmaps must have the same slab count ({_slabCount} vs {other._slabCount}).",
                nameof(other)
            );
        }
        for (int s = 0; s < _slabCount; s++) {
            other._data[s].AsSpan().CopyTo(_data[s]);
            _oneCounts[s] = other._oneCounts[s];
            _l1HasOne[s] = other._l1HasOne[s];
            _l1HasZero[s] = other._l1HasZero[s];
        }
        int summaryLen = (_slabCount + 63) >> 6;
        if (summaryLen > 0) {
            other._l2HasOne.AsSpan(0, summaryLen).CopyTo(_l2HasOne);
            other._l2HasZero.AsSpan(0, summaryLen).CopyTo(_l2HasZero);
        }
    }
}
