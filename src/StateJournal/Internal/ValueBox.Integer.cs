using System.Diagnostics;
using System.Runtime.CompilerServices;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial struct ValueBox {
    #region From integer

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
    public static ValueBox FromInt32(int value) => FromInlineableSigned(value);

    /// <summary>始终 inline。uint 值域 [0, 2^32-1] 完全在 inline 容量以内。</summary>
    public static ValueBox FromUInt32(uint value) => FromInlineableUnsigned(value);

    /// <summary>始终 inline。</summary>
    public static ValueBox FromInt16(short value) => FromInlineableSigned(value);

    /// <summary>始终 inline。</summary>
    public static ValueBox FromUInt16(ushort value) => FromInlineableUnsigned(value);

    /// <summary>始终 inline。</summary>
    public static ValueBox FromSByte(sbyte value) => FromInlineableSigned(value);

    /// <summary>始终 inline。</summary>
    public static ValueBox FromByte(byte value) => FromInlineableUnsigned(value);

    /// <summary>小范围有符号整数的快速内联编码路径。不触发堆分配回退的安全边界为：[-2^61, 2^62-1]。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueBox FromInlineableSigned(long value) {
        Debug.Assert(value >= LzcConstants.NegIntInlineMin && value < (long)LzcConstants.NonnegIntInlineCap);
        ulong u = unchecked((ulong)value);
        return new(value >= 0 ? (u | LzcConstants.NonnegIntTag) : (u & LzcConstants.NegIntPayloadMask));
    }

    /// <summary>小范围无符号整数的快速内联编码路径。不触发堆分配回退的安全边界为：[0, 2^62-1]。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueBox FromInlineableUnsigned(ulong value) {
        Debug.Assert(value < LzcConstants.NonnegIntInlineCap);
        return new(value | LzcConstants.NonnegIntTag);
    }

    #endregion
    #region Get integer 不从浮点值转换

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong DecodeInlineNonnegInt() {
        Debug.Assert(GetLZC() == LzcCode.InlineNonnegInt);
        return _bits & LzcConstants.NonnegIntPayloadMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long DecodeInlineNegInt() {
        Debug.Assert(GetLZC() == LzcCode.InlineNegInt);
        return unchecked((long)(_bits | LzcConstants.NegIntSignRestore));
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

    /// <summary>尝试按long类型读出内部保存的值。</summary>
    /// <returns>
    /// <see cref="GetIssue.None"/>
    /// <see cref="GetIssue.Saturated"/>
    /// <see cref="GetIssue.TypeMismatch"/>
    /// </returns>
    public GetIssue Get(out long value) {
        LzcCode lzc = GetLZC();
        if (lzc == LzcCode.InlineNonnegInt) {
            value = (long)DecodeInlineNonnegInt(); // inline范围是62bit所以不会溢出
            return GetIssue.None;
        }
        if (lzc == LzcCode.InlineNegInt) {
            value = DecodeInlineNegInt();
            return GetIssue.None;
        }
        if (lzc == LzcCode.HeapSlot) {
            DurableValueKind kind = GetHeapKind();
            if (kind == DurableValueKind.NonnegativeInteger) {
                ulong u = DecodeHeapNonnegInt();
                if (u > (ulong)long.MaxValue) {
                    value = long.MaxValue;
                    return GetIssue.Saturated;
                }
                value = (long)u;
                return GetIssue.None;
            }
            if (kind == DurableValueKind.NegativeInteger) {
                value = DecodeHeapNegInt();
                return GetIssue.None;
            }
        }
        value = default;
        return GetIssue.TypeMismatch;
    }

    /// <summary>尝试按ulong类型读出内部保存的值。</summary>
    /// <returns>
    /// <see cref="GetIssue.None"/>
    /// <see cref="GetIssue.Saturated"/>
    /// <see cref="GetIssue.TypeMismatch"/>
    /// </returns>
    public GetIssue Get(out ulong value) {
        LzcCode lzc = GetLZC();
        if (lzc == LzcCode.InlineNonnegInt) {
            value = DecodeInlineNonnegInt();
            return GetIssue.None;
        }
        if (lzc == LzcCode.HeapSlot) {
            DurableValueKind kind = GetHeapKind();
            if (kind == DurableValueKind.NonnegativeInteger) {
                value = DecodeHeapNonnegInt();
                return GetIssue.None;
            }
            if (kind == DurableValueKind.NegativeInteger) {
                value = ulong.MinValue;
                return GetIssue.Saturated;
            }
        }

        if (lzc == LzcCode.InlineNegInt) {
            value = ulong.MinValue;
            return GetIssue.Saturated;
        }

        value = default;
        return GetIssue.TypeMismatch;
    }

    public GetIssue Get(out int value) {
        GetIssue status = Get(out long l);
        if (status > GetIssue.Saturated) {
            value = default;
            return status;
        }
        if (l < int.MinValue || l > int.MaxValue) {
            value = l < int.MinValue ? int.MinValue : int.MaxValue;
            return GetIssue.Saturated;
        }
        value = (int)l;
        return status;
    }

    public GetIssue Get(out uint value) {
        GetIssue status = Get(out ulong u);
        if (status > GetIssue.Saturated) {
            value = default;
            return status;
        }
        if (u > uint.MaxValue) {
            value = uint.MaxValue;
            return GetIssue.Saturated;
        }
        value = (uint)u;
        return status;
    }

    public GetIssue Get(out short value) {
        GetIssue status = Get(out long l);
        if (status > GetIssue.Saturated) {
            value = default;
            return status;
        }
        if (l < short.MinValue || l > short.MaxValue) {
            value = l < short.MinValue ? short.MinValue : short.MaxValue;
            return GetIssue.Saturated;
        }
        value = (short)l;
        return status;
    }

    public GetIssue Get(out ushort value) {
        GetIssue status = Get(out ulong u);
        if (status > GetIssue.Saturated) {
            value = default;
            return status;
        }
        if (u > ushort.MaxValue) {
            value = ushort.MaxValue;
            return GetIssue.Saturated;
        }
        value = (ushort)u;
        return status;
    }

    public GetIssue Get(out sbyte value) {
        GetIssue status = Get(out long l);
        if (status > GetIssue.Saturated) {
            value = default;
            return status;
        }
        if (l < sbyte.MinValue || l > sbyte.MaxValue) {
            value = l < sbyte.MinValue ? sbyte.MinValue : sbyte.MaxValue;
            return GetIssue.Saturated;
        }
        value = (sbyte)l;
        return status;
    }

    public GetIssue Get(out byte value) {
        GetIssue status = Get(out ulong u);
        if (status > GetIssue.Saturated) {
            value = default;
            return status;
        }
        if (u > byte.MaxValue) {
            value = byte.MaxValue;
            return GetIssue.Saturated;
        }
        value = (byte)u;
        return status;
    }

    #endregion
    #region Exclusive set
    // ai:test `atelia/tests/StateJournal.Tests/Internal/ValueBoxExclusiveSetTests.cs`

    /// <summary>
    /// 独占更新：将 ValueBox 覆写为指定的 long 值。
    /// 若旧值与新值都使用 <see cref="ValuePools.Bits64"/>，则 inplace 修改 Slot 中的值，
    /// 避免 Free + Store 的开销。其他情况下清理旧 Slot（如有）并编码新值。
    /// </summary>
    internal static void ExclusiveSetInt64(ref ValueBox box, long value) {
        ulong u = unchecked((ulong)value);
        if (value >= 0) {
            if (u < LzcConstants.NonnegIntInlineCap) {
                FreeOldBits64IfNeeded(box);
                box = new(u | LzcConstants.NonnegIntTag);
                return;
            }
            SlotHandle h = StoreOrReuseBits64(box, u);
            box = EncodeHeapSlot(DurableValueKind.NonnegativeInteger, h);
        } else {
            if (value >= LzcConstants.NegIntInlineMin) {
                FreeOldBits64IfNeeded(box);
                box = new(u & LzcConstants.NegIntPayloadMask);
                return;
            }
            SlotHandle h = StoreOrReuseBits64(box, u);
            box = EncodeHeapSlot(DurableValueKind.NegativeInteger, h);
        }
    }

    /// <summary>
    /// 独占更新：将 ValueBox 覆写为指定的 ulong 值。
    /// 逻辑同 <see cref="ExclusiveSetInt64"/>，仅无负数路径。
    /// </summary>
    internal static void ExclusiveSetUInt64(ref ValueBox box, ulong value) {
        if (value < LzcConstants.NonnegIntInlineCap) {
            FreeOldBits64IfNeeded(box);
            box = new(value | LzcConstants.NonnegIntTag);
            return;
        }
        SlotHandle h = StoreOrReuseBits64(box, value);
        box = EncodeHeapSlot(DurableValueKind.NonnegativeInteger, h);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExclusiveSetInlineSigned(ref ValueBox box, long value) {
        FreeOldBits64IfNeeded(box);
        box = FromInlineableSigned(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExclusiveSetInlineUnsigned(ref ValueBox box, ulong value) {
        FreeOldBits64IfNeeded(box);
        box = FromInlineableUnsigned(value);
    }

    /// <summary>独占更新为 int。始终 inline。</summary>
    internal static void ExclusiveSetInt32(ref ValueBox box, int value) => ExclusiveSetInlineSigned(ref box, value);

    /// <summary>独占更新为 uint。始终 inline。</summary>
    internal static void ExclusiveSetUInt32(ref ValueBox box, uint value) => ExclusiveSetInlineUnsigned(ref box, value);

    /// <summary>独占更新为 short。始终 inline。</summary>
    internal static void ExclusiveSetInt16(ref ValueBox box, short value) => ExclusiveSetInlineSigned(ref box, value);

    /// <summary>独占更新为 ushort。始终 inline。</summary>
    internal static void ExclusiveSetUInt16(ref ValueBox box, ushort value) => ExclusiveSetInlineUnsigned(ref box, value);

    /// <summary>独占更新为 sbyte。始终 inline。</summary>
    internal static void ExclusiveSetSByte(ref ValueBox box, sbyte value) => ExclusiveSetInlineSigned(ref box, value);

    /// <summary>独占更新为 byte。始终 inline。</summary>
    internal static void ExclusiveSetByte(ref ValueBox box, byte value) => ExclusiveSetInlineUnsigned(ref box, value);

    #endregion
}
