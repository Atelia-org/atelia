// Source: Atelia.StateJournal - 对象已分离异常
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.1.0.1

namespace Atelia.StateJournal;

/// <summary>
/// 对象已分离异常。
/// </summary>
/// <remarks>
/// <para>
/// 当访问 <see cref="DurableObjectState.Detached"/> 状态的对象时抛出此异常。
/// </para>
/// <para>
/// 对应条款：<c>[S-TRANSIENT-DISCARD-DETACH]</c>
/// </para>
/// </remarks>
public class ObjectDetachedException : InvalidOperationException {
    /// <summary>
    /// 已分离对象的 ID。
    /// </summary>
    public ulong ObjectId { get; }

    /// <summary>
    /// 创建新的 ObjectDetachedException。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    public ObjectDetachedException(ulong objectId)
        : base($"Object {objectId} has been detached and cannot be accessed.") {
        ObjectId = objectId;
    }
}
