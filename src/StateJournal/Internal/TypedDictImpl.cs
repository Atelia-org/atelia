using System.Runtime.InteropServices;

namespace Atelia.StateJournal.Internal;

// AI TODO: 将DurableDict<TKey, TValue>改为abstract。用本类型中的_core字段具体实现DurableDict<TKey, TValue>。
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
        throw new NotImplementedException();
    }

    internal override void OnCommitSucceeded() {
        throw new NotImplementedException();
    }

    internal override void WritePendingDiff(IDiffWriter writer, DiffWriteContext context) {
        _core.WritePendingDiff<KHelper, VHelper>(writer, context);
    }
}
