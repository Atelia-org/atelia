using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// <see cref="DurableDict{TKey, TValue}"/> 的 DurableObject 值类型专用实现。
/// 内部以 <see cref="LocalId"/> 存储引用，对外通过 <see cref="DurableDict{TKey, TValue}"/>
/// 提供 <typeparamref name="TDurObj"/> 的读写接口。
/// Get 时委托 <see cref="DurableObject.Revision"/> 按需加载实例。
/// </summary>
internal class DurObjDictImpl<TKey, TDurObj, KHelper> : DurableDict<TKey, TDurObj>
    where TKey : notnull
    where TDurObj : DurableObject
    where KHelper : unmanaged, ITypeHelper<TKey> {

    private DictChangeTracker<TKey, LocalId> _core;

    internal DurObjDictImpl() {
        _core = new();
    }

    #region DurableDictBase abstract properties

    public override bool HasChanges => _core.HasChanges;
    private protected override uint EstimatedRebaseBytes => _core.EstimatedRebaseBytes<KHelper, LocalIdAsRefHelper>();
    private protected override uint EstimatedDeltifyBytes => _core.EstimatedDeltifyBytes<KHelper, LocalIdAsRefHelper>();

    #endregion

    #region DurableObject

    internal override void DiscardChanges() {
        ThrowIfPendingObjectMapRegistration();
        if (IsFrozen) {
            ThrowIfCannotDiscardFrozenChanges();
            _core.UnfreezeToMutableClean<LocalIdAsRefHelper>();
            ClearDiscardedFreeze();
            return;
        }
        _core.Revert<LocalIdAsRefHelper>();
    }

    internal override DurableObject ForkAsMutableCore() {
        var fork = new DurObjDictImpl<TKey, TDurObj, KHelper>();
        fork._core = _core.ForkMutableFromCommitted<KHelper, LocalIdAsRefHelper>();
        fork._versionStatus = _versionStatus.ForkForNewObject();
        return fork;
    }

    internal override void FreezeCore(bool forceRebase) {
        if (forceRebase) {
            _core.FreezeFromCurrent<LocalIdAsRefHelper>();
        }
        else {
            _core.FreezeFromClean<LocalIdAsRefHelper>();
        }
    }

    #endregion

    #region IDict<TKey>

    public override bool ContainsKey(TKey key) => _core.Current.ContainsKey(key);

    public override int Count => _core.Current.Count;

    public override IEnumerable<TKey> Keys => _core.Current.Keys;

    internal override IReadOnlyCollection<TKey> CommittedKeys => _core.CommittedKeys;

    public override bool Remove(TKey key) {
        ThrowIfDetachedOrFrozen();
        if (!_core.Current.Remove(key, out var removedId)) { return false; }
        _core.AfterRemove<LocalIdAsRefHelper>(key, removedId, KHelper.EstimateBareSize(key, asKey: true));
        return true;
    }

    #endregion

    #region IDict<TKey, TDurObj>

    public override GetIssue Get(TKey key, out TDurObj? value) {
        value = null;
        if (!_core.Current.TryGetValue(key, out var localId)) { return GetIssue.NotFound; }
        if (localId.IsNull) { return GetIssue.None; }

        var loadResult = Revision.Load(localId);
        if (loadResult.IsFailure) { return GetIssue.LoadFailed; }
        if (loadResult.Value is not TDurObj typed) { return GetIssue.LoadFailed; }

        value = typed;
        return GetIssue.None;
    }

    public override UpsertStatus Upsert(TKey key, TDurObj? value) {
        ThrowIfDetachedOrFrozen();
        if (value is not null) { Revision.EnsureCanReference(value); }
        var localId = value?.LocalId ?? LocalId.Null;
        return _core.Upsert<KHelper, LocalIdAsRefHelper>(key, localId);
    }

    #endregion

    #region Persistence Hooks

    private protected override void CommitCore() => _core.Commit<LocalIdAsRefHelper>();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted<LocalIdAsRefHelper>();
    private protected override void SyncFrozenCurrentFromCommittedCore() => _core.MaterializeFrozenFromReconstructedCommitted<LocalIdAsRefHelper>();
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<KHelper, LocalIdAsRefHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<KHelper, LocalIdAsRefHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<KHelper, LocalIdAsRefHelper>(ref reader);

    #endregion

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        if (KHelper.NeedVisitChildRefs) {
            foreach (var key in _core.Current.Keys) {
                KHelper.VisitChildRefs(key, Revision, ref visitor);
            }
        }

        foreach (var localId in _core.Current.Values) {
            if (!localId.IsNull) { visitor.Visit(localId); }
        }
    }

    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? symbolPool) {
        if (tracker is null || !KHelper.NeedValidateReconstructed) { return null; }
        foreach (var pair in _core.ReconstructedOrCurrent) {
            if (KHelper.ValidateReconstructed(pair.Key, tracker, "DurObjDict") is { } error) { return error; }
        }
        return null;
    }
}
