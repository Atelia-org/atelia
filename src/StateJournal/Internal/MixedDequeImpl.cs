using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal class MixedDequeImpl : DurableDeque {
    private int _durableRefCount;
    private int _symbolRefCount;

    internal MixedDequeImpl() {
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
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<ValueBoxHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<ValueBoxHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<ValueBoxHelper>(ref reader);

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

        _core.Current.GetSegments(out Span<ValueBox> first, out Span<ValueBox> second);
        VisitSegment(ref visitor, first);
        VisitSegment(ref visitor, second);
    }

    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? symbolPool) {
        if (symbolPool is null || _symbolRefCount == 0) { return null; }

        _core.Current.GetSegments(out Span<ValueBox> first, out Span<ValueBox> second);
        return ValidateSymbolSegment(first, symbolPool) ?? ValidateSymbolSegment(second, symbolPool);
    }

    private void RecountRefs() {
        int durCount = 0, symCount = 0;
        _core.Current.GetSegments(out Span<ValueBox> first, out Span<ValueBox> second);
        CountRefs(first, ref durCount, ref symCount);
        CountRefs(second, ref durCount, ref symCount);
        _durableRefCount = durCount;
        _symbolRefCount = symCount;
    }

    private static void VisitSegment<TVisitor>(ref TVisitor visitor, Span<ValueBox> segment)
        where TVisitor : IChildRefVisitor, allows ref struct {
        foreach (var box in segment) {
            if (box.IsDurableRef) { visitor.Visit(box.GetDurRefId()); }
            else if (box.IsSymbolRef) { visitor.Visit(box.DecodeSymbolId()); }
        }
    }

    private static AteliaError? ValidateSymbolSegment(Span<ValueBox> segment, Pools.StringPool symbolPool) {
        foreach (var box in segment) {
            if (!box.IsSymbolRef) { continue; }
            SymbolId symbolId = box.DecodeSymbolId();
            if (!symbolPool.Validate(symbolId.ToSlotHandle())) {
                return new SjCorruptionError(
                    $"MixedDeque load completed with a dangling SymbolId {symbolId.Value}.",
                    RecoveryHint: "The final SymbolTable is missing a string still referenced by the reconstructed object state."
                );
            }
        }
        return null;
    }

    private static void CountRefs(Span<ValueBox> segment, ref int durCount, ref int symCount) {
        foreach (var box in segment) {
            if (box.IsDurableRef) { durCount++; }
            else if (box.IsSymbolRef) { symCount++; }
        }
    }
}
