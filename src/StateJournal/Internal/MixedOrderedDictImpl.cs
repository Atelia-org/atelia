using System.Diagnostics;
using Atelia.StateJournal.NodeContainers;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal sealed class MixedOrderedDictImpl<TKey, KHelper> : DurableOrderedDict<TKey>
    where TKey : notnull
    where KHelper : unmanaged, ITypeHelper<TKey> {

    private SkipListCore<TKey, ValueBox, KHelper, ValueBoxHelper> _core = new();
    private int _durableRefCount;
    private int _symbolRefCount;

    internal MixedOrderedDictImpl() { }

    // ── DurableDictBase abstract hooks ──
    public override bool HasChanges => _core.HasChanges;
    private protected override uint EstimatedRebaseBytes => _core.EstimatedRebaseBytes();
    private protected override uint EstimatedDeltifyBytes => _core.EstimatedDeltifyBytes();

    private protected override void CommitCore() => _core.Commit();
    private protected override void SyncCurrentFromCommittedCore() {
        _core.SyncCurrentFromCommitted();
        RecountRefs();
    }
    private protected override void SyncFrozenCurrentFromCommittedCore() => throw new InvalidDataException("Frozen OrderedDict is not supported by this implementation.");
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) =>
        _core.WriteRebase(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) =>
        _core.WriteDeltify(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) =>
        _core.ApplyDelta(ref reader);

    internal override void DiscardChanges() {
        _core.Revert();
        RecountRefs();
    }

    // ── IDict<TKey> ──
    public override bool ContainsKey(TKey key) => _core.ContainsKey(key);
    public override int Count => _core.Count;
    public override bool Remove(TKey key) {
        if (!_core.TryRemove(key, out var oldBox)) return false;
        OnCurrentValueRemoved(oldBox);
        return true;
    }
    public override IEnumerable<TKey> Keys => _core.GetAllKeys();

    // ── Ordered operations ──
    public override IReadOnlyList<TKey> GetKeys() => _core.GetAllKeys();
    public override IReadOnlyList<TKey> GetKeysFrom(TKey minInclusive, int maxCount) =>
        _core.ReadKeysAscendingFrom(minInclusive, maxCount);

    // ── Core Impl ──
    private protected override GetIssue GetCore<TValue, VFace>(TKey key, out TValue value) {
        value = default!;
        if (!_core.TryGet(key, out var box)) return GetIssue.NotFound;
        return VFace.Get(box, out value!);
    }

#pragma warning disable CS8765 // notnull 约束下 override 不能写 TValue?，这是 C# 泛型 override 的已知限制。
    private protected override UpsertStatus UpsertCore<TValue, VFace>(TKey key, TValue value) {
#pragma warning restore CS8765
        ref ValueBox slot = ref _core.UpsertGetValueRef(key, out bool existed, out int slotIndex, out bool capturedNow);
        ValueBox oldValue = existed ? slot : default;
        if (!VFace.UpdateOrInit(ref slot, value)) {
            if (capturedNow) { _core.CancelPreparedValueUpdate(slotIndex); }
            return UpsertStatus.Updated;
        }
        _core.ConfirmValueDirty(slotIndex);
        OnCurrentValueUpserted(oldValue, slot, existed);
        return existed ? UpsertStatus.Updated : UpsertStatus.Inserted;
    }

    private protected override bool TryGetValueBox(TKey key, out ValueBox box) =>
        _core.TryGet(key, out box);

    // ── Ref counting hooks ──
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

    // ── ChildRefVisitor ──
    /// <remarks>
    /// refcount 短路的时序安全性——同 MixedDequeImpl.AcceptChildRefVisitor 注释。
    /// </remarks>
    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        AssertRefCountConsistency();
        bool needVisitKeys = KHelper.NeedVisitChildRefs;
        bool needVisitValues = _durableRefCount != 0 || _symbolRefCount != 0;
        if (!needVisitKeys && !needVisitValues) { return; }

        var cursor = _core.Head;
        while (cursor.IsNotNull) {
            var (key, box) = _core.GetEntry(ref cursor);
            if (needVisitKeys) {
                KHelper.VisitChildRefs(key, Revision, ref visitor);
            }
            if (needVisitValues) {
                if (box.IsDurableRef) { visitor.Visit(box.GetDurRefId()); }
                else if (box.IsSymbolRef) { visitor.Visit(box.DecodeSymbolId()); }
            }
            cursor = _core.GetNext(ref cursor);
        }
    }

    // ── ValidateReconstructed ──
    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? symbolPool) {
        bool needValidateKeys = tracker is not null && KHelper.NeedValidateReconstructed;
        bool needValidateSymbols = symbolPool is not null;
        if (!needValidateKeys && !needValidateSymbols) { return null; }

        var cursor = _core.Head;
        while (cursor.IsNotNull) {
            var (key, box) = _core.GetEntry(ref cursor);
            if (needValidateKeys) {
                if (KHelper.ValidateReconstructed(key, tracker!, "MixedOrderedDict") is { } keyError) {
                    return keyError;
                }
            }
            if (needValidateSymbols &&
                ValueBox.ValidateReconstructedMixedSymbol(box, symbolPool!, "MixedOrderedDict") is { } symbolError) {
                return symbolError;
            }
            cursor = _core.GetNext(ref cursor);
        }
        return null;
    }

    private void RecountRefs() => (_durableRefCount, _symbolRefCount) = ComputeRefCounts();

    private (int dur, int sym) ComputeRefCounts() {
        int durCount = 0, symCount = 0;
        var cursor = _core.Head;
        while (cursor.IsNotNull) {
            var (_, box) = _core.GetEntry(ref cursor);
            if (box.IsDurableRef) { ++durCount; }
            else if (box.IsSymbolRef) { ++symCount; }
            cursor = _core.GetNext(ref cursor);
        }
        return (durCount, symCount);
    }

    [Conditional("DEBUG")]
    private void AssertRefCountConsistency() {
        var (durCount, symCount) = ComputeRefCounts();
        Debug.Assert(_durableRefCount == durCount && _symbolRefCount == symCount,
            $"MixedOrderedDict refcount drift: durable={_durableRefCount}(expect {durCount}), symbol={_symbolRefCount}(expect {symCount})");
    }
}
