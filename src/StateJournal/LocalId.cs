namespace Atelia.StateJournal;

public readonly record struct LocalId(uint Value) {
    /// <summary>表示空引用，不指向任何对象。</summary>
    public static LocalId Null => default;  // Value = 0
    public bool IsNull => Value == 0;
}
