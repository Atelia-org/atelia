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
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly partial struct ValueBox {
    internal const int HeapKindBitCount = 6, ExclusiveBitCount = 1, HeapHandleBitCount = 32;
    private const int KindShift = ExclusiveBitCount + HeapHandleBitCount; // 33
    private const ulong ExclusiveBit = 1UL << HeapHandleBitCount; // bit32
    private readonly ulong _bits;

    internal ValueBox(ulong bits) => _bits = bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetBits() => _bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private BoxLzc GetLzc() => (BoxLzc)BitOperations.LeadingZeroCount(_bits);

    private ValueKind GetHeapKind() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        return (ValueKind)(_bits >> KindShift) & ValueKind.Mask;
    }
    private SlotHandle GetHeapHandle() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        return new SlotHandle((uint)_bits); // 依赖 HeapHandleBitCount == 32
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetTagAndKind() => (uint)(_bits >> KindShift);
    private static ValueBox EncodeHeapSlot(ValueKind kind, SlotHandle handle) => new(LzcConstants.HeapSlotTag | ((ulong)kind << KindShift) | ExclusiveBit | handle.Packed);

    public static ValueBox Null => new(LzcConstants.BoxNull); // 有意避开了0值，default，以实现内部的明确赋值检查。
    public bool IsNull => _bits == LzcConstants.BoxNull;
    internal bool IsUninitialized => _bits == LzcConstants.BoxUninitialized;

    public ValueKind GetValueKind() => throw new NotImplementedException(); // AI TODO

    #region Bits64 Slot 管理原语（ExclusiveSet 共用）
    internal const uint TagHeapKindFloat = (uint)(LzcConstants.HeapSlotTag >> KindShift) | (uint)ValueKind.FloatingPoint;
    internal const uint TagHeapKindNonnegInt = (uint)(LzcConstants.HeapSlotTag >> KindShift) | (uint)ValueKind.NonnegativeInteger;
    internal const uint TagHeapKindNegInt = (uint)(LzcConstants.HeapSlotTag >> KindShift) | (uint)ValueKind.NegativeInteger;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsHeapFloatOrInteger(uint tagAndKind) => tagAndKind is TagHeapKindFloat or TagHeapKindNonnegInt or TagHeapKindNegInt;
    internal bool IsHeapFloatOrInteger() => IsHeapFloatOrInteger(GetTagAndKind());

    /// <summary>
    /// 检查当前 ValueBox 是否持有 Bits64 池的堆 Slot（NonnegativeInteger、NegativeInteger 或 FloatingPoint）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetBits64Handle(out SlotHandle handle) {
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
    private bool IsExclusive => (_bits & ExclusiveBit) != 0;

    /// <summary>清除 Exclusive bit。仅对 HeapSlot 调用安全（inline 值的 bit32 是 payload）。</summary>
    private ValueBox AsFrozen() => new(_bits & ~ExclusiveBit);

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

    #endregion

    internal interface ITypedFace<T> where T : notnull {
        static abstract ValueBox From(T? value);
        static abstract bool Update(ref ValueBox old, T? value);
        static abstract GetIssue Get(ValueBox box, out T? value);
    }
}
