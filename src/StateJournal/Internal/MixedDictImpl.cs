namespace Atelia.StateJournal.Internal;

// AI TODO: 将DurableDict<TKey>改为abstract。用本类型中的_core字段具体实现DurableDict<TKey>。
internal class MixedDictImpl<TKey, KHelper> : DurableDict<TKey>
    where TKey : notnull
    where KHelper : unmanaged, ITypeHelper<TKey> {
    public override ValueKind Kind => ValueKind.MixedDict;
    internal MixedDictImpl() {
        _core = new();
    }

    public override void DiscardChanges() {
        throw new NotImplementedException();
    }
    internal override void OnCommitSucceeded() {
        throw new NotImplementedException();
    }


    internal override void WritePendingDiff(IDiffWriter writer) => _core.WritePendingDiff<KHelper, ValueBoxHelper>(writer);
}
