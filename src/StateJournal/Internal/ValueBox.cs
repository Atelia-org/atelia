using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

/// <summary>
///
/// </summary>
/// <remarks>
/// 作为Dictionary的TValue时，如果TKey是4字节值，`StructLayout(Pack = 4)`能避免Padding以节约4字节内存。
/// </remarks>
/// <summary>
/// 内部值存储的不透明载体。此类型本身为 public 以便用作泛型类型参数，
/// 但所有成员均为 internal，外部代码不应直接操作。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly partial struct ValueBox {
    private readonly ulong _bits;
    internal ValueBox(ulong bits) => _bits = bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong GetBits() => _bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly BoxLzc GetLzc() => (BoxLzc)BitOperations.LeadingZeroCount(GetBits());

    public static ValueBox Null => new(LzcConstants.BoxNull); // 有意避开了0值，default，以实现内部的明确赋值检查。
    public readonly bool IsNull => GetBits() == LzcConstants.BoxNull;
    internal readonly bool IsUninitialized => GetBits() == LzcConstants.BoxUninitialized;

    public readonly ValueKind GetValueKind() => GetLzc() switch {
        BoxLzc.InlineDouble => ValueKind.FloatingPoint,
        BoxLzc.InlineNonnegInt => ValueKind.NonnegativeInteger,
        BoxLzc.InlineNegInt => ValueKind.NegativeInteger,
        BoxLzc.HeapSlot => GetHeapKind() switch {
            HeapValueKind.FloatingPoint => ValueKind.FloatingPoint,
            HeapValueKind.NonnegativeInteger => ValueKind.NonnegativeInteger,
            HeapValueKind.NegativeInteger => ValueKind.NegativeInteger,
            HeapValueKind.String => ValueKind.String,
            _ => throw new UnreachableException()
        },
        BoxLzc.DurableRef => GetDurRefKind() switch {
            DurableObjectKind.MixedDict => ValueKind.MixedDict,
            DurableObjectKind.TypedDict => ValueKind.TypedDict,
            DurableObjectKind.MixedList => ValueKind.MixedList,
            DurableObjectKind.TypedList => ValueKind.TypedList,
            _ => throw new UnreachableException()
        },
        BoxLzc.Boolean => ValueKind.Boolean,
        BoxLzc.Null => ValueKind.Null,
        _ => throw new UnreachableException()
    };

    #region HeapSlot
    internal const int HeapKindBitCount = 6, ExclusiveBitCount = 1, HeapHandleBitCount = 32;
    private const int HeapKindShift = ExclusiveBitCount + HeapHandleBitCount; // 33
    private const ulong ExclusiveBit = 1UL << HeapHandleBitCount; // bit32
    private readonly HeapValueKind GetHeapKind() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        return (HeapValueKind)(GetBits() >> HeapKindShift) & HeapValueKind.Mask;
    }
    /// <summary>依赖<see cref="HeapHandleBitCount"/> == 32</summary>
    private readonly SlotHandle GetHeapHandle() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        return new((uint)GetBits()); // 依赖 HeapHandleBitCount == 32
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly uint GetTagAndKind() => (uint)(GetBits() >> HeapKindShift);
    /// <summary>默认设置ExclusiveBit。目前仅对Heap Float/Integer有效。</summary>
    private static ValueBox EncodeHeapSlot(HeapValueKind kind, SlotHandle handle) => new(LzcConstants.HeapSlotTag | ((ulong)kind << HeapKindShift) | ExclusiveBit | handle.Packed);
    #endregion

    #region Bits64 Slot 管理原语（ExclusiveSet 共用）
    internal const uint TagHeapKindFloat = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)HeapValueKind.FloatingPoint;
    internal const uint TagHeapKindNonnegInt = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)HeapValueKind.NonnegativeInteger;
    internal const uint TagHeapKindNegInt = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)HeapValueKind.NegativeInteger;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsHeapFloatOrInteger(uint tagAndKind) => tagAndKind is TagHeapKindFloat or TagHeapKindNonnegInt or TagHeapKindNegInt;
    internal readonly bool IsHeapFloatOrInteger() => IsHeapFloatOrInteger(GetTagAndKind());

    /// <summary>
    /// 检查当前 ValueBox 是否持有 Bits64 池的堆 Slot（NonnegativeInteger、NegativeInteger 或 FloatingPoint）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool TryGetBits64Handle(out SlotHandle handle) {
        if (IsHeapFloatOrInteger()) {
            handle = GetHeapHandle();
            return true;
        }
        handle = default;
        return false;
    }

    /// <summary>释放旧 ValueBox 持有的独占 Bits64 Slot（如有）。冻结 slot 不释放（属于 committed 共享）。</summary>
    private static void FreeOldBits64IfNeeded(ValueBox old) {
        if (old.TryGetBits64Handle(out SlotHandle h) && old.IsExclusive) { ValuePools.OfBits64.Free(h); }
    }

    /// <summary>
    /// 为新的 64-bit 原始值获取 Slot：若旧 box 持有独占 Bits64 Slot 则 inplace 更新并返回同一 handle，
    /// 否则分配新 Slot（旧 box 冻结或非 Bits64 时触发）。
    /// </summary>
    private static SlotHandle StoreOrReuseBits64(ValueBox old, ulong rawBits) {
        if (old.TryGetBits64Handle(out SlotHandle h) && old.IsExclusive) {
            ValuePools.OfBits64[h] = rawBits;
            return h;
        }
        return ValuePools.OfBits64.Store(rawBits);
    }

    /// <summary>当前 ValueBox 是否持有独占（可变）的堆 Slot。仅对 HeapSlot 编码有意义。</summary>
    private readonly bool IsExclusive => (GetBits() & ExclusiveBit) != 0;

    /// <summary>清除 Exclusive bit。仅对 HeapSlot 调用安全（inline 值的 bit32 是 payload）。</summary>
    private readonly ValueBox AsFrozen() => new(GetBits() & ~ExclusiveBit);

    /// <summary>
    /// 冻结 ValueBox 用于 Commit 时共享。
    /// 对堆分配值：清除 Exclusive bit。对 inline 值：直接返回（bit32 是 payload 的一部分，不可修改）。
    /// </summary>
    internal static ValueBox Freeze(ValueBox box) =>
        box.GetLzc() == BoxLzc.HeapSlot ? box.AsFrozen() : box;

    /// <summary>
    /// 无条件释放 ValueBox 持有的 Bits64 Slot（如有），不论 exclusive/frozen 状态。
    /// 调用者必须确保自己是该 Slot 的最后持有者。对 inline 值和 InternPool 类型无操作。
    /// </summary>
    internal static void ReleaseBits64Slot(ValueBox box) {
        if (box.TryGetBits64Handle(out SlotHandle h)) {
            ValuePools.OfBits64.Free(h);
        }
    }

    internal static bool UpdateToNull(ref ValueBox old) {
        if (old.IsNull) { return false; }
        FreeOldBits64IfNeeded(old);
        old = Null;
        return true;
    }

    #endregion

    internal interface ITypedFace<T> where T : notnull {
        static abstract ValueBox From(T? value);
        /// <summary>Update if <paramref name="old"/> is not Uninitialized.
        /// Init if <paramref name="old"/> is Uninitialized.</summary>
        /// <returns>is <paramref name="old"/> changed.</returns>
        static abstract bool UpdateOrInit(ref ValueBox old, T? value);
        static abstract GetIssue Get(ValueBox box, out T? value);
    }
}
