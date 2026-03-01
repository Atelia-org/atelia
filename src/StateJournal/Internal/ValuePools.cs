using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

/// <summary>
///
/// </summary>
/// <typeparam name="T"></typeparam>
/// <see cref="Atelia.StateJournal.Pools.SlotPool{T}">src/StateJournal/Pools/SlotPool.cs</see>
internal static class ValuePools {
    /// <summary>所有 64-bit 宽的堆分配值（double bits、ulong、long 的 unchecked cast）。</summary>
    public static GcPool<ulong> Bits64 { get; } = new GcPool<ulong>();

    /// <summary>字符串去重池。同值字符串共享同一 SlotHandle，保证 <see cref="ValueBox.ValueEquals"/> 快速路径命中。</summary>
    public static InternPool<string> Strings { get; } = new InternPool<string>(StringComparer.Ordinal);
}
