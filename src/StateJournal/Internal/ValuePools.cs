using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

/// <summary>
///
/// </summary>
/// <typeparam name="T"></typeparam>
/// <see cref="SlotPool{T}">src/StateJournal/Pools/SlotPool.cs</see>
internal static class ValuePools {
    /// <summary>所有 64-bit 宽的堆分配值（double bits、ulong、long 的 unchecked cast）。</summary>
    public static GcPool<ulong> OfBits64 { get; } = new();

    /// <summary>
    /// Payload string 的独占 owned 堆 slot 池：每次 <see cref="GcPool{T}.Store"/> 分配新 slot，
    /// 不去重；slot 由持有者 (ValueBox 通过 ExclusiveBit) 显式 <see cref="GcPool{T}.Free"/>。
    /// 与 intern 用途的 <see cref="StringPool"/> 完全分离，互不干涉。
    /// </summary>
    public static GcPool<string> OfOwnedString { get; } = new();
}
