using System.Diagnostics;
using System.Runtime.CompilerServices;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

// ai:test `tests/StateJournal.Tests/Internal/ValueBoxInt64Tests.cs`
// ai:test `tests/StateJournal.Tests/Internal/ValueBoxUInt64Tests.cs`
// ai:test `tests/StateJournal.Tests/Internal/ValueBoxInt32Tests.cs`
// ai:test `tests/StateJournal.Tests/Internal/ValueBoxUInt32Tests.cs`
// ai:test `tests/StateJournal.Tests/Internal/ValueBoxSmallIntTests.cs`
partial struct ValueBox {
    internal readonly struct Int64Face : ITypedFace<long> {
        /// <summary>将 long 编码为 ValueBox。值域 [-2^61, 2^62-1] 内联；超出范围回退到堆分配。</summary>
        public static ValueBox From(long value) {
            ulong u = unchecked((ulong)value);
            if (value >= 0) {
                if (u < LzcConstants.NonnegIntInlineCap) { return new(u | LzcConstants.NonnegIntTag); }
                return EncodeHeapSlot(ValueKind.NonnegativeInteger, ValuePools.OfBits64.Store(u));
            }
            // value < 0
            if (value >= LzcConstants.NegIntInlineMin) { return new(u & LzcConstants.NegIntPayloadMask); }
            return EncodeHeapSlot(ValueKind.NegativeInteger, ValuePools.OfBits64.Store(u));
        }

        /// <summary>
        /// 独占更新：将 ValueBox 覆写为指定的 long 值。
        /// 若旧值与新值都使用 <see cref="ValuePools.OfBits64"/>，则 inplace 修改 Slot 中的值，
        /// 避免 Free + Store 的开销。其他情况下清理旧 Slot（如有）并编码新值。
        /// </summary>
        public static bool UpdateOrInit(ref ValueBox box, long value) {
            BoxLzc lzc = box.GetLzc();
            ulong u = unchecked((ulong)value);
            if (value >= 0) {
                if (lzc == BoxLzc.InlineNonnegInt && box.DecodeInlineNonnegIntAsSigned() == value) { return false; }
                if (u < LzcConstants.NonnegIntInlineCap) {
                    FreeOldBits64IfNeeded(box);
                    box = new(u | LzcConstants.NonnegIntTag);
                    return true;
                }
                if (lzc == BoxLzc.HeapSlot && box.GetHeapKind() == ValueKind.NonnegativeInteger
                    && box.DecodeHeapNonnegInt() == u) { return false; }
                SlotHandle h = StoreOrReuseBits64(box, u);
                box = EncodeHeapSlot(ValueKind.NonnegativeInteger, h);
            }
            else {
                if (lzc == BoxLzc.InlineNegInt && box.DecodeInlineNegInt() == value) { return false; }
                if (value >= LzcConstants.NegIntInlineMin) {
                    FreeOldBits64IfNeeded(box);
                    box = new(u & LzcConstants.NegIntPayloadMask);
                    return true;
                }
                if (lzc == BoxLzc.HeapSlot && box.GetHeapKind() == ValueKind.NegativeInteger
                    && box.DecodeHeapNegInt() == value) { return false; }
                SlotHandle h = StoreOrReuseBits64(box, u);
                box = EncodeHeapSlot(ValueKind.NegativeInteger, h);
            }
            return true;
        }

        /// <summary>尝试按long类型读出内部保存的值。</summary>
        /// <returns>
        /// <see cref="GetIssue.None"/>
        /// <see cref="GetIssue.Saturated"/>
        /// <see cref="GetIssue.TypeMismatch"/>
        /// </returns>
        public static GetIssue Get(ValueBox box, out long value) {
            Debug.Assert(!box.IsUninitialized);
            BoxLzc lzc = box.GetLzc();
            if (lzc == BoxLzc.InlineNonnegInt) {
                value = box.DecodeInlineNonnegIntAsSigned();
                return GetIssue.None;
            }
            if (lzc == BoxLzc.InlineNegInt) {
                value = box.DecodeInlineNegInt();
                return GetIssue.None;
            }
            if (lzc == BoxLzc.HeapSlot) {
                ValueKind kind = box.GetHeapKind();
                if (kind == ValueKind.NonnegativeInteger) {
                    ulong u = box.DecodeHeapNonnegInt();
                    if (u > (ulong)long.MaxValue) {
                        value = long.MaxValue;
                        return GetIssue.Saturated;
                    }
                    value = (long)u;
                    return GetIssue.None;
                }
                if (kind == ValueKind.NegativeInteger) {
                    value = box.DecodeHeapNegInt();
                    return GetIssue.None;
                }
            }
            value = default;
            return GetIssue.TypeMismatch;
        }
    }

    internal readonly struct UInt64Face : ITypedFace<ulong> {
        /// <summary>将 ulong 编码为 ValueBox。值域 [0, 2^62-1] 内联；超出范围回退到堆分配。</summary>
        public static ValueBox From(ulong value) {
            if (value < LzcConstants.NonnegIntInlineCap) { return new(value | LzcConstants.NonnegIntTag); }
            return EncodeHeapSlot(ValueKind.NonnegativeInteger, ValuePools.OfBits64.Store(value));
        }

        /// <summary>
        /// 独占更新：将 ValueBox 覆写为指定的 ulong 值。
        /// 逻辑同 <see cref="Int64Face.UpdateOrInit"/>，仅无负数路径。
        /// </summary>
        public static bool UpdateOrInit(ref ValueBox box, ulong value) {
            BoxLzc lzc = box.GetLzc();
            if (lzc == BoxLzc.InlineNonnegInt && box.DecodeInlineNonnegInt() == value) { return false; }
            if (value < LzcConstants.NonnegIntInlineCap) {
                FreeOldBits64IfNeeded(box);
                box = new(value | LzcConstants.NonnegIntTag);
                return true;
            }
            if (lzc == BoxLzc.HeapSlot && box.GetHeapKind() == ValueKind.NonnegativeInteger
                && box.DecodeHeapNonnegInt() == value) { return false; }
            SlotHandle h = StoreOrReuseBits64(box, value);
            box = EncodeHeapSlot(ValueKind.NonnegativeInteger, h);
            return true;
        }

        /// <summary>尝试按ulong类型读出内部保存的值。</summary>
        /// <returns>
        /// <see cref="GetIssue.None"/>
        /// <see cref="GetIssue.Saturated"/>
        /// <see cref="GetIssue.TypeMismatch"/>
        /// </returns>
        public static GetIssue Get(ValueBox box, out ulong value) {
            Debug.Assert(!box.IsUninitialized);
            BoxLzc lzc = box.GetLzc();
            if (lzc == BoxLzc.InlineNonnegInt) {
                value = box.DecodeInlineNonnegInt();
                return GetIssue.None;
            }
            if (lzc == BoxLzc.HeapSlot) {
                ValueKind kind = box.GetHeapKind();
                if (kind == ValueKind.NonnegativeInteger) {
                    value = box.DecodeHeapNonnegInt();
                    return GetIssue.None;
                }
                if (kind == ValueKind.NegativeInteger) {
                    value = ulong.MinValue;
                    return GetIssue.Saturated;
                }
            }

            if (lzc == BoxLzc.InlineNegInt) {
                value = ulong.MinValue;
                return GetIssue.Saturated;
            }

            value = default;
            return GetIssue.TypeMismatch;
        }
    }

    internal readonly struct Int32Face : ITypedFace<int> {
        /// <summary>始终 inline。int 值域 [-2^31, 2^31-1] 完全在 inline 容量以内。</summary>
        public static ValueBox From(int value) => FromInlineableSigned(value);

        /// <summary>独占更新为 int。始终 inline。</summary>
        public static bool UpdateOrInit(ref ValueBox old, int value) => UpdateByInlineSigned(ref old, value);
        public static GetIssue Get(ValueBox box, out int value) {
            GetIssue status = Int64Face.Get(box, out long l);
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
    }

    internal readonly struct UInt32Face : ITypedFace<uint> {
        /// <summary>始终 inline。uint 值域 [0, 2^32-1] 完全在 inline 容量以内。</summary>
        public static ValueBox From(uint value) => FromInlineableUnsigned(value);

        /// <summary>独占更新为 uint。始终 inline。</summary>
        public static bool UpdateOrInit(ref ValueBox old, uint value) => UpdateByInlineUnsigned(ref old, value);
        public static GetIssue Get(ValueBox box, out uint value) {
            GetIssue status = UInt64Face.Get(box, out ulong u);
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
    }

    internal readonly struct Int16Face : ITypedFace<short> {
        /// <summary>始终 inline。</summary>
        public static ValueBox From(short value) => FromInlineableSigned(value);

        /// <summary>独占更新为 short。始终 inline。</summary>
        public static bool UpdateOrInit(ref ValueBox old, short value) => UpdateByInlineSigned(ref old, value);
        public static GetIssue Get(ValueBox box, out short value) {
            GetIssue status = Int64Face.Get(box, out long l);
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
    }

    internal readonly struct UInt16Face : ITypedFace<ushort> {
        /// <summary>始终 inline。</summary>
        public static ValueBox From(ushort value) => FromInlineableUnsigned(value);

        /// <summary>独占更新为 ushort。始终 inline。</summary>
        public static bool UpdateOrInit(ref ValueBox old, ushort value) => UpdateByInlineUnsigned(ref old, value);
        public static GetIssue Get(ValueBox box, out ushort value) {
            GetIssue status = UInt64Face.Get(box, out ulong u);
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
    }

    internal readonly struct SByteFace : ITypedFace<sbyte> {
        /// <summary>始终 inline。</summary>
        public static ValueBox From(sbyte value) => FromInlineableSigned(value);

        /// <summary>独占更新为 sbyte。始终 inline。</summary>
        public static bool UpdateOrInit(ref ValueBox old, sbyte value) => UpdateByInlineSigned(ref old, value);
        public static GetIssue Get(ValueBox box, out sbyte value) {
            GetIssue status = Int64Face.Get(box, out long l);
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
    }

    internal readonly struct ByteFace : ITypedFace<byte> {
        /// <summary>始终 inline。</summary>
        public static ValueBox From(byte value) => FromInlineableUnsigned(value);

        /// <summary>独占更新为 byte。始终 inline。</summary>
        public static bool UpdateOrInit(ref ValueBox old, byte value) => UpdateByInlineUnsigned(ref old, value);
        public static GetIssue Get(ValueBox box, out byte value) {
            GetIssue status = UInt64Face.Get(box, out ulong u);
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
    }

    #region Integer encoding helpers

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
    #region Integer decoding helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong DecodeInlineNonnegInt() {
        Debug.Assert(GetLzc() == BoxLzc.InlineNonnegInt);
        return _bits & LzcConstants.NonnegIntPayloadMask;
    }
    private long DecodeInlineNonnegIntAsSigned() => unchecked((long)DecodeInlineNonnegInt()); // inline范围是62bit所以不会溢出

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long DecodeInlineNegInt() {
        Debug.Assert(GetLzc() == BoxLzc.InlineNegInt);
        return unchecked((long)(_bits | LzcConstants.NegIntSignRestore));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong DecodeHeapNonnegInt() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        Debug.Assert(GetHeapKind() == ValueKind.NonnegativeInteger);
        return ValuePools.OfBits64[GetHeapHandle()];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long DecodeHeapNegInt() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        Debug.Assert(GetHeapKind() == ValueKind.NegativeInteger);
        return unchecked((long)ValuePools.OfBits64[GetHeapHandle()]);
    }

    #endregion
    #region Integer update helpers
    // ai:test `atelia/tests/StateJournal.Tests/Internal/ValueBoxExclusiveSetTests.cs`

    /// <summary>类型匹配且值相等时返回 false（无需后续处理）；否则编码新值并返回 true。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool UpdateByInlineSigned(ref ValueBox box, long value) {
        BoxLzc lzc = box.GetLzc();
        if (value >= 0) {
            if (lzc == BoxLzc.InlineNonnegInt && box.DecodeInlineNonnegIntAsSigned() == value) { return false; }
        }
        else {
            if (lzc == BoxLzc.InlineNegInt && box.DecodeInlineNegInt() == value) { return false; }
        }
        FreeOldBits64IfNeeded(box);
        box = FromInlineableSigned(value);
        return true;
    }

    /// <summary>类型匹配且值相等时返回 false（无需后续处理）；否则编码新值并返回 true。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool UpdateByInlineUnsigned(ref ValueBox box, ulong value) {
        if (box.GetLzc() == BoxLzc.InlineNonnegInt && box.DecodeInlineNonnegInt() == value) { return false; }
        FreeOldBits64IfNeeded(box);
        box = FromInlineableUnsigned(value);
        return true;
    }

    #endregion
}
