namespace Atelia.StateJournal;

/// <summary>
/// 指向 <see cref="DurableObject"/> 的轻量引用，由 (<see cref="Kind"/>, <see cref="Id"/>) 构成。
/// 内存布局与 <c>(DurableObjectKind, LocalId)</c> 元组相同，零成本抽象。
/// </summary>
internal readonly record struct DurableRef(DurableObjectKind Kind, LocalId Id) {
    /// <summary>空引用。等价于 <c>default</c>。</summary>
    internal static DurableRef Null => default;

    internal bool IsNull => Id.IsNull;

    internal static bool IsValidKind(DurableObjectKind kind) => kind is
        DurableObjectKind.MixedDict
        or DurableObjectKind.TypedDict
        or DurableObjectKind.MixedList
        or DurableObjectKind.TypedList;
}
