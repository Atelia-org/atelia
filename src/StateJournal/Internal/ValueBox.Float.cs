using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

partial struct ValueBox {
    internal readonly struct RoundedDoubleFace : ITypedFace<double> {
        // 编码：LZC=0 bit63=1 + 63 bit payload (sign + exp + mantissa[51..1] with RTO sticky)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ValueBox FromRoundedDoubleBits(ulong doubleBits) => new((doubleBits >> 1) | (doubleBits & 1) | LzcConstants.DoubleTag);
        /// <summary>将 double编码为 ValueBox。采用 round-to-odd舍入至 52-bit尾数（±1 ULP）。这是 double的推荐主路径。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueBox From(double value) => FromRoundedDoubleBits(BitConverter.DoubleToUInt64Bits(value));

        // ai:test `atelia/tests/StateJournal.Tests/Internal/ValueBoxExclusiveSetTests.cs`
        /// <summary>
        /// 独占更新：将 ValueBox 覆写为指定的 double 值（lossy round-to-odd 编码）。
        /// 相等性语义：NumericEquiv — <c>-0.0 ≠ +0.0</c>（保符号），所有 NaN 互等。
        /// </summary>
        public static bool UpdateOrInit(ref ValueBox old, double value) {
            if (old.NumericEquiv(value)) { return false; }
            FreeOldOwnedHeapIfNeeded(old);
            old = From(value);
            return true;
        }
        public static GetIssue Get(ValueBox box, out double value) => box.GetDouble(out value);
    }

    internal readonly struct ExactDoubleFace : ITypedFace<double> {
        /// <summary>将 double 无损编码为 ValueBox。当尾数 LSB=1 时会产生堆分配。</summary>
        public static ValueBox From(double value) {
            ulong doubleBits = BitConverter.DoubleToUInt64Bits(value);
            return (doubleBits & 1) == 0
                ? FromInlineableDoubleBits(doubleBits)
                : EncodeHeapSlot(HeapValueKind.FloatingPoint, ValuePools.OfBits64.Store(doubleBits));
        }

        /// <summary>
        /// 独占更新：将 ValueBox 覆写为指定的 double 值（无损编码）。
        /// 相等性语义：BitExact — IEEE 754 bit 完全一致才视为相等（区分 -0.0/+0.0 及不同 NaN payload）。
        /// 当尾数 LSB=1 时需要堆分配，此时尝试 inplace 复用旧 Bits64 Slot。
        /// </summary>
        public static bool UpdateOrInit(ref ValueBox old, double value) {
            ulong doubleBits = BitConverter.DoubleToUInt64Bits(value);
            if (old.TryDecodeDoubleRawBits(out ulong oldBits) && oldBits == doubleBits) { return false; }
            if ((doubleBits & 1) == 0) {
                FreeOldOwnedHeapIfNeeded(old);
                old = FromInlineableDoubleBits(doubleBits);
            }
            else {
                SlotHandle h = StoreOrReuseBits64(old, doubleBits);
                old = EncodeHeapSlot(HeapValueKind.FloatingPoint, h);
            }
            return true;
        }
        public static GetIssue Get(ValueBox box, out double value) => box.GetDouble(out value);
    }

    internal readonly struct SingleFace : ITypedFace<float> {
        /// <summary>将 float 编码为 ValueBox。float→double 拓宽后 LSB 恒为 0，始终 inline。</summary>
        public static ValueBox From(float value) => FromInlineableFloatingPoint(value);

        /// <summary>
        /// 独占更新为 float。始终 inline。
        /// 相等性语义：NumericEquiv — <c>-0.0f ≠ +0.0f</c>（保符号），所有 NaN 互等。
        /// </summary>
        public static bool UpdateOrInit(ref ValueBox old, float value) {
            if (old.NumericEquiv(value)) { return false; }
            FreeOldOwnedHeapIfNeeded(old);
            old = FromInlineableFloatingPoint(value);
            return true;
        }

        public static GetIssue Get(ValueBox box, out float value) {
            Debug.Assert(!box.IsUninitialized);
            BoxLzc lzc = box.GetLzc();
            if (lzc == BoxLzc.InlineDouble) { return NarrowToSingle(box.DecodeInlineDouble(), out value); }
            if (lzc == BoxLzc.InlineNonnegInt) {
                ulong u = box.DecodeInlineNonnegInt();
                value = (float)(long)u; // 因为inline值小于62bit所以可以先转signed再转floating point
                return IsExactAsSingle(u) ? GetIssue.None : GetIssue.PrecisionLost;
            }
            if (lzc == BoxLzc.InlineNegInt) {
                long n = box.DecodeInlineNegInt();
                value = (float)n;
                return IsExactAsSingle((ulong)-n) ? GetIssue.None : GetIssue.PrecisionLost;
            }
            if (lzc == BoxLzc.HeapSlot) {
                HeapValueKind kind = box.GetHeapKind();
                if (kind == HeapValueKind.FloatingPoint) { return NarrowToSingle(box.DecodeHeapDouble(), out value); }
                if (kind == HeapValueKind.NonnegativeInteger) {
                    ulong u = box.DecodeHeapNonnegInt();
                    value = (float)u; // 堆中正整数MSB可能为1，不能先转signed
                    return IsExactAsSingle(u) ? GetIssue.None : GetIssue.PrecisionLost;
                }
                if (kind == HeapValueKind.NegativeInteger) {
                    long n = box.DecodeHeapNegInt();
                    value = (float)n;
                    // 当 n = long.MinValue (-2⁶³) 时，-n 在 long 中溢出回到 long.MinValue，但 (ulong)long.MinValue = 2⁶³ 恰好是正确的 magnitude。
                    return IsExactAsSingle((ulong)-n) ? GetIssue.None : GetIssue.PrecisionLost;
                }
            }
            value = default;
            return GetIssue.TypeMismatch;
        }
    }

    internal readonly struct HalfFace : ITypedFace<Half> {
        /// <summary>将 Half 编码为 ValueBox。Half→double 拓宽后 LSB 恒为 0，始终 inline。</summary>
        public static ValueBox From(Half value) => FromInlineableFloatingPoint((double)value);

        /// <summary>
        /// 独占更新为 Half。始终 inline。
        /// 相等性语义：NumericEquiv — <c>-0 ≠ +0</c>（保符号），所有 NaN 互等。
        /// </summary>
        public static bool UpdateOrInit(ref ValueBox old, Half value) {
            if (old.NumericEquiv((double)value)) { return false; }
            FreeOldOwnedHeapIfNeeded(old);
            old = FromInlineableFloatingPoint((double)value);
            return true;
        }

        public static GetIssue Get(ValueBox box, out Half value) {
            Debug.Assert(!box.IsUninitialized);
            BoxLzc lzc = box.GetLzc();
            if (lzc == BoxLzc.InlineDouble) { return NarrowToHalf(box.DecodeInlineDouble(), out value); }
            if (lzc == BoxLzc.InlineNonnegInt) {
                ulong u = box.DecodeInlineNonnegInt();
                value = (Half)(long)u; // 因为inline值小于62bit所以可以先转signed再转floating point
                if (Half.IsInfinity(value)) { return GetIssue.OverflowedToInfinity; }
                return IsExactAsHalf(u) ? GetIssue.None : GetIssue.PrecisionLost;
            }
            if (lzc == BoxLzc.InlineNegInt) {
                long n = box.DecodeInlineNegInt();
                value = (Half)n;
                if (Half.IsInfinity(value)) { return GetIssue.OverflowedToInfinity; }
                return IsExactAsHalf((ulong)-n) ? GetIssue.None : GetIssue.PrecisionLost;
            }
            if (lzc == BoxLzc.HeapSlot) {
                HeapValueKind kind = box.GetHeapKind();
                if (kind == HeapValueKind.FloatingPoint) { return NarrowToHalf(box.DecodeHeapDouble(), out value); }
                if (kind == HeapValueKind.NonnegativeInteger) {
                    value = Half.PositiveInfinity; // 因为需要放进Heap的整数都超过FP16的表示范围，所以不用真的从堆里把数读出来。
                    return GetIssue.OverflowedToInfinity;
                }
                if (kind == HeapValueKind.NegativeInteger) {
                    value = Half.NegativeInfinity;
                    return GetIssue.OverflowedToInfinity;
                }
            }
            value = default;
            return GetIssue.TypeMismatch;
        }
    }

    #region Floating-point equality helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool NumericEquiv(double value) {
        ulong newBits = BitConverter.DoubleToUInt64Bits(value);
        return TryDecodeDoubleRawBits(out ulong oldBits)
            && (oldBits == newBits || (IsNaNBits(oldBits) && IsNaNBits(newBits)));
    }

    /// <summary>
    /// 尝试从 ValueBox 解码出浮点值的 IEEE 754 原始 bits。
    /// 仅检查 inline double 和 heap FloatingPoint 两条路径，不尝试整数转换。
    /// 用于 Update 中的相等性判断。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDecodeDoubleRawBits(out ulong doubleBits) {
        BoxLzc lzc = GetLzc();
        if (lzc == BoxLzc.InlineDouble) {
            doubleBits = GetBits() << 1;
            return true;
        }
        if (lzc == BoxLzc.HeapSlot && GetHeapKind() == HeapValueKind.FloatingPoint) {
            doubleBits = ValuePools.OfBits64[GetHeapHandle()];
            return true;
        }
        doubleBits = default;
        return false;
    }

    /// <summary>判断 IEEE 754 double raw bits 是否表示 NaN（exponent 全1 且 mantissa 非0）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // private static bool IsNaNBits(ulong doubleBits) => (doubleBits & 0x7FFF_FFFF_FFFF_FFFFUL) > 0x7FF0_0000_0000_0000UL;
    internal static bool IsNaNBits(ulong doubleBits) => (doubleBits << 1) > (0x7FF0_0000_0000_0000UL << 1);

    #endregion
    #region Floating-point encoding helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueBox FromInlineableDoubleBits(ulong doubleBits) {
        Debug.Assert(((doubleBits & 1) == 0) || double.IsNaN(BitConverter.UInt64BitsToDouble(doubleBits)));
        return new((doubleBits >> 1) | LzcConstants.DoubleTag);
    }
    /// <summary>float/Half → double 拓宽后低位必然有 29/42 个 0，LSB 必然为 0，无需 RTO sticky bit。对 NaN payload 放宽断言以容忍某些平台 (如 Wasm) 的非确定性行为。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueBox FromInlineableFloatingPoint(double value) => FromInlineableDoubleBits(BitConverter.DoubleToUInt64Bits(value));

    #endregion
    #region Floating-point decoding helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double DecodeInlineDouble() {
        Debug.Assert(GetLzc() == BoxLzc.InlineDouble);
        return BitConverter.UInt64BitsToDouble(GetBits() << 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double DecodeHeapDouble() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        Debug.Assert(GetHeapKind() == HeapValueKind.FloatingPoint);
        return BitConverter.UInt64BitsToDouble(ValuePools.OfBits64[GetHeapHandle()]);
    }

    /// <summary>
    /// 判断一个无符号整数（magnitude）是否可精确表示为指定 significand 位宽的浮点数。
    /// 无分支：LZC(v) + TZC(v) 恰等于 64 减去有效位数（significant bits）。
    /// 当有效位数 ≤ significandBits 时，整数可精确表示。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsExactAsDouble(ulong magnitude) => BitOperations.LeadingZeroCount(magnitude) + BitOperations.TrailingZeroCount(magnitude) >= 64 - 53;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsExactAsSingle(ulong magnitude) => BitOperations.LeadingZeroCount(magnitude) + BitOperations.TrailingZeroCount(magnitude) >= 64 - 24;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsExactAsHalf(ulong magnitude) => BitOperations.LeadingZeroCount(magnitude) + BitOperations.TrailingZeroCount(magnitude) >= 64 - 11;

    /// <summary>将 double 窄化为 float。处理溢出（→Infinity）和精度损失。</summary>
    private static GetIssue NarrowToSingle(double d, out float value) {
        value = (float)d; // 有意在发生溢出和精度损失时依然返回显式类型转换语义下的值
        if (float.IsInfinity(value) && !double.IsInfinity(d)) { return GetIssue.OverflowedToInfinity; }
        return ((double)value == d || double.IsNaN(d)) ? GetIssue.None : GetIssue.PrecisionLost;
    }

    /// <summary>将 double 窄化为 Half。处理溢出（→Infinity）和精度损失。</summary>
    private static GetIssue NarrowToHalf(double d, out Half value) {
        value = (Half)d; // 有意在发生溢出和精度损失时依然返回显式类型转换语义下的值
        if (Half.IsInfinity(value) && !double.IsInfinity(d)) { return GetIssue.OverflowedToInfinity; }
        return ((double)value == d || double.IsNaN(d)) ? GetIssue.None : GetIssue.PrecisionLost;
    }

    /// <summary>共享的 GetDouble 实现，供 RoundedDoubleFace 和 ExactDoubleFace 共用。</summary>
    internal GetIssue GetDouble(out double value) {
        Debug.Assert(!IsUninitialized);
        BoxLzc lzc = GetLzc();
        if (lzc == BoxLzc.InlineDouble) {
            value = DecodeInlineDouble();
            return GetIssue.None;
        }
        if (lzc == BoxLzc.InlineNonnegInt) {
            ulong u = DecodeInlineNonnegInt();
            value = (double)(long)u; // 因为inline值小于62bit所以可以先转signed再转floating point
            return IsExactAsDouble(u) ? GetIssue.None : GetIssue.PrecisionLost;
        }
        if (lzc == BoxLzc.InlineNegInt) {
            long n = DecodeInlineNegInt();
            value = (double)n;
            return IsExactAsDouble((ulong)-n) ? GetIssue.None : GetIssue.PrecisionLost;
        }
        if (lzc == BoxLzc.HeapSlot) {
            HeapValueKind kind = GetHeapKind();
            if (kind == HeapValueKind.FloatingPoint) {
                value = DecodeHeapDouble();
                return GetIssue.None;
            }
            if (kind == HeapValueKind.NonnegativeInteger) {
                ulong u = DecodeHeapNonnegInt();
                value = (double)u; // 堆中正整数MSB可能为1，不能先转signed
                return IsExactAsDouble(u) ? GetIssue.None : GetIssue.PrecisionLost;
            }
            if (kind == HeapValueKind.NegativeInteger) {
                long n = DecodeHeapNegInt();
                value = (double)n;
                return IsExactAsDouble((ulong)-n) ? GetIssue.None : GetIssue.PrecisionLost;
            }
        }
        value = default;
        return GetIssue.TypeMismatch;
    }

    #endregion
}
