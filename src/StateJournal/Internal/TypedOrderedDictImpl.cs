using Atelia.StateJournal.NodeContainers;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal sealed class TypedOrderedDictImpl<TKey, TValue, KHelper, VHelper> : DurableOrderedDict<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {

    private SkipListCore<TKey, TValue, KHelper, VHelper> _core = new();

    internal TypedOrderedDictImpl() { }

    #region DurableDictBase abstract hooks

    private protected override bool HasChangesCore => _core.HasChanges;
    private protected override uint EstimatedRebaseBytes => _core.EstimatedRebaseBytes();
    private protected override uint EstimatedDeltifyBytes => _core.EstimatedDeltifyBytes();

    internal override void FreezeCore(bool forceRebase) {
        if (forceRebase) {
            _core.FreezeFromCurrent();
        }
        else {
            _core.FreezeFromClean();
        }
    }

    private protected override void CommitCore() => _core.Commit();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted();
    private protected override void SyncFrozenCurrentFromCommittedCore() => _core.MaterializeFrozenFromReconstructedCommitted();
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta(ref reader);

    #endregion

    #region DurableOrderedDict API

    public override bool ContainsKey(TKey key) {
        ThrowIfDisposed();
        return _core.ContainsKey(key);
    }
    public override int Count {
        get {
            ThrowIfDisposed();
            return _core.Count;
        }
    }

    public override GetIssue Get(TKey key, out TValue? value) {
        ThrowIfDisposed();
        return _core.TryGet(key, out value) ? GetIssue.None : GetIssue.NotFound;
    }
    // value! : notnull 约束下 TValue? 仅是 NRT 注解；引用类型的 null 值在运行时被正确传递和存储。
    public override UpsertStatus Upsert(TKey key, TValue? value) {
        ThrowIfDetachedOrFrozen();
        return _core.Upsert(key, value!) ? UpsertStatus.Inserted : UpsertStatus.Updated;
    }

    public override bool Remove(TKey key) {
        ThrowIfDetachedOrFrozen();
        return _core.Remove(key);
    }

    public override IReadOnlyList<TKey> GetKeys() {
        ThrowIfDisposed();
        return _core.GetAllKeys();
    }
    public override List<KeyValuePair<TKey, TValue?>> ReadAscendingFrom(TKey minInclusive, int maxCount) {
        ThrowIfDisposed();
        return _core.ReadAscendingFrom(minInclusive, maxCount)!;
    }

    #endregion

    internal override void DiscardChanges() {
        ThrowIfPendingObjectMapRegistration();
        if (IsFrozen) {
            ThrowIfCannotDiscardFrozenChanges();
            _core.UnfreezeToMutableClean();
            ClearDiscardedFreeze();
            return;
        }
        _core.Revert();
    }

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        _core.AcceptChildRefVisitor(Revision, ref visitor);
    }

    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? _) {
        // 对 typed ordered dict，typed Symbol key/value 在 ApplyDelta 期间已通过
        // BinaryDiffReader.BareSymbol(...) 物化为 Symbol facade。
        // 因此这里不需要像 mixed 容器那样再验证 surviving SymbolId 是否仍在 symbolPool 中；
        // load 后校验职责仅剩 placeholder 残留检查。
        if (tracker is null) { return null; }
        return _core.ValidateReconstructed(tracker, "TypedOrderedDict");
    }
}
