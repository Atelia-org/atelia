namespace Atelia.StateJournal3;

/// <summary>对象已分离异常。</summary>
/// <remarks>
/// 当访问 <see cref="DurableObjectState.Detached"/> 状态的对象时抛出此异常。
/// </remarks>
public class ObjectDetachedException : InvalidOperationException {
    /// <summary>已分离对象的 ID。</summary>
    public LocalId LocalId { get; }

    /// <summary>创建新的 ObjectDetachedException。</summary>
    /// <param name="objectId">对象 ID。</param>
    public ObjectDetachedException(LocalId localId)
        : base($"Object {localId} has been detached and cannot be accessed.") {
        LocalId = localId;
    }
}
