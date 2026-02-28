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
    private static ValueBox EncodeHeapSlot(DurableValueKind kind, SlotHandle handle) => new(((ulong)kind << HeapHandleBitCount) | handle.Packed);
}
