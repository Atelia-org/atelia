using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

public readonly record struct LocalId(uint Value) {
    /// <summary>表示空引用，不指向任何对象。</summary>
    public static LocalId Null => default;  // Value = 0
    public bool IsNull => Value == 0;

    /// <summary>从 SlotHandle 构造 LocalId（使用 SlotHandle 的 packed uint 作为 Value）。</summary>
    internal static LocalId FromSlotHandle(SlotHandle handle) => new(handle.Packed);

    /// <summary>将 LocalId.Value 解释为 SlotHandle 的 packed 表示。</summary>
    internal SlotHandle ToSlotHandle() => new(Value);
}
