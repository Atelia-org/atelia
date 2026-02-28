using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

/// <summary>
///
/// </summary>
/// <typeparam name="T"></typeparam>
/// <see cref="Atelia.StateJournal.Pools.SlotPool{T}">src/StateJournal/Pools/SlotPool.cs</see>
internal static class ValuePools {
    /// <summary>所有 64-bit 宽的堆分配值（double bits、ulong、long 的 unchecked cast）。</summary>
    public static IValuePool<ulong> Bits64 { get; } = new GcPool<ulong>();
}
