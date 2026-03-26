using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// Symbol（intern 字符串）的编译期语义包装，隔离 object slot handle 与 symbol slot handle。
/// 其中 <c>0</c> 专门保留给 <see cref="Null"/>；非空 symbol 直接使用底层 <see cref="SlotHandle.Packed"/> 编码。
/// </summary>
internal readonly record struct SymbolId(uint Value) {
    /// <summary>表示空引用，不指向任何 symbol。</summary>
    public static SymbolId Null => default; // Value = 0
    public bool IsNull => Value == 0;

    /// <summary>从 SlotHandle 构造 SymbolId（直接使用 <see cref="SlotHandle.Packed"/>）。</summary>
    internal static SymbolId FromSlotHandle(SlotHandle handle) => new(handle.Packed);

    /// <summary>将 <see cref="Value"/> 解释为底层 <see cref="SlotHandle.Packed"/>。</summary>
    internal SlotHandle ToSlotHandle() => new(Value);
}
