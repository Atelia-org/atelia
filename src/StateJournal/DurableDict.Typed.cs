using Atelia.Data;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

public abstract class DurableDict<TKey, TValue> : DurableDictBase<TKey>, IDict<TKey>, IDict<TKey, TValue>
where TKey : notnull where TValue : notnull {
    /// <summary>由<see cref="TypedDictFactory{TKey, TValue}"/>初始化。</summary>
    internal static byte[]? s_typeCode;
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    internal DurableDict() {
    }

    #region DurableObject
    public override DurableObjectKind Kind => DurableObjectKind.TypedDict;
    #endregion

    // ── IDict<TKey> ─────────────────────────────────────────────

    public abstract bool ContainsKey(TKey key);
    public abstract int Count { get; }
    public abstract bool Remove(TKey key);
    public abstract IEnumerable<TKey> Keys { get; }

    // ── IDict<TKey, TValue> ─────────────────────────────────────

    public abstract GetIssue Get(TKey key, out TValue? value);
    public abstract UpsertStatus Upsert(TKey key, TValue? value);

}
