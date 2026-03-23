using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal class MixedDequeImpl : DurableDeque {
    private int _durableRefCount;

    internal MixedDequeImpl() {
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
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<ValueBoxHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<ValueBoxHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<ValueBoxHelper>(ref reader);

    private protected override void OnCurrentValueRemoved(ValueBox removedValue) {
        if (removedValue.IsDurableRef) { _durableRefCount--; }
    }

    private protected override void OnCurrentValueUpserted(ValueBox oldValue, ValueBox newValue, bool existed) {
        if (existed && oldValue.IsDurableRef) { _durableRefCount--; }
        if (newValue.IsDurableRef) { _durableRefCount++; }
    }

    internal override bool AcceptChildRefRewrite<TRewriter>(ref TRewriter rewriter) {
        if (_durableRefCount == 0) { return false; }

        bool changed = false;
        _core.Current.GetSegments(out int firstStartIndex, out Span<ValueBox> first, out int secondStartIndex, out Span<ValueBox> second);
        changed |= RewriteSegment(ref rewriter, firstStartIndex, first);
        changed |= RewriteSegment(ref rewriter, secondStartIndex, second);
        return changed;
    }

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        if (_durableRefCount == 0) { return; }

        _core.Current.GetSegments(out Span<ValueBox> first, out Span<ValueBox> second);
        VisitSegment(ref visitor, first);
        VisitSegment(ref visitor, second);
    }

    private void RecountDurableRefs() {
        int count = 0;
        _core.Current.GetSegments(out Span<ValueBox> first, out Span<ValueBox> second);
        count += CountDurableRefs(first);
        count += CountDurableRefs(second);
        _durableRefCount = count;
    }

    private bool RewriteSegment<TRewriter>(ref TRewriter rewriter, int startIndex, Span<ValueBox> segment)
        where TRewriter : IChildRefRewriter, allows ref struct {
        bool changed = false;
        for (int i = 0; i < segment.Length; i++) {
            ref ValueBox slot = ref segment[i];
            var box = slot;
            if (!box.IsDurableRef) { continue; }

            var oldId = box.GetDurRefId();
            var newId = rewriter.Rewrite(oldId);
            if (newId == oldId) { continue; }

            var newRef = new DurableRef(box.GetDurRefKind(), newId);
            ValueBox oldValue = slot;
            if (!ValueBox.DurableRefFace.UpdateOrInit(ref slot, newRef)) { continue; }

            OnCurrentValueUpserted(oldValue, slot, existed: true);
            _core.AfterSet<ValueBoxHelper>(startIndex + i, ref slot);
            changed = true;
        }

        return changed;
    }

    private static void VisitSegment<TVisitor>(ref TVisitor visitor, Span<ValueBox> segment)
        where TVisitor : IChildRefVisitor, allows ref struct {
        foreach (var box in segment) {
            if (box.IsDurableRef) {
                visitor.Visit(box.GetDurRefId());
            }
        }
    }

    private static int CountDurableRefs(Span<ValueBox> segment) {
        int count = 0;
        foreach (var box in segment) {
            if (box.IsDurableRef) { count++; }
        }
        return count;
    }
}
