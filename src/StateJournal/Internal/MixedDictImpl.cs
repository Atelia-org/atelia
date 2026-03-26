using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal class MixedDictImpl<TKey, KHelper> : DurableDict<TKey>
    where TKey : notnull
    where KHelper : unmanaged, ITypeHelper<TKey> {
    private int _durableRefCount;
    private int _symbolRefCount;

    internal MixedDictImpl() {
        _core = new();
    }

    internal override void DiscardChanges() {
        _core.Revert<ValueBoxHelper>();
        RecountRefs();
    }

    private protected override void CommitCore() => _core.Commit<ValueBoxHelper>();
    private protected override void SyncCurrentFromCommittedCore() {
        _core.SyncCurrentFromCommitted<ValueBoxHelper>();
        RecountRefs();
    }
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<KHelper, ValueBoxHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<KHelper, ValueBoxHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<KHelper, ValueBoxHelper>(ref reader);

    private protected override void OnCurrentValueRemoved(ValueBox removedValue) {
        if (removedValue.IsDurableRef) { _durableRefCount--; }
        else if (removedValue.IsSymbolRef) { _symbolRefCount--; }
    }

    private protected override void OnCurrentValueUpserted(ValueBox oldValue, ValueBox newValue, bool existed) {
        if (existed) {
            if (oldValue.IsDurableRef) { _durableRefCount--; }
            else if (oldValue.IsSymbolRef) { _symbolRefCount--; }
        }
        if (newValue.IsDurableRef) { _durableRefCount++; }
        else if (newValue.IsSymbolRef) { _symbolRefCount++; }
    }

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        if (_durableRefCount == 0 && _symbolRefCount == 0) { return; }
        foreach (var box in _core.Current.Values) {
            if (box.IsDurableRef) { visitor.Visit(box.GetDurRefId()); }
            else if (box.IsSymbolRef) { visitor.Visit(box.DecodeSymbolId()); }
        }
    }

    private void RecountRefs() {
        int durCount = 0, symCount = 0;
        foreach (var box in _core.Current.Values) {
            if (box.IsDurableRef) { ++durCount; }
            else if (box.IsSymbolRef) { ++symCount; }
        }
        _durableRefCount = durCount;
        _symbolRefCount = symCount;
    }
}
