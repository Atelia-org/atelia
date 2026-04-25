using Atelia.Data;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal;

public abstract class DurableObject {
    public abstract DurableObjectKind Kind { get; }

    DurableState _state;
    Revision? _revision;
    bool _pendingObjectMapRegistration;
    bool _isFrozen;
    bool _mutabilityDirty;
    bool _forceRebaseForFrozenSnapshot;

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

    public bool IsFrozen => _isFrozen;

    /// <inheritdoc/>
    public abstract bool HasChanges { get; }

    internal bool HasPersistenceChanges => HasChanges || _mutabilityDirty || _pendingObjectMapRegistration;

    internal bool HasMutabilityChanges => _mutabilityDirty;

    internal bool ForceRebaseForFrozenSnapshot => _forceRebaseForFrozenSnapshot;

    /// <returns>RBF frame tag</returns>
    internal abstract FrameTag WritePendingDiff(BinaryDiffWriter writer, ref DiffWriteContext context);

    private protected abstract ReadOnlySpan<byte> TypeCode { get; }

    internal abstract SizedPtr HeadTicket { get; }
    internal abstract bool IsTracked { get; }

    internal bool HasPendingObjectMapRegistration => _pendingObjectMapRegistration;

    internal void MarkPendingObjectMapRegistration() => _pendingObjectMapRegistration = true;

    internal void ClearPendingObjectMapRegistration() => _pendingObjectMapRegistration = false;

    internal abstract void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context);

    internal abstract void ApplyDelta(ref BinaryDiffReader reader, SizedPtr parentTicket);

    /// <summary>Load 完成后调用，修正 _previousVersion 并将 _committed 数据同步到 _current。</summary>
    internal abstract void OnLoadCompleted(SizedPtr versionTicket);

    internal abstract void DiscardChanges();

    internal virtual DurableObject ForkAsMutableCore() =>
        throw new NotSupportedException($"{GetType().Name} does not support ForkCommittedAsMutable.");

    public void Freeze() {
        ThrowIfDetached();
        if (_isFrozen) { return; }

        bool forceRebase = HasChanges || !IsTracked;
        FreezeCore(forceRebase);
        _isFrozen = true;
        _mutabilityDirty = CurrentObjectFlags != VersionObjectFlags;
        if (forceRebase) { _forceRebaseForFrozenSnapshot = true; }
    }

    internal virtual void FreezeCore(bool forceRebase) =>
        throw new NotSupportedException($"{GetType().Name} does not support Freeze.");

    protected void ThrowIfPendingObjectMapRegistration() {
        if (_pendingObjectMapRegistration) {
            throw new InvalidOperationException(
                "Cannot discard changes on a forked durable object before its ObjectMap registration has been committed."
            );
        }
    }

    protected void ThrowIfFrozen() {
        if (_isFrozen) { throw new ObjectFrozenException(LocalId); }
    }

    protected void ThrowIfDetachedOrFrozen() {
        ThrowIfDetached();
        ThrowIfFrozen();
    }

    protected bool CanDiscardCleanFreeze => _isFrozen && _mutabilityDirty && !_forceRebaseForFrozenSnapshot && IsTracked && !HasChanges;

    protected void ThrowIfCannotDiscardFrozenChanges() {
        if (!_isFrozen) { return; }
        if (CanDiscardCleanFreeze) { return; }

        throw new InvalidOperationException(
            "Cannot discard changes on a frozen durable object unless it is a clean tracked object whose uncommitted Freeze() can be reverted."
        );
    }

    protected void ClearDiscardedFreeze() {
        if (!CanDiscardCleanFreeze) { throw new InvalidOperationException("Current freeze state cannot be discarded."); }
        _isFrozen = false;
        _mutabilityDirty = false;
        _forceRebaseForFrozenSnapshot = false;
    }

    internal ObjectVersionFlags CurrentObjectFlags =>
        _isFrozen ? ObjectVersionFlags.Frozen : ObjectVersionFlags.None;

    internal virtual ObjectVersionFlags VersionObjectFlags => ObjectVersionFlags.None;

    internal void MarkMutabilityDirty() => _mutabilityDirty = true;

    internal void ApplyLoadedObjectFlags(ObjectVersionFlags objectFlags) {
        _isFrozen = (objectFlags & ObjectVersionFlags.Frozen) != 0;
        _mutabilityDirty = false;
        _forceRebaseForFrozenSnapshot = false;
    }

    internal void ClearCommittedPersistenceFlags() {
        _mutabilityDirty = false;
        _forceRebaseForFrozenSnapshot = false;
    }

    internal abstract void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) where TVisitor : IChildRefVisitor, allows ref struct;
    // load 历史回放完成后的局部收尾校验入口。
    // 目前既承接 typed string placeholder 残留校验，也承接 mixed 容器里的 SymbolId 完整性校验。
    internal virtual AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, StringPool? symbolPool) => null;

    /// <summary>设置对象状态。</summary>
    /// <param name="state">新状态。</param>
    protected void SetState(DurableState state) => _state = state;

    /// <summary>是否已绑定到指定 Revision（不会抛异常）。</summary>
    internal bool IsBoundTo(Revision revision) => ReferenceEquals(_revision, revision);
    internal Revision? BoundRevision => _revision;

    /// <summary>是否处于 Detached 状态。</summary>
    internal bool IsDetached => _state == DurableState.Detached;

    /// <summary>供 Revision 在 GC Sweep 回收对象时标记 Detached。</summary>
    internal void DetachByGc() => _state = DurableState.Detached;

    /// <summary>如果对象已分离则抛出异常。</summary>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    protected void ThrowIfDetached() {
        if (_state == DurableState.Detached) { throw new ObjectDetachedException(LocalId); }
    }
}
