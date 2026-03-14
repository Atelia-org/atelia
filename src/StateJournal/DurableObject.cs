using Atelia.Data;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal;

public abstract class DurableObject {
    public abstract DurableObjectKind Kind { get; }

    DurableState _state;

    public LocalId LocalId { get; }
    public Revision Revision { get; }
    public GlobalId GlobalId { get; }

    /// <inheritdoc/>
    public DurableState State => _state;

    /// <inheritdoc/>
    public abstract bool HasChanges { get; }

    public bool IsLoaded {
        get {
            ThrowIfDetached();
            return _state != DurableState.Unloaded;
        }
    }

    /// <summary>确保内容已加载，未加载则触发回放</summary>
    protected void EnsureLoaded() {
        if (!IsLoaded) {
            Revision.Materialize(this); // 回放版本链，填充 _committed/_current
            _state = DurableState.Clean;
        }
    }

    /// <returns>RBF frame tag</returns>
    internal abstract FrameTag WritePendingDiff(IDiffWriter writer, DiffWriteContext context);

    private protected abstract ReadOnlySpan<byte> TypeCode { get; }

    internal abstract SizedPtr HeadTicket { get; }
    internal abstract bool IsTracked { get; }

    internal abstract void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context);

    internal abstract void ApplyDelta(ref BinaryDiffReader reader, SizedPtr parentTicket);

    /// <summary>Load 完成后调用，修正 _previousVersion 并将 _committed 数据同步到 _current。</summary>
    internal abstract void OnLoadCompleted(SizedPtr versionTicket);

    public abstract void DiscardChanges();

    /// <summary>设置对象状态。</summary>
    /// <param name="state">新状态。</param>
    protected void SetState(DurableState state) => _state = state;

    /// <summary>注册对象为脏状态（Clean → PersistentDirty 转换时调用）。</summary>
    /// <remarks>
    /// 当对象从 Clean 状态变为 PersistentDirty 时，调用此方法将对象重新添加到 Workspace 的 DirtySet。
    /// </remarks>
    protected void NotifyDirty() {
        Revision.RegisterDirty(this);
    }

    /// <summary>如果对象已分离则抛出异常。</summary>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    protected void ThrowIfDetached() {
        if (_state == DurableState.Detached) { throw new ObjectDetachedException(LocalId); }
    }

    protected void EnsureReady() {
        ThrowIfDetached();
        EnsureLoaded();
    }
}
