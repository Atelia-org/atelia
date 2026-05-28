using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal sealed class TypedHashSetImpl<T, THelper> : DurableHashSet<T>
    where T : notnull
    where THelper : unmanaged, ITypeHelper<T> {
    private SetChangeTracker<T> _core;

    internal TypedHashSetImpl() {
        _core = new();
    }

    public override bool HasChanges => _core.HasChanges;
    public override int Count => _core.Count;
    public override IReadOnlyCollection<T> Items => _core.Items;

    public override bool Contains(T value) {
        ArgumentNullException.ThrowIfNull(value);
        return _core.Contains<THelper>(value);
    }

    public override bool Add(T value) {
        ThrowIfDetachedOrFrozen();
        ArgumentNullException.ThrowIfNull(value);
        return _core.Add<THelper>(value);
    }

    public override bool Remove(T value) {
        ThrowIfDetachedOrFrozen();
        ArgumentNullException.ThrowIfNull(value);
        return _core.Remove<THelper>(value);
    }

    internal override void DiscardChanges() {
        ThrowIfPendingObjectMapRegistration();
        if (IsFrozen) {
            ThrowIfCannotDiscardFrozenChanges();
            _core.UnfreezeToMutableClean<THelper>();
            ClearDiscardedFreeze();
            return;
        }

        _core.Revert<THelper>();
    }

    internal override DurableObject ForkAsMutableCore() {
        var fork = new TypedHashSetImpl<T, THelper>();
        fork._core = _core.ForkMutableFromCommitted<THelper>();
        fork._versionStatus = _versionStatus.ForkForNewObject();
        return fork;
    }

    internal override void FreezeCore(bool forceRebase) {
        if (forceRebase) {
            _core.FreezeFromCurrent<THelper>();
        }
        else {
            _core.FreezeFromClean<THelper>();
        }
    }

    private protected override uint EstimatedRebaseBytes => _core.EstimatedRebaseBytes<THelper>();
    private protected override uint EstimatedDeltifyBytes => _core.EstimatedDeltifyBytes<THelper>();
    private protected override void CommitCore() => _core.Commit<THelper>();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted<THelper>();
    private protected override void SyncFrozenCurrentFromCommittedCore() => _core.MaterializeFrozenFromReconstructedCommitted<THelper>();
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<THelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<THelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<THelper>(ref reader);

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        if (!THelper.NeedVisitChildRefs) { return; }

        foreach (T item in _core.Current) {
            THelper.VisitChildRefs(item, Revision, ref visitor);
        }
    }

    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? symbolPool) {
        if (tracker is null || !THelper.NeedValidateReconstructed) { return null; }

        foreach (T item in _core.ReconstructedOrCurrent) {
            if (THelper.ValidateReconstructed(item, tracker, "TypedHashSet") is { } error) { return error; }
        }

        return null;
    }
}
