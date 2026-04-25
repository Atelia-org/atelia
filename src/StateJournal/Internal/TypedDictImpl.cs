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
    private protected override int RebaseCount => _core.RebaseCount;
    private protected override int DeltifyCount => _core.DeltifyCount;

    #endregion

    #region IDict<TKey>

    public override bool ContainsKey(TKey key) => _core.Current.ContainsKey(key);
    public override int Count => _core.Current.Count;
    public override IEnumerable<TKey> Keys => _core.Current.Keys;
    internal override IReadOnlyCollection<TKey> CommittedKeys => _core.CommittedKeys;

    public override bool Remove(TKey key) {
        if (!_core.Current.Remove(key, out TValue? removedValue)) { return false; }
        _core.AfterRemove<VHelper>(key, removedValue);
        return true;
    }

    #endregion

    #region IDict<TKey, TValue>

    public override GetIssue Get(TKey key, out TValue? value) {
        return _core.Current.TryGetValue(key, out value)
            ? GetIssue.None
            : GetIssue.NotFound;
    }

    public override UpsertStatus Upsert(TKey key, TValue? value) => _core.Upsert<VHelper>(key, value);

    #endregion

    internal override void DiscardChanges() {
        ThrowIfPendingObjectMapRegistration();
        _core.Revert<VHelper>();
    }

    internal override DurableObject ForkAsMutableCore() {
        var fork = new TypedDictImpl<TKey, TValue, KHelper, VHelper>();
        fork._core = _core.ForkMutableFromCommitted<KHelper, VHelper>();
        fork._versionStatus = _versionStatus.ForkForNewObject();
        return fork;
    }

    private protected override void CommitCore() => _core.Commit<VHelper>();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted<VHelper>();
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
            foreach (var key in _core.Current.Keys) {
                if (KHelper.ValidateReconstructed(key, tracker, "TypedDict") is { } keyError) { return keyError; }
            }
        }

        if (VHelper.NeedValidateReconstructed) {
            foreach (var value in _core.Current.Values) {
                if (VHelper.ValidateReconstructed(value, tracker, "TypedDict") is { } valueError) { return valueError; }
            }
        }

        return null;
    }
}
