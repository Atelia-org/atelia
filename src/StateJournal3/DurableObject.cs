namespace Atelia.StateJournal3;

public abstract class DurableObject : DurableBase {
    DurableObjectState _state;

    public LocalId LocalId { get; }
    public DurableEpoch Epoch { get; }
    public GlobalId GlobalId { get; }

    /// <inheritdoc/>
    public DurableObjectState State => _state;

    /// <inheritdoc/>
    public abstract bool HasChanges { get; }

    public override bool Equals(DurableBase? other) {
        return (other is DurableObject otherObj) && otherObj.GlobalId == GlobalId;
    }

    public override Type ContentType => GetType();

    public bool IsLoaded {
        get {
            ThrowIfDetached();
            return _state != DurableObjectState.Unloaded;
        }
    }

    /// <summary>确保内容已加载，未加载则触发回放</summary>
    protected void EnsureLoaded() {
        if (!IsLoaded) {
            Epoch.Materialize(this); // 回放版本链，填充 _committed/_current
            _state = DurableObjectState.Clean;
        }
    }

    /// <inheritdoc/>
    public abstract void WritePendingDiff(System.Buffers.IBufferWriter<byte> writer);

    /// <inheritdoc/>
    public abstract void OnCommitSucceeded();

    /// <inheritdoc/>
    public abstract void DiscardChanges();

    /// <summary>设置对象状态。</summary>
    /// <param name="state">新状态。</param>
    protected void SetState(DurableObjectState state) => _state = state;

    /// <summary>注册对象为脏状态（Clean → PersistentDirty 转换时调用）。</summary>
    /// <remarks>
    /// 当对象从 Clean 状态变为 PersistentDirty 时，调用此方法将对象重新添加到 Workspace 的 DirtySet。
    /// </remarks>
    protected void NotifyDirty() {
        Epoch.RegisterDirty(this);
    }

    /// <summary>如果对象已分离则抛出异常。</summary>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    protected void ThrowIfDetached() {
        if (_state == DurableObjectState.Detached) { throw new ObjectDetachedException(LocalId); }
    }

    protected void EnsureReady() {
        ThrowIfDetached();
        EnsureLoaded();
    }
}
