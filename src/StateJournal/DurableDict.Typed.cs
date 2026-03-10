using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

public abstract class DurableDict<TKey, TValue> : DurableObject, IDict<TKey>, IDict<TKey, TValue>
where TKey : notnull where TValue : notnull {
    /// <summary>由<see cref="TypedDictFactory{TKey, TValue}"/>初始化。</summary>
    internal static byte[]? s_typeCode;
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    #region Core
    private protected DictChangeTracker<TKey, TValue> _core;
    #endregion
    internal DurableDict() {
    }

    #region DurableObject
    public override ValueKind Kind => ValueKind.MixedDict;
    public override bool HasChanges => _core.HasChanges;
    #endregion

    // ── IDict<TKey> ─────────────────────────────────────────────

    public bool ContainsKey(TKey key) => _core.Current.ContainsKey(key);
    public int Count => _core.Current.Count;
    protected virtual void OnRemoved(TKey key, TValue? removedValue) {
        // 默认实现保守处理：仅更新脏键集合。
        // 具体实现（TypedDictImpl）会用 VHelper 执行资源释放语义。
        _core.AfterRemove(key);
    }

    public virtual bool Remove(TKey key) {
        if (!_core.Current.Remove(key, out TValue? removedValue)) { return false; }
        OnRemoved(key, removedValue);
        return true;
    }

    /// <summary>所有键的枚举。</summary>
    public IEnumerable<TKey> Keys => _core.Current.Keys;

    // ── IDict<TKey, TValue> ─────────────────────────────────────

    public GetIssue Get(TKey key, out TValue? value) {
        return _core.Current.TryGetValue(key, out value)
            ? GetIssue.None
            : GetIssue.NotFound;
    }

    public abstract UpsertStatus Upsert(TKey key, TValue? value);
}
