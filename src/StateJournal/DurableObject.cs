using System.Diagnostics;
using Atelia.Data;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal;

public abstract class DurableObject {
    public abstract DurableObjectKind Kind { get; }

    DurableState _state;
    Revision? _revision;

    public LocalId LocalId { get; private set; }

    /// <summary>所属 Revision。对象一经绑定即不可更改。</summary>
    /// <exception cref="InvalidOperationException">对象尚未绑定到 Revision。</exception>
    public Revision Revision => _revision ?? throw new InvalidOperationException("DurableObject not bound to a Revision.");

    /// <summary>由 Revision 调用，将对象与 Revision 绑定并分配 LocalId。一经绑定不可更改。</summary>
    /// <param name="revision">所属 Revision。</param>
    /// <param name="localId">分配的 LocalId。</param>
    /// <param name="initialState">绑定时设置的初始状态（新建对象为 TransientDirty，加载对象为 Clean）。</param>
    internal void Bind(Revision revision, LocalId localId, DurableState initialState = DurableState.Clean) {
        if (_revision is not null) { throw new InvalidOperationException($"DurableObject already bound to a Revision (LocalId={LocalId})."); }
        _revision = revision;
        LocalId = localId;
        _state = initialState;
    }

    /// <inheritdoc/>
    public DurableState State => _state;

    /// <inheritdoc/>
    public abstract bool HasChanges { get; }

    /// <returns>RBF frame tag</returns>
    internal abstract FrameTag WritePendingDiff(BinaryDiffWriter writer, ref DiffWriteContext context);

    private protected abstract ReadOnlySpan<byte> TypeCode { get; }

    internal abstract SizedPtr HeadTicket { get; }
    internal abstract bool IsTracked { get; }

    internal abstract void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context);

    internal abstract void ApplyDelta(ref BinaryDiffReader reader, SizedPtr parentTicket);

    /// <summary>Load 完成后调用，修正 _previousVersion 并将 _committed 数据同步到 _current。</summary>
    internal abstract void OnLoadCompleted(SizedPtr versionTicket);

    internal abstract void DiscardChanges();

    internal abstract void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) where TVisitor : IChildRefVisitor, allows ref struct;

    /// <summary>Compaction 时重写子引用中的 LocalId。无子引用的类型提供空实现。</summary>
    internal abstract void AcceptChildRefRewrite<TRewriter>(ref TRewriter rewriter) where TRewriter : IChildRefRewriter, allows ref struct;

    /// <summary>设置对象状态。</summary>
    /// <param name="state">新状态。</param>
    protected void SetState(DurableState state) => _state = state;

    /// <summary>是否已绑定到指定 Revision（不会抛异常）。</summary>
    internal bool IsBoundTo(Revision revision) => ReferenceEquals(_revision, revision);

    /// <summary>是否处于 Detached 状态。</summary>
    internal bool IsDetached => _state == DurableState.Detached;

    /// <summary>供 Revision 在 GC Sweep 回收对象时标记 Detached。</summary>
    internal void DetachByGc() => _state = DurableState.Detached;

    /// <summary>Compaction 时重新绑定 LocalId（不更改 Revision 所属关系）。</summary>
    internal void Rebind(LocalId newId) {
        Debug.Assert(!IsDetached, "Cannot rebind a detached object.");
        LocalId = newId;
    }

    /// <summary>如果对象已分离则抛出异常。</summary>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    protected void ThrowIfDetached() {
        if (_state == DurableState.Detached) { throw new ObjectDetachedException(LocalId); }
    }
}
