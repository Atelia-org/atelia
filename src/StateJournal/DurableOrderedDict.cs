using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>
/// 可持久化有序字典。内部基于跳表实现，叶链序列化 + 索引层内存重建。
/// Key 按自然序排列，支持范围查询和有序遍历。
/// </summary>
/// <remarks>
/// <para><b>关于 <c>notnull</c> 约束与 <c>TValue?</c></b>：
/// <c>where TValue : notnull</c> 的作用是阻止泛型实参传入自带可空注解的类型（如 <c>string?</c>、<c>Symbol?</c>、<c>DurableDict?</c>），
/// 但容器内部对于引用类型始终支持 <c>null</c> 值的传入和存储。
/// 这与 <see cref="DurableDict{TKey, TValue}"/> 的约定一致。</para>
/// <para>对值类型（如 <c>int</c>、<c>double</c>），<c>TValue?</c> 在 NRT 下只是注解，运行时类型仍为 <c>TValue</c> 本身。</para>
/// </remarks>
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
    public abstract GetIssue Get(TKey key, out TValue? value);

    /// <summary>查询便捷方法，等价于 <c>Get(key, out value) == GetIssue.None</c>。</summary>
    public bool TryGet(TKey key, out TValue? value) => Get(key, out value) == GetIssue.None;

    /// <summary>插入或更新。</summary>
    /// <remarks>
    /// 对 <see cref="DurableObject"/> 和 <see cref="Symbol"/> 这类可表达空值的 facade，空值会被正确持久化
    /// （DurableObject → <see cref="LocalId.Null"/>，Symbol → <see cref="SymbolId.Null"/>）并在 <see cref="Get"/> 时原样返回。
    /// 裸 <c>string</c> 走值语义 payload 路线；<c>null</c> 会按空字符串写出。
    /// </remarks>
    public abstract UpsertStatus Upsert(TKey key, TValue? value);

    // ── Ordered operations ──────────────────────────────────────

    /// <summary>按升序返回所有 key（每次调用分配新列表）。</summary>
    public abstract IReadOnlyList<TKey> GetKeys();

    /// <summary>从 <paramref name="minInclusive"/> 开始按升序读取最多 <paramref name="maxCount"/> 个 KV 对。</summary>
    /// <remarks>DurableObject 值路线下，<c>Upsert(key, null)</c> 存入的条目在此处 Value 为 <c>null</c>。</remarks>
    public abstract List<KeyValuePair<TKey, TValue?>> ReadAscendingFrom(TKey minInclusive, int maxCount);
}
