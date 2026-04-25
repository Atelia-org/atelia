namespace Atelia.StateJournal;

/// <summary>对象已冻结异常。</summary>
/// <remarks>
/// 当尝试修改 frozen DurableObject 时抛出此异常。
/// </remarks>
public sealed class ObjectFrozenException : InvalidOperationException {
    /// <summary>已冻结对象的 ID。</summary>
    public LocalId LocalId { get; }

    /// <summary>创建新的 ObjectFrozenException。</summary>
    public ObjectFrozenException(LocalId localId)
        : base($"Object {localId} is frozen and cannot be modified.") {
        LocalId = localId;
    }
}
