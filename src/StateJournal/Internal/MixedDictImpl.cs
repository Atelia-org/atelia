using System.Diagnostics;
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

    private protected override uint EstimatedRebaseBytes => _core.EstimatedRebaseBytes<KHelper, ValueBoxHelper>();
    private protected override uint EstimatedDeltifyBytes => _core.EstimatedDeltifyBytes<KHelper, ValueBoxHelper>();
    private protected override uint EstimateKeyBareBytes(TKey key) => KHelper.EstimateBareSize(key, asKey: true);

    internal override void DiscardChanges() {
        ThrowIfPendingObjectMapRegistration();
        if (IsFrozen) {
            ThrowIfCannotDiscardFrozenChanges();
            _core.UnfreezeToMutableClean<ValueBoxHelper>();
            ClearDiscardedFreeze();
            RecountRefs();
            return;
        }
        _core.Revert<ValueBoxHelper>();
        RecountRefs();
    }

    internal override DurableObject ForkAsMutableCore() {
        var fork = new MixedDictImpl<TKey, KHelper>();
        fork._core = _core.ForkMutableFromCommitted<KHelper, ValueBoxHelper>();
        fork._versionStatus = _versionStatus.ForkForNewObject();
        fork.RecountRefs();
        return fork;
    }

    internal override void FreezeCore(bool forceRebase) {
        if (forceRebase) {
            _core.FreezeFromCurrent<ValueBoxHelper>();
        }
        else {
            _core.FreezeFromClean<ValueBoxHelper>();
        }
        RecountRefs();
    }

    private protected override void CommitCore() => _core.Commit<ValueBoxHelper>();
    private protected override void SyncCurrentFromCommittedCore() {
        _core.SyncCurrentFromCommitted<ValueBoxHelper>();
        RecountRefs();
    }
    private protected override void SyncFrozenCurrentFromCommittedCore() {
        _core.MaterializeFrozenFromReconstructedCommitted<ValueBoxHelper>();
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

    /// <remarks>
    /// refcount 短路的时序安全性——同 MixedDequeImpl.AcceptChildRefVisitor 注释。
    /// </remarks>
    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        AssertRefCountConsistency();
        if (KHelper.NeedVisitChildRefs) {
            foreach (var key in _core.Current.Keys) {
                KHelper.VisitChildRefs(key, Revision, ref visitor);
            }
        }
        if (_durableRefCount == 0 && _symbolRefCount == 0) { return; }
        foreach (var box in _core.Current.Values) {
            if (box.IsDurableRef) { visitor.Visit(box.GetDurRefId()); }
            else if (box.IsSymbolRef) { visitor.Visit(box.DecodeSymbolId()); }
        }
    }

    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? symbolPool) {
        if (tracker is not null && KHelper.NeedValidateReconstructed) {
            foreach (var pair in _core.ReconstructedOrCurrent) {
                if (KHelper.ValidateReconstructed(pair.Key, tracker, "MixedDict") is { } keyError) { return keyError; }
            }
        }

        if (symbolPool is not null) {
            foreach (var pair in _core.ReconstructedOrCurrent) {
                if (ValueBox.ValidateReconstructedMixedSymbol(pair.Value, symbolPool, "MixedDict") is { } symbolError) {
                    return symbolError;
                }
            }
        }

        return null;
    }

    private void RecountRefs() => (_durableRefCount, _symbolRefCount) = ComputeRefCounts();

    private (int dur, int sym) ComputeRefCounts() {
        int durCount = 0, symCount = 0;
        foreach (var box in _core.Current.Values) {
            if (box.IsDurableRef) { ++durCount; }
            else if (box.IsSymbolRef) { ++symCount; }
        }
        return (durCount, symCount);
    }

    [Conditional("DEBUG")]
    private void AssertRefCountConsistency() {
        var (durCount, symCount) = ComputeRefCounts();
        Debug.Assert(_durableRefCount == durCount && _symbolRefCount == symCount,
            $"MixedDict refcount drift: durable={_durableRefCount}(expect {durCount}), symbol={_symbolRefCount}(expect {symCount})");
    }
}
