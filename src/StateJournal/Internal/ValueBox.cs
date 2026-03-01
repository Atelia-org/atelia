using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

/// <summary>
///
/// </summary>
/// <remarks>
/// 作为Dictionary的TValue时，如果TKey是4字节值，`StructLayout(Pack = 4)`能避免Padding以节约4字节内存。
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly partial struct ValueBox {
    internal const int HeapKindBitCount = 7, HeapHandleBitCount = 32;
    private readonly ulong _bits;

    internal ValueBox(ulong bits) => _bits = bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetBits() => _bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LzcCode GetLZC() => (LzcCode)BitOperations.LeadingZeroCount(_bits);

    private DurableValueKind GetHeapKind() {
        Debug.Assert(GetLZC() == LzcCode.HeapSlot);
        return (DurableValueKind)(_bits >> HeapHandleBitCount) & DurableValueKind.Mask;
    }
    private SlotHandle GetHeapHandle() {
        Debug.Assert(GetLZC() == LzcCode.HeapSlot);
        return new SlotHandle((uint)_bits); // 依赖 HeapHandleBitCount == 32
    }
    private static ValueBox EncodeHeapSlot(DurableValueKind kind, SlotHandle handle) => new(LzcConstants.HeapSlotTag | ((ulong)kind << HeapHandleBitCount) | handle.Packed);

    #region Bits64 Slot 管理原语（ExclusiveSet 共用）

    /// <summary>
    /// 检查当前 ValueBox 是否持有 Bits64 池的堆 Slot（NonnegativeInteger、NegativeInteger 或 FloatingPoint）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetBits64Handle(out SlotHandle handle) {
        if (GetLZC() == LzcCode.HeapSlot) {
            DurableValueKind kind = GetHeapKind();
            if (kind is DurableValueKind.NonnegativeInteger
                     or DurableValueKind.NegativeInteger
                     or DurableValueKind.FloatingPoint) {
                handle = GetHeapHandle();
                return true;
            }
        }
        handle = default;
        return false;
    }

    /// <summary>释放旧 ValueBox 持有的 Bits64 Slot（如有）。用于新值为 inline 编码时的旧 slot 清理。</summary>
    private static void FreeOldBits64IfNeeded(ValueBox old) {
        if (old.TryGetBits64Handle(out SlotHandle h)) { ValuePools.Bits64.Free(h); }
    }

    /// <summary>
    /// 为新的 64-bit 原始值获取 Slot：若旧 box 持有 Bits64 Slot 则 inplace 更新并返回同一 handle，
    /// 否则分配新 Slot。
    /// </summary>
    private static SlotHandle StoreOrReuseBits64(ValueBox old, ulong rawBits) {
        if (old.TryGetBits64Handle(out SlotHandle h)) {
            ValuePools.Bits64[h] = rawBits;
            return h;
        }
        return ValuePools.Bits64.Store(rawBits);
    }

    #endregion
}
