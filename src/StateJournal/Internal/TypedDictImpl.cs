using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal class TypedDictImpl<TKey, TValue, KHelper, VHelper> : DurableDict<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {

    private DictChangeTracker<TKey, TValue> _core;

    internal TypedDictImpl() {
        _core = new();
    }

    #region DurableDictBase abstract properties

    public override bool HasChanges => _core.HasChanges;
    private protected override uint EstimatedRebaseBytes => _core.EstimatedRebaseBytes<KHelper, VHelper>();
    private protected override uint EstimatedDeltifyBytes => _core.EstimatedDeltifyBytes<KHelper, VHelper>();

    #endregion

    #region IDict<TKey>

    public override bool ContainsKey(TKey key) => _core.Current.ContainsKey(key);
    public override int Count => _core.Current.Count;
    public override IEnumerable<TKey> Keys => _core.Current.Keys;
    internal override IReadOnlyCollection<TKey> CommittedKeys => _core.CommittedKeys;

    public override bool Remove(TKey key) {
        ThrowIfDetachedOrFrozen();
        if (!_core.Current.TryGetValue(key, out TValue? removedValue)) { return false; }
        uint keyBareBytes = KHelper.EstimateBareSize(key, asKey: true);
        uint removedEntryBytes = checked(keyBareBytes + VHelper.EstimateBareSize(removedValue, asKey: false));
        bool removed = _core.Current.Remove(key);
        System.Diagnostics.Debug.Assert(removed);
        _core.AfterRemove<VHelper>(key, removedValue, removedEntryBytes, keyBareBytes);
        return true;
    }

    #endregion

    #region IDict<TKey, TValue>

    public override GetIssue Get(TKey key, out TValue? value) {
        return _core.Current.TryGetValue(key, out value)
            ? GetIssue.None
            : GetIssue.NotFound;
    }

    public override UpsertStatus Upsert(TKey key, TValue? value) {
        ThrowIfDetachedOrFrozen();
        return _core.Upsert<KHelper, VHelper>(key, value);
    }

    #endregion

    internal override void DiscardChanges() {
        ThrowIfPendingObjectMapRegistration();
        if (IsFrozen) {
            ThrowIfCannotDiscardFrozenChanges();
            _core.UnfreezeToMutableClean<VHelper>();
            ClearDiscardedFreeze();
            return;
        }
        _core.Revert<VHelper>();
    }

    internal override DurableObject ForkAsMutableCore() {
        var fork = new TypedDictImpl<TKey, TValue, KHelper, VHelper>();
        fork._core = _core.ForkMutableFromCommitted<KHelper, VHelper>();
        fork._versionStatus = _versionStatus.ForkForNewObject();
        return fork;
    }

    internal override void FreezeCore(bool forceRebase) {
        if (forceRebase) {
            _core.FreezeFromCurrent<VHelper>();
        }
        else {
            _core.FreezeFromClean<VHelper>();
        }
    }

    private protected override void CommitCore() => _core.Commit<VHelper>();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted<VHelper>();
    private protected override void SyncFrozenCurrentFromCommittedCore() => _core.MaterializeFrozenFromReconstructedCommitted<VHelper>();
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<KHelper, VHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<KHelper, VHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<KHelper, VHelper>(ref reader);

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        if (KHelper.NeedVisitChildRefs) {
            foreach (var key in _core.Current.Keys) {
                KHelper.VisitChildRefs(key, Revision, ref visitor);
            }
        }

        if (VHelper.NeedVisitChildRefs) {
            foreach (var value in _core.Current.Values) {
                VHelper.VisitChildRefs(value, Revision, ref visitor);
            }
        }
    }

    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? symbolPool) {
        if (tracker is null) { return null; }

        if (KHelper.NeedValidateReconstructed) {
            foreach (var pair in _core.ReconstructedOrCurrent) {
                if (KHelper.ValidateReconstructed(pair.Key, tracker, "TypedDict") is { } keyError) { return keyError; }
            }
        }

        if (VHelper.NeedValidateReconstructed) {
            foreach (var pair in _core.ReconstructedOrCurrent) {
                if (VHelper.ValidateReconstructed(pair.Value, tracker, "TypedDict") is { } valueError) { return valueError; }
            }
        }

        return null;
    }
}
