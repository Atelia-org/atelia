using System.Diagnostics;
using System.Runtime.CompilerServices;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial struct ValueBox {
    #region Encode Integer

    /// <summary>将 long 编码为 ValueBox。值域 [-2^61, 2^62-1] 内联；超出范围回退到堆分配。</summary>
    public static ValueBox FromInt64(long value) {
        ulong u = unchecked((ulong)value);
        if (value >= 0) {
            if (u < LzcConstants.NonnegIntInlineCap) { return new(u | LzcConstants.NonnegIntTag); }
            return EncodeHeapSlot(DurableValueKind.NonnegativeInteger, ValuePools.Bits64.Store(u));
        }
        // value < 0
        if (value >= LzcConstants.NegIntInlineMin) { return new(u & LzcConstants.NegIntPayloadMask); }
        return EncodeHeapSlot(DurableValueKind.NegativeInteger, ValuePools.Bits64.Store(u));
    }

    /// <summary>将 ulong 编码为 ValueBox。值域 [0, 2^62-1] 内联；超出范围回退到堆分配。</summary>
    public static ValueBox FromUInt64(ulong value) {
        if (value < LzcConstants.NonnegIntInlineCap) { return new(value | LzcConstants.NonnegIntTag); }
        return EncodeHeapSlot(DurableValueKind.NonnegativeInteger, ValuePools.Bits64.Store(value));
    }

    /// <summary>始终 inline。int 值域 [-2^31, 2^31-1] 完全在 inline 容量以内。</summary>
    public static ValueBox FromInt32(int value) => FromInlineableSignedInteger(value);

    /// <summary>始终 inline。uint 值域 [0, 2^32-1] 完全在 inline 容量以内。</summary>
    public static ValueBox FromUInt32(uint value) => FromInlineableUnsignedInteger(value);

    /// <summary>始终 inline。</summary>
    public static ValueBox FromInt16(short value) => FromInlineableSignedInteger(value);

    /// <summary>始终 inline。</summary>
    public static ValueBox FromUInt16(ushort value) => FromInlineableUnsignedInteger(value);

    /// <summary>始终 inline。</summary>
    public static ValueBox FromSByte(sbyte value) => FromInlineableSignedInteger(value);

    /// <summary>始终 inline。</summary>
    public static ValueBox FromByte(byte value) => FromInlineableUnsignedInteger(value);

    /// <summary>小范围有符号整数的快速内联编码路径。不触发堆分配回退的安全边界为：[-2^61, 2^62-1]。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueBox FromInlineableSignedInteger(long value) {
        Debug.Assert(value >= LzcConstants.NegIntInlineMin && value < (long)LzcConstants.NonnegIntInlineCap);
        ulong u = unchecked((ulong)value);
        return new(value >= 0 ? (u | LzcConstants.NonnegIntTag) : (u & LzcConstants.NegIntPayloadMask));
    }

    /// <summary>小范围无符号整数的快速内联编码路径。不触发堆分配回退的安全边界为：[0, 2^62-1]。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueBox FromInlineableUnsignedInteger(ulong value) {
        Debug.Assert(value < LzcConstants.NonnegIntInlineCap);
        return new(value | LzcConstants.NonnegIntTag);
    }

    #endregion
    #region Decode Integer

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong DecodeInlineNonnegInt() {
        Debug.Assert(GetLZC() == LzcCode.InlineNonnegInt);
        return _bits & LzcConstants.NonnegIntPayloadMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long DecodeInlineNegInt() {
        Debug.Assert(GetLZC() == LzcCode.InlineNegInt);
        return (long)(_bits | LzcConstants.NegIntSignRestore);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong DecodeHeapNonnegInt() {
        Debug.Assert(GetLZC() == LzcCode.HeapSlot);
        Debug.Assert(GetHeapKind() == DurableValueKind.NonnegativeInteger);
        return ValuePools.Bits64[GetHeapHandle()];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long DecodeHeapNegInt() {
        Debug.Assert(GetLZC() == LzcCode.HeapSlot);
        Debug.Assert(GetHeapKind() == DurableValueKind.NegativeInteger);
        return unchecked((long)ValuePools.Bits64[GetHeapHandle()]);
    }

    public GetStatus Get(out long value) {
        LzcCode lzc = GetLZC();
        if (lzc == LzcCode.InlineNonnegInt) {
            value = (long)DecodeInlineNonnegInt(); // inline范围是62bit所以不会溢出
            return GetStatus.Success;
        }
        if (lzc == LzcCode.InlineNegInt) {
            value = DecodeInlineNegInt();
            return GetStatus.Success;
        }
        if (lzc == LzcCode.HeapSlot) {
            DurableValueKind kind = GetHeapKind();
            if (kind == DurableValueKind.NonnegativeInteger) {
                ulong u = DecodeHeapNonnegInt();
                if (u > (ulong)long.MaxValue) {
                    value = long.MaxValue;
                    return GetStatus.Saturated;
                }
                value = (long)u;
                return GetStatus.Success;
            }
            if (kind == DurableValueKind.NegativeInteger) {
                value = DecodeHeapNegInt();
                return GetStatus.Success;
            }
        }
        value = default;
        return GetStatus.TypeMismatch;
    }

    public GetStatus Get(out ulong value) {
        LzcCode lzc = GetLZC();
        if (lzc == LzcCode.InlineNonnegInt) {
            value = _bits & LzcConstants.NonnegIntPayloadMask;
            return GetStatus.Success;
        }
        if (lzc == LzcCode.HeapSlot) {
            DurableValueKind kind = GetHeapKind();
            if (kind == DurableValueKind.NonnegativeInteger) {
                value = DecodeHeapNonnegInt();
                return GetStatus.Success;
            }
            if (kind == DurableValueKind.NegativeInteger) {
                value = ulong.MinValue;
                return GetStatus.Saturated;
            }
        }

        if (lzc == LzcCode.InlineNegInt) {
            value = ulong.MinValue;
            return GetStatus.Saturated;
        }

        value = default;
        return GetStatus.TypeMismatch;
    }

    public GetStatus Get(out int value) {
        GetStatus status = Get(out long l);
        if (status != GetStatus.Success) {
            value = default;
            return status;
        }
        if (l < int.MinValue || l > int.MaxValue) {
            value = l < int.MinValue ? int.MinValue : int.MaxValue;
            return GetStatus.Saturated;
        }
        value = (int)l;
        return GetStatus.Success;
    }

    public GetStatus Get(out uint value) {
        GetStatus status = Get(out ulong u);
        if (status != GetStatus.Success) {
            value = default;
            return status;
        }
        if (u > uint.MaxValue) {
            value = uint.MaxValue;
            return GetStatus.Saturated;
        }
        value = (uint)u;
        return GetStatus.Success;
    }

    public GetStatus Get(out short value) {
        GetStatus status = Get(out long l);
        if (status != GetStatus.Success) {
            value = default;
            return status;
        }
        if (l < short.MinValue || l > short.MaxValue) {
            value = l < short.MinValue ? short.MinValue : short.MaxValue;
            return GetStatus.Saturated;
        }
        value = (short)l;
        return GetStatus.Success;
    }

    public GetStatus Get(out ushort value) {
        GetStatus status = Get(out ulong u);
        if (status != GetStatus.Success) {
            value = default;
            return status;
        }
        if (u > ushort.MaxValue) {
            value = ushort.MaxValue;
            return GetStatus.Saturated;
        }
        value = (ushort)u;
        return GetStatus.Success;
    }

    public GetStatus Get(out sbyte value) {
        GetStatus status = Get(out long l);
        if (status != GetStatus.Success) {
            value = default;
            return status;
        }
        if (l < sbyte.MinValue || l > sbyte.MaxValue) {
            value = l < sbyte.MinValue ? sbyte.MinValue : sbyte.MaxValue;
            return GetStatus.Saturated;
        }
        value = (sbyte)l;
        return GetStatus.Success;
    }

    public GetStatus Get(out byte value) {
        GetStatus status = Get(out ulong u);
        if (status != GetStatus.Success) {
            value = default;
            return status;
        }
        if (u > byte.MaxValue) {
            value = byte.MaxValue;
            return GetStatus.Saturated;
        }
        value = (byte)u;
        return GetStatus.Success;
    }

    #endregion
}
