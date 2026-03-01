
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial struct ValueBox {
    #region From floating point
    /// <summary>将 double编码为 ValueBox。采用 round-to-odd舍入至 52-bit尾数（±1 ULP）。这是 double的推荐主路径。</summary>
    public static ValueBox FromRoundedDouble(double value) {
        ulong doubleBits = BitConverter.DoubleToUInt64Bits(value);
        // 编码：LZC=0 bit63=1 + 63 bit payload (sign + exp + mantissa[51..1] with RTO sticky)
        return new((doubleBits >> 1) | (doubleBits & 1) | LzcConstants.DoubleTag);
    }

    /// <summary>将 double 无损编码为 ValueBox。当尾数 LSB=1 时会产生堆分配。</summary>
    internal static ValueBox FromExactDouble(double value) {
        ulong doubleBits = BitConverter.DoubleToUInt64Bits(value);
        return (doubleBits & 1) == 0
            ? new((doubleBits >> 1) | LzcConstants.DoubleTag)
            : EncodeHeapSlot(DurableValueKind.FloatingPoint, ValuePools.Bits64.Store(doubleBits));
    }

    /// <summary>float/Half → double 拓宽后低位必然有 29/42 个 0，LSB 必然为 0，无需 RTO sticky bit。对 NaN payload 放宽断言以容忍某些平台 (如 Wasm) 的非确定性行为。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueBox FromInlineableFloatingPoint(double value) {
        ulong doubleBits = BitConverter.DoubleToUInt64Bits(value);
        Debug.Assert(((doubleBits & 1) == 0) || double.IsNaN(value));
        return new((doubleBits >> 1) | LzcConstants.DoubleTag);
    }

    public static ValueBox FromSingle(float value) => FromInlineableFloatingPoint(value);
    public static ValueBox FromHalf(Half value) => FromInlineableFloatingPoint((double)value);
    #endregion
    #region Get as floating point
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double DecodeInlineDouble() {
        Debug.Assert(GetLZC() == LzcCode.InlineDouble);
        return BitConverter.UInt64BitsToDouble(_bits << 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double DecodeHeapDouble() {
        Debug.Assert(GetLZC() == LzcCode.HeapSlot);
        Debug.Assert(GetHeapKind() == DurableValueKind.FloatingPoint);
        return BitConverter.UInt64BitsToDouble(ValuePools.Bits64[GetHeapHandle()]);
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
        if (float.IsInfinity(value) && !double.IsInfinity(d)) {
            return GetIssue.OverflowedToInfinity;
        }
        return ((double)value == d || double.IsNaN(d)) ? GetIssue.None : GetIssue.PrecisionLost;
    }

    /// <summary>将 double 窄化为 Half。处理溢出（→Infinity）和精度损失。</summary>
    private static GetIssue NarrowToHalf(double d, out Half value) {
        value = (Half)d; // 有意在发生溢出和精度损失时依然返回显式类型转换语义下的值
        if (Half.IsInfinity(value) && !double.IsInfinity(d)) {
            return GetIssue.OverflowedToInfinity;
        }
        return ((double)value == d || double.IsNaN(d)) ? GetIssue.None : GetIssue.PrecisionLost;
    }

    public GetIssue Get(out double value) {
        LzcCode lzc = GetLZC();
        if (lzc == LzcCode.InlineDouble) {
            value = DecodeInlineDouble();
            return GetIssue.None;
        }
        if (lzc == LzcCode.InlineNonnegInt) {
            ulong u = DecodeInlineNonnegInt();
            value = (double)(long)u; // 因为inline值小于62bit所以可以先转signed再转floating point
            return IsExactAsDouble(u) ? GetIssue.None : GetIssue.PrecisionLost;
        }
        if (lzc == LzcCode.InlineNegInt) {
            long n = DecodeInlineNegInt();
            value = (double)n;
            return IsExactAsDouble((ulong)-n) ? GetIssue.None : GetIssue.PrecisionLost;
        }
        if (lzc == LzcCode.HeapSlot) {
            DurableValueKind kind = GetHeapKind();
            if (kind == DurableValueKind.FloatingPoint) {
                value = DecodeHeapDouble();
                return GetIssue.None;
            }
            if (kind == DurableValueKind.NonnegativeInteger) {
                ulong u = DecodeHeapNonnegInt();
                value = (double)u; // 堆中正整数MSB可能为1，不能先转signed
                return IsExactAsDouble(u) ? GetIssue.None : GetIssue.PrecisionLost;
            }
            if (kind == DurableValueKind.NegativeInteger) {
                long n = DecodeHeapNegInt();
                value = (double)n;
                return IsExactAsDouble((ulong)-n) ? GetIssue.None : GetIssue.PrecisionLost;
            }
        }
        value = default;
        return GetIssue.TypeMismatch;
    }

    public GetIssue Get(out float value) {
        LzcCode lzc = GetLZC();
        if (lzc == LzcCode.InlineDouble) {
            return NarrowToSingle(DecodeInlineDouble(), out value);
        }
        if (lzc == LzcCode.InlineNonnegInt) {
            ulong u = DecodeInlineNonnegInt();
            value = (float)(long)u; // 因为inline值小于62bit所以可以先转signed再转floating point
            return IsExactAsSingle(u) ? GetIssue.None : GetIssue.PrecisionLost;
        }
        if (lzc == LzcCode.InlineNegInt) {
            long n = DecodeInlineNegInt();
            value = (float)n;
            return IsExactAsSingle((ulong)-n) ? GetIssue.None : GetIssue.PrecisionLost;
        }
        if (lzc == LzcCode.HeapSlot) {
            DurableValueKind kind = GetHeapKind();
            if (kind == DurableValueKind.FloatingPoint) {
                return NarrowToSingle(DecodeHeapDouble(), out value);
            }
            if (kind == DurableValueKind.NonnegativeInteger) {
                ulong u = DecodeHeapNonnegInt();
                value = (float)u; // 堆中正整数MSB可能为1，不能先转signed
                return IsExactAsSingle(u) ? GetIssue.None : GetIssue.PrecisionLost;
            }
            if (kind == DurableValueKind.NegativeInteger) {
                long n = DecodeHeapNegInt();
                value = (float)n;
                // 当 n = long.MinValue (-2⁶³) 时，-n 在 long 中溢出回到 long.MinValue，但 (ulong)long.MinValue = 2⁶³ 恰好是正确的 magnitude。
                return IsExactAsSingle((ulong)-n) ? GetIssue.None : GetIssue.PrecisionLost;
            }
        }
        value = default;
        return GetIssue.TypeMismatch;
    }

    public GetIssue Get(out Half value) {
        LzcCode lzc = GetLZC();
        if (lzc == LzcCode.InlineDouble) {
            return NarrowToHalf(DecodeInlineDouble(), out value);
        }
        if (lzc == LzcCode.InlineNonnegInt) {
            ulong u = DecodeInlineNonnegInt();
            value = (Half)(long)u; // 因为inline值小于62bit所以可以先转signed再转floating point
            if (Half.IsInfinity(value)) { return GetIssue.OverflowedToInfinity; }
            return IsExactAsHalf(u) ? GetIssue.None : GetIssue.PrecisionLost;
        }
        if (lzc == LzcCode.InlineNegInt) {
            long n = DecodeInlineNegInt();
            value = (Half)n;
            if (Half.IsInfinity(value)) { return GetIssue.OverflowedToInfinity; }
            return IsExactAsHalf((ulong)-n) ? GetIssue.None : GetIssue.PrecisionLost;
        }
        if (lzc == LzcCode.HeapSlot) {
            DurableValueKind kind = GetHeapKind();
            if (kind == DurableValueKind.FloatingPoint) {
                return NarrowToHalf(DecodeHeapDouble(), out value);
            }
            if (kind == DurableValueKind.NonnegativeInteger) {
                value = Half.PositiveInfinity; // 因为需要放进Heap的整数都超过FP16的表示范围，所以不用真的从堆里把数读出来。
                return GetIssue.OverflowedToInfinity;
            }
            if (kind == DurableValueKind.NegativeInteger) {
                value = Half.NegativeInfinity;
                return GetIssue.OverflowedToInfinity;
            }
        }
        value = default;
        return GetIssue.TypeMismatch;
    }
    #endregion
    #region Exclusive set
    // ai:test `atelia/tests/StateJournal.Tests/Internal/ValueBoxExclusiveSetTests.cs`

    /// <summary>
    /// 独占更新：将 ValueBox 覆写为指定的 double 值（lossy round-to-odd 编码）。
    /// <see cref="FromRoundedDouble"/> 始终 inline，因此只需清理旧 slot。
    /// </summary>
    internal static void ExclusiveSetRoundedDouble(ref ValueBox box, double value) {
        FreeOldBits64IfNeeded(box);
        ulong doubleBits = BitConverter.DoubleToUInt64Bits(value);
        box = new((doubleBits >> 1) | (doubleBits & 1) | LzcConstants.DoubleTag);
    }

    /// <summary>
    /// 独占更新：将 ValueBox 覆写为指定的 double 值（无损编码）。
    /// 当尾数 LSB=1 时需要堆分配，此时尝试 inplace 复用旧 Bits64 Slot。
    /// </summary>
    internal static void ExclusiveSetExactDouble(ref ValueBox box, double value) {
        ulong doubleBits = BitConverter.DoubleToUInt64Bits(value);
        if ((doubleBits & 1) == 0) {
            FreeOldBits64IfNeeded(box);
            box = new((doubleBits >> 1) | LzcConstants.DoubleTag);
        } else {
            SlotHandle h = StoreOrReuseBits64(box, doubleBits);
            box = EncodeHeapSlot(DurableValueKind.FloatingPoint, h);
        }
    }

    /// <summary>独占更新为 float。始终 inline。</summary>
    internal static void ExclusiveSetSingle(ref ValueBox box, float value) {
        FreeOldBits64IfNeeded(box);
        box = FromInlineableFloatingPoint(value);
    }

    /// <summary>独占更新为 Half。始终 inline。</summary>
    internal static void ExclusiveSetHalf(ref ValueBox box, Half value) {
        FreeOldBits64IfNeeded(box);
        box = FromInlineableFloatingPoint((double)value);
    }

    #endregion
}
