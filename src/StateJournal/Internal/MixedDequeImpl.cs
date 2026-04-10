using System.Diagnostics;
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

    /// <remarks>
    /// 此方法的 refcount 短路依赖 <c>_symbolRefCount/_durableRefCount</c> 在调用前已正确。
    /// 两条调用路径的时序安全性：
    /// <list type="bullet">
    ///   <item>Commit WalkAndMark — 运行时修改经过 OnCurrentValueUpserted/Removed，计数实时准确。</item>
    ///   <item>Open ValidateAllReferences — OnLoadCompleted（含 RecountRefs）已在 VersionChain.Load 里执行完毕。</item>
    /// </list>
    /// load 路径上 ValidateReconstructed 在 OnLoadCompleted <b>之前</b>调用，此时计数尚未重建，
    /// 因此 ValidateReconstructed 不能依赖 refcount 短路（已在该方法中独立处理）。
    /// </remarks>
    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        AssertRefCountConsistency();
        if (_durableRefCount == 0 && _symbolRefCount == 0) { return; }

        _core.Current.GetSegments(out Span<ValueBox> first, out Span<ValueBox> second);
        VisitSegment(ref visitor, first);
        VisitSegment(ref visitor, second);
    }

    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? symbolPool) {
        if (symbolPool is null) { return null; }

        _core.Current.GetSegments(out Span<ValueBox> first, out Span<ValueBox> second);
        return ValidateSymbolSegment(first, symbolPool) ?? ValidateSymbolSegment(second, symbolPool);
    }

    private void RecountRefs() => (_durableRefCount, _symbolRefCount) = ComputeRefCounts();

    private (int dur, int sym) ComputeRefCounts() {
        int durCount = 0, symCount = 0;
        _core.Current.GetSegments(out Span<ValueBox> first, out Span<ValueBox> second);
        CountRefs(first, ref durCount, ref symCount);
        CountRefs(second, ref durCount, ref symCount);
        return (durCount, symCount);
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
            if (ValueBox.ValidateReconstructedMixedSymbol(box, symbolPool, "MixedDeque") is { } error) { return error; }
        }
        return null;
    }

    [Conditional("DEBUG")]
    private void AssertRefCountConsistency() {
        var (durCount, symCount) = ComputeRefCounts();
        Debug.Assert(_durableRefCount == durCount && _symbolRefCount == symCount,
            $"MixedDeque refcount drift: durable={_durableRefCount}(expect {durCount}), symbol={_symbolRefCount}(expect {symCount})");
    }

    private static void CountRefs(Span<ValueBox> segment, ref int durCount, ref int symCount) {
        foreach (var box in segment) {
            if (box.IsDurableRef) { durCount++; }
            else if (box.IsSymbolRef) { symCount++; }
        }
    }
}
