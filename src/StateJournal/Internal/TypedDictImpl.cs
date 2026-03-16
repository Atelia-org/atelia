using System.Runtime.InteropServices;
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

    public override UpsertStatus Upsert(TKey key, TValue? value) {
        ref TValue? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_core.Current, key, out bool exists);
        slot = value;
        _core.AfterUpsert<VHelper>(key, value);
        return exists ? UpsertStatus.Updated : UpsertStatus.Inserted;
    }

    #endregion

    internal override void DiscardChanges() {
        _core.Revert<VHelper>();
    }

    private protected override void CommitCore() => _core.Commit<VHelper>();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted<VHelper>();
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<KHelper, VHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<KHelper, VHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<KHelper, VHelper>(ref reader);

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) { }

    internal override void AcceptChildRefRewrite<TRewriter>(ref TRewriter rewriter) { }
}
