using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>
/// 可持久化有序字典。内部基于跳表实现，叶链序列化 + 索引层内存重建。
/// Key 按自然序排列，支持范围查询和有序遍历。
/// </summary>
public abstract class DurableOrderedDict<TKey, TValue> : DurableDictBase<TKey>
    where TKey : notnull
    where TValue : notnull {

    /// <summary>由 <see cref="TypedOrderedDictFactory{TKey, TValue}"/> 初始化。</summary>
    internal static byte[]? s_typeCode;
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    internal DurableOrderedDict() { }

    public override DurableObjectKind Kind => DurableObjectKind.TypedOrderedDict;

    // ── Key-Value operations ────────────────────────────────────

    public abstract bool ContainsKey(TKey key);
    public abstract int Count { get; }
    public abstract bool Remove(TKey key);

    /// <summary>查询指定 key 的值。</summary>
    public abstract bool TryGet(TKey key, out TValue? value);

    /// <summary>插入或更新。</summary>
    public abstract void Upsert(TKey key, TValue value);

    // ── Ordered operations ──────────────────────────────────────

    /// <summary>按升序返回所有 key（每次调用分配新列表）。</summary>
    public abstract IReadOnlyList<TKey> GetKeys();

    /// <summary>从 <paramref name="minInclusive"/> 开始按升序读取最多 <paramref name="maxCount"/> 个 KV 对。</summary>
    public abstract List<KeyValuePair<TKey, TValue>> ReadAscendingFrom(TKey minInclusive, int maxCount);
}
