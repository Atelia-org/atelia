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

    public override bool HasChanges => _core.HasChanges;
    private protected override int RebaseCount => _core.RebaseCount;
    private protected override int DeltifyCount => _core.DeltifyCount;

    private protected override void CommitCore() => _core.Commit();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted();
    private protected override void SyncFrozenCurrentFromCommittedCore() => throw new InvalidDataException("Frozen OrderedDict is not supported by this implementation.");
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta(ref reader);

    #endregion

    #region DurableOrderedDict API

    public override bool ContainsKey(TKey key) => _core.ContainsKey(key);
    public override int Count => _core.Count;

    public override GetIssue Get(TKey key, out TValue? value) =>
        _core.TryGet(key, out value) ? GetIssue.None : GetIssue.NotFound;
    // value! : notnull 约束下 TValue? 仅是 NRT 注解；引用类型的 null 值在运行时被正确传递和存储。
    public override UpsertStatus Upsert(TKey key, TValue? value) =>
        _core.Upsert(key, value!) ? UpsertStatus.Inserted : UpsertStatus.Updated;

    public override bool Remove(TKey key) => _core.Remove(key);

    public override IReadOnlyList<TKey> GetKeys() => _core.GetAllKeys();
    public override List<KeyValuePair<TKey, TValue?>> ReadAscendingFrom(TKey minInclusive, int maxCount) =>
        _core.ReadAscendingFrom(minInclusive, maxCount)!;

    #endregion

    internal override void DiscardChanges() => _core.Revert();

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        _core.AcceptChildRefVisitor(Revision, ref visitor);
    }

    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? _) {
        // 对 typed ordered dict，typed string key/value 在 ApplyDelta 期间已通过
        // BinaryDiffReader.BareSymbolId(...) 物化为 string facade。
        // 因此这里不需要像 mixed 容器那样再验证 surviving SymbolId 是否仍在 symbolPool 中；
        // load 后校验职责仅剩 placeholder 残留检查。
        if (tracker is null) { return null; }
        return _core.ValidateReconstructed(tracker, "TypedOrderedDict");
    }
}
