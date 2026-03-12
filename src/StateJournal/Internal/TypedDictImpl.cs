using System.Runtime.InteropServices;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal class TypedDictImpl<TKey, TValue, KHelper, VHelper> : DurableDict<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {
    internal TypedDictImpl() {
        _core = new();
    }

    public override UpsertStatus Upsert(TKey key, TValue? value) {
        ref TValue? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_core.Current, key, out bool exists);
        slot = value;
        _core.AfterUpsert<VHelper>(key, value);
        return exists ? UpsertStatus.Updated : UpsertStatus.Inserted;
    }

    public override bool Remove(TKey key) {
        if (!_core.Current.Remove(key, out TValue? removedValue)) { return false; }
        _core.AfterRemove<VHelper>(key, removedValue);
        return true;
    }

    public override void DiscardChanges() {
        _core.Revert<VHelper>();
    }

    private protected override ObjectKind DictObjectKind => ObjectKind.TypedDict;
    private protected override void CommitCore() => _core.Commit<VHelper>();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted<VHelper>();
    private protected override void WriteRebaseCore(IDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<KHelper, VHelper>(writer, context);
    private protected override void WriteDeltifyCore(IDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<KHelper, VHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<KHelper, VHelper>(ref reader);
}
