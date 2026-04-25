using Atelia.StateJournal.NodeContainers;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// <see cref="DurableOrderedDict{TKey, TValue}"/> 的 DurableObject 值类型特化实现。
/// 内部以 <see cref="LocalId"/> 存储引用，对外通过 <see cref="DurableOrderedDict{TKey, TValue}"/>
/// 提供 <typeparamref name="TDurObj"/> 的读写接口。
/// Get 时通过 <see cref="Revision.Load(LocalId)"/> 懒加载实例。
/// </summary>
internal sealed class DurObjOrderedDictImpl<TKey, TDurObj, KHelper> : DurableOrderedDict<TKey, TDurObj>
    where TKey : notnull
    where TDurObj : DurableObject
    where KHelper : unmanaged, ITypeHelper<TKey> {

    private SkipListCore<TKey, LocalId, KHelper, LocalIdAsRefHelper> _core = new();

    internal DurObjOrderedDictImpl() { }

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

    public override GetIssue Get(TKey key, out TDurObj? value) {
        value = null;
        if (!_core.TryGet(key, out var localId)) { return GetIssue.NotFound; }
        if (localId.IsNull) { return GetIssue.None; }

        var loadResult = Revision.Load(localId);
        if (loadResult.IsFailure) { return GetIssue.LoadFailed; }
        if (loadResult.Value is not TDurObj typed) { return GetIssue.LoadFailed; }

        value = typed;
        return GetIssue.None;
    }

    public override UpsertStatus Upsert(TKey key, TDurObj? value) {
        if (value is not null) { Revision.EnsureCanReference(value); }
        var localId = value?.LocalId ?? LocalId.Null;
        bool inserted = _core.Upsert(key, localId);
        return inserted ? UpsertStatus.Inserted : UpsertStatus.Updated;
    }

    public override bool Remove(TKey key) => _core.Remove(key);

    public override IReadOnlyList<TKey> GetKeys() => _core.GetAllKeys();

    public override List<KeyValuePair<TKey, TDurObj?>> ReadAscendingFrom(TKey minInclusive, int maxCount) {
        var raw = _core.ReadAscendingFrom(minInclusive, maxCount);
        var result = new List<KeyValuePair<TKey, TDurObj?>>(raw.Count);
        foreach (var pair in raw) {
            if (pair.Value.IsNull) {
                result.Add(new KeyValuePair<TKey, TDurObj?>(pair.Key, null));
                continue;
            }
            var loadResult = Revision.Load(pair.Value);
            if (loadResult.IsFailure) {
                throw new InvalidOperationException(
                    $"DurObjOrderedDict range scan: failed to load LocalId {pair.Value} for key '{pair.Key}'."
                );
            }
            if (loadResult.Value is not TDurObj typed) {
                throw new InvalidOperationException(
                    $"DurObjOrderedDict range scan: LocalId {pair.Value} for key '{pair.Key}' resolved to {loadResult.Value?.GetType().Name ?? "null"}, expected {typeof(TDurObj).Name}."
                );
            }
            result.Add(new KeyValuePair<TKey, TDurObj?>(pair.Key, typed));
        }
        return result;
    }

    #endregion

    internal override void DiscardChanges() => _core.Revert();

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        _core.AcceptChildRefVisitor(Revision, ref visitor);
    }

    internal override AteliaError? ValidateReconstructed(LoadPlaceholderTracker? tracker, Pools.StringPool? _) {
        if (tracker is null || !KHelper.NeedValidateReconstructed) { return null; }
        return _core.ValidateReconstructed(tracker, "DurObjOrderedDict", validateValues: false);
    }
}
