using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal class MixedDictImpl<TKey, KHelper> : DurableDict<TKey>
    where TKey : notnull
    where KHelper : unmanaged, ITypeHelper<TKey> {
    internal MixedDictImpl() {
        _core = new();
    }

    public override void DiscardChanges() {
        _core.Revert<ValueBoxHelper>();
    }

    private protected override void CommitCore() => _core.Commit<ValueBoxHelper>();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted<ValueBoxHelper>();
    private protected override void WriteRebaseCore(IDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<KHelper, ValueBoxHelper>(writer, context);
    private protected override void WriteDeltifyCore(IDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<KHelper, ValueBoxHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<KHelper, ValueBoxHelper>(ref reader);
}
