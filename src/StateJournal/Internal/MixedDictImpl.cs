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
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<KHelper, ValueBoxHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<KHelper, ValueBoxHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<KHelper, ValueBoxHelper>(ref reader);

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        foreach (var box in _core.Current.Values) {
            if (!box.IsNull && box.GetLzc() == BoxLzc.DurableRef) {
                visitor.Visit(box.GetDurRefId());
            }
        }
    }
}
