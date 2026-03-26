using System.Runtime.InteropServices;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// <see cref="DurableDict{TKey, TValue}"/> 的 string value 专用实现。
/// 内部以 <see cref="SymbolId"/> 存储引用（per-Revision intern pool），
/// 对外通过 <see cref="DurableDict{TKey, TValue}"/> 提供 <c>string</c> 的读写接口。
/// Get 时委托 <see cref="RevisionStringCodec"/> 从 Revision symbol table 解码。
/// </summary>
/// <remarks>
/// 与 <see cref="DurObjDictImpl{TKey, TDurObj, KHelper}"/> 完全对称：
/// DurObj 场景内部存 <see cref="LocalId"/>，本场景内部存 <see cref="SymbolId"/>。
/// </remarks>
internal class SymbolValDictImpl<TKey, KHelper> : DurableDict<TKey, string>
    where TKey : notnull
    where KHelper : unmanaged, ITypeHelper<TKey> {

    private DictChangeTracker<TKey, SymbolId> _core;

    internal SymbolValDictImpl() {
        _core = new();
    }

    #region DurableDictBase abstract properties

    public override bool HasChanges => _core.HasChanges;
    private protected override int RebaseCount => _core.RebaseCount;
    private protected override int DeltifyCount => _core.DeltifyCount;

    #endregion

    #region DurableObject

    internal override void DiscardChanges() => _core.Revert<SymbolIdHelper>();

    #endregion

    #region IDict<TKey>

    public override bool ContainsKey(TKey key) => _core.Current.ContainsKey(key);

    public override int Count => _core.Current.Count;

    public override IEnumerable<TKey> Keys => _core.Current.Keys;

    internal override IReadOnlyCollection<TKey> CommittedKeys => _core.CommittedKeys;

    public override bool Remove(TKey key) {
        if (!_core.Current.Remove(key, out var removedId)) { return false; }
        _core.AfterRemove<SymbolIdHelper>(key, removedId);
        return true;
    }

    #endregion

    #region IDict<TKey, string>

    public override GetIssue Get(TKey key, out string? value) {
        value = null;
        if (!_core.Current.TryGetValue(key, out var symbolId)) { return GetIssue.NotFound; }
        return RevisionStringCodec.Decode(Revision, symbolId, out value);
    }

    public override UpsertStatus Upsert(TKey key, string? value) {
        var symbolId = RevisionStringCodec.Encode(Revision, value);
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_core.Current, key, out bool exists);
        slot = symbolId;
        _core.AfterUpsert<SymbolIdHelper>(key, symbolId);
        return exists ? UpsertStatus.Updated : UpsertStatus.Inserted;
    }

    #endregion

    #region Persistence Hooks

    private protected override void CommitCore() => _core.Commit<SymbolIdHelper>();
    private protected override void SyncCurrentFromCommittedCore() => _core.SyncCurrentFromCommitted<SymbolIdHelper>();
    private protected override void WriteRebaseCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteRebase<KHelper, SymbolIdHelper>(writer, context);
    private protected override void WriteDeltifyCore(BinaryDiffWriter writer, DiffWriteContext context) => _core.WriteDeltify<KHelper, SymbolIdHelper>(writer, context);
    private protected override void ApplyDeltaCore(ref BinaryDiffReader reader) => _core.ApplyDelta<KHelper, SymbolIdHelper>(ref reader);

    #endregion

    // ── 引用遍历 ──

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        // SymbolValDictImpl 不持有 DurableObject 引用，Visit(LocalId) 无需调用。
        // 遍历内部存储的 SymbolId，标记 symbol pool 中的可达条目。
        foreach (var symbolId in _core.Current.Values) {
            if (!symbolId.IsNull) { visitor.Visit(symbolId); }
        }
    }

    internal override bool AcceptChildRefRewrite<TRewriter>(ref TRewriter rewriter) {
        bool changed = false;
        var keys = new List<TKey>(_core.Current.Count);
        foreach (var kvp in _core.Current) {
            if (!kvp.Value.IsNull) { keys.Add(kvp.Key); }
        }
        foreach (var key in keys) {
            var oldId = _core.Current[key];
            var newId = rewriter.Rewrite(oldId);
            if (newId != oldId) {
                _core.Current[key] = newId;
                _core.AfterUpsert<SymbolIdHelper>(key, newId);
                changed = true;
            }
        }
        return changed;
    }
}
