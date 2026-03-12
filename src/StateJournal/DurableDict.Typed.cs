using Atelia.Data;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

public abstract class DurableDict<TKey, TValue> : DurableDictBase<TKey, TValue>, IDict<TKey>, IDict<TKey, TValue>
where TKey : notnull where TValue : notnull {
    /// <summary>由<see cref="TypedDictFactory{TKey, TValue}"/>初始化。</summary>
    internal static byte[]? s_typeCode;
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    internal DurableDict() {
    }

    #region DurableObject
    public override ValueKind Kind => ValueKind.TypedDict;
    #endregion

    // ── IDict<TKey> ─────────────────────────────────────────────

    public bool ContainsKey(TKey key) => _core.Current.ContainsKey(key);
    public int Count => _core.Current.Count;
    public abstract bool Remove(TKey key);

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
