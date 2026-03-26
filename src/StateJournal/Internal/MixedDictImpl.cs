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

    internal override bool AcceptChildRefRewrite<TRewriter>(ref TRewriter rewriter) {
        if (_durableRefCount == 0 && _symbolRefCount == 0) { return false; }
        bool changed = false;
        var keys = new List<TKey>(_core.Current.Count);
        foreach (var kvp in _core.Current) {
            if (kvp.Value.IsDurableRef || kvp.Value.IsSymbolRef) {
                keys.Add(kvp.Key);
            }
        }
        foreach (var key in keys) {
            var box = _core.Current[key];
            if (box.IsDurableRef) {
                var oldId = box.GetDurRefId();
                var newId = rewriter.Rewrite(oldId);
                if (newId != oldId) {
                    var newRef = new DurableRef(box.GetDurRefKind(), newId);
                    var newBox = ValueBox.DurableRefFace.From(newRef);
                    _core.Current[key] = newBox;
                    _core.AfterUpsert<ValueBoxHelper>(key, newBox);
                    changed = true;
                }
            }
            else if (box.IsSymbolRef) {
                var oldId = box.DecodeSymbolId();
                var newId = rewriter.Rewrite(oldId);
                if (newId != oldId) {
                    var newBox = ValueBox.FromSymbolId(newId);
                    _core.Current[key] = newBox;
                    _core.AfterUpsert<ValueBoxHelper>(key, newBox);
                    changed = true;
                }
            }
        }
        return changed;
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
