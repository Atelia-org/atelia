// Source: Atelia.StateJournal - 持久化对象基类
// Spec: atelia/docs/StateJournal/workspace-binding-spec.md §2.1

namespace Atelia.StateJournal;

/// <summary>
/// 持久化对象的抽象基类，所有 DurableObject 必须继承此类。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[S-WORKSPACE-OWNING-EXACTLY-ONE]</c>: 每个对象绑定到唯一的 Owning Workspace</item>
///   <item><c>[S-WORKSPACE-OWNING-IMMUTABLE]</c>: 绑定在对象生命周期内不可变</item>
///   <item><c>[S-WORKSPACE-CTOR-REQUIRES-WORKSPACE]</c>: 构造函数必须接收 Workspace 参数</item>
/// </list>
/// </para>
/// <para>
/// 设计隐喻（护照模式）：
/// <list type="bullet">
///   <item><c>_owningWorkspace</c>：对象的"国籍"，构造时确定</item>
///   <item>构造时绑定：出生地原则，对象诞生于哪个 Workspace 就归属于哪个</item>
///   <item>绑定不可变：护照一经颁发，国籍不变</item>
/// </list>
/// </para>
/// </remarks>
public abstract class DurableObjectBase : IDurableObject {
    private readonly Workspace _owningWorkspace;
    private DurableObjectState _state;

    /// <summary>
    /// 创建持久化对象。此构造函数仅供 Workspace 工厂方法调用。
    /// </summary>
    /// <param name="workspace">对象所属的 Workspace。</param>
    /// <param name="objectId">对象的唯一标识符。</param>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> 为 null。</exception>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[S-WORKSPACE-CTOR-REQUIRES-WORKSPACE]</c>
    /// </para>
    /// <para>
    /// 构造函数为 <c>protected internal</c>，禁止用户直接调用 <c>new DurableDict()</c>。
    /// 用户应通过 <see cref="Workspace.CreateObject{T}"/> 或 <see cref="Workspace.LoadObject{T}"/> 创建对象。
    /// </para>
    /// </remarks>
    protected internal DurableObjectBase(Workspace workspace, ulong objectId) {
        _owningWorkspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        ObjectId = objectId;
        _state = DurableObjectState.TransientDirty;
    }

    /// <summary>
    /// 从 Committed State 加载对象。此构造函数仅供 Workspace 工厂方法调用。
    /// </summary>
    /// <param name="workspace">对象所属的 Workspace。</param>
    /// <param name="objectId">对象的唯一标识符。</param>
    /// <param name="initialState">初始状态（通常为 Clean 或 TransientDirty）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> 为 null。</exception>
    protected internal DurableObjectBase(Workspace workspace, ulong objectId, DurableObjectState initialState) {
        _owningWorkspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        ObjectId = objectId;
        _state = initialState;
    }

    // === IDurableObject 实现 ===

    /// <inheritdoc/>
    public ulong ObjectId { get; }

    /// <inheritdoc/>
    public DurableObjectState State => _state;

    /// <inheritdoc/>
    public abstract bool HasChanges { get; }

    /// <inheritdoc/>
    public abstract void WritePendingDiff(System.Buffers.IBufferWriter<byte> writer);

    /// <inheritdoc/>
    public abstract void OnCommitSucceeded();

    /// <inheritdoc/>
    public abstract void DiscardChanges();

    // === Protected 成员 ===

    /// <summary>
    /// 获取对象所属的 Workspace。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[S-LAZYLOAD-DISPATCH-BY-OWNER]</c>
    /// </para>
    /// <para>
    /// Lazy Load 时使用此属性获取 Workspace，确保按 Owning Workspace 分派，
    /// 而不是使用调用点的 Ambient Workspace。
    /// </para>
    /// </remarks>
    protected Workspace OwningWorkspace => _owningWorkspace;

    /// <summary>
    /// 设置对象状态。
    /// </summary>
    /// <param name="state">新状态。</param>
    protected void SetState(DurableObjectState state) => _state = state;

    /// <summary>
    /// 注册对象为脏状态（Clean → PersistentDirty 转换时调用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 当对象从 Clean 状态变为 PersistentDirty 时，调用此方法将对象重新添加到 Workspace 的 DirtySet。
    /// </para>
    /// </remarks>
    protected void NotifyDirty() {
        _owningWorkspace.RegisterDirty(this);
    }

    /// <summary>
    /// 如果对象已分离则抛出异常。
    /// </summary>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    protected void ThrowIfDetached() {
        if (_state == DurableObjectState.Detached) { throw new ObjectDetachedException(ObjectId); }
    }

    /// <summary>
    /// 通过 Owning Workspace 加载关联对象（用于 Lazy Loading）。
    /// </summary>
    /// <typeparam name="T">对象类型。</typeparam>
    /// <param name="id">对象 ID。</param>
    /// <returns>加载的对象。</returns>
    /// <exception cref="InvalidOperationException">加载失败。</exception>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[S-LAZYLOAD-DISPATCH-BY-OWNER]</c>
    /// </para>
    /// <para>
    /// 此方法按 Owning Workspace 分派，确保跨 Scope 访问时使用正确的 Workspace。
    /// </para>
    /// </remarks>
    protected T LoadObject<T>(ulong id) where T : class, IDurableObject {
        var result = _owningWorkspace.LoadObject<T>(id);
        if (result.IsFailure) {
            throw new InvalidOperationException(
                $"Failed to load object {id}: {result.Error?.Message ?? "Unknown error"}"
            );
        }
        return result.Value!;
    }
}
