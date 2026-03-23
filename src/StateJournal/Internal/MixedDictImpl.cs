using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal class MixedDictImpl<TKey, KHelper> : DurableDict<TKey>
    where TKey : notnull
    where KHelper : unmanaged, ITypeHelper<TKey> {
    private int _durableRefCount;

    internal MixedDictImpl() {
        _core = new();
    }

    internal override void DiscardChanges() {
        _core.Revert<ValueBoxHelper>();
        RecountDurableRefs();
    }

    private protected override void CommitCore() => _core.Commit<ValueBoxHelper>();
    private protected override void SyncCurrentFromCommittedCore() {
        _core.SyncCurrentFromCommitted<ValueBoxHelper>();
        RecountDurableRefs();
    }
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<KHelper, ValueBoxHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<KHelper, ValueBoxHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<KHelper, ValueBoxHelper>(ref reader);

    private protected override void OnCurrentValueRemoved(ValueBox removedValue) {
        if (removedValue.IsDurableRef) { _durableRefCount--; }
    }

    private protected override void OnCurrentValueUpserted(ValueBox oldValue, ValueBox newValue, bool existed) {
        if (existed && oldValue.IsDurableRef) { _durableRefCount--; }
        if (newValue.IsDurableRef) { _durableRefCount++; }
    }

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        if (_durableRefCount == 0) { return; }
        foreach (var box in _core.Current.Values) {
            if (box.IsDurableRef) {
                visitor.Visit(box.GetDurRefId());
            }
        }
    }

    internal override bool AcceptChildRefRewrite<TRewriter>(ref TRewriter rewriter) {
        if (_durableRefCount == 0) { return false; }
        bool changed = false;
        var keys = new List<TKey>(_core.Current.Count);
        foreach (var kvp in _core.Current) {
            if (kvp.Value.IsDurableRef) {
                keys.Add(kvp.Key);
            }
        }
        foreach (var key in keys) {
            var box = _core.Current[key];
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
        return changed;
    }

    private void RecountDurableRefs() {
        int count = 0;
        foreach (var box in _core.Current.Values) {
            if (box.IsDurableRef) { ++count; }
        }
        _durableRefCount = count;
    }
}
