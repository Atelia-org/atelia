using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>异构值有序字典。内部以 <see cref="ValueBox"/> 存储值，按 Key 自然序排列。</summary>
[UseMixedValueCatalog(typeof(MixedValueCatalog), MixedContainers.OrderedDict)]
public abstract partial class DurableOrderedDict<TKey> : DurableDictBase<TKey>, IDict<TKey>,
    IDict<TKey, bool>, IDict<TKey, Symbol>, IDict<TKey, string>, IDict<TKey, DurableObject>,
    IDict<TKey, double>, IDict<TKey, float>, IDict<TKey, Half>,
    IDict<TKey, ulong>, IDict<TKey, uint>, IDict<TKey, ushort>, IDict<TKey, byte>,
    IDict<TKey, long>, IDict<TKey, int>, IDict<TKey, short>, IDict<TKey, sbyte>
where TKey : notnull {

    #region TypeCode

    /// <summary>由 <see cref="MixedOrderedDictFactory{TKey}"/> 初始化。</summary>
    internal static byte[]? s_typeCode;
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    #endregion

    internal DurableOrderedDict() { }

    #region DurableObject

    public override DurableObjectKind Kind => DurableObjectKind.MixedOrderedDict;

    #endregion

    #region Generated Dispatch (partial — bodies in .g.cs)

    public partial UpsertStatus Upsert<TValue>(TKey key, TValue? value) where TValue : notnull;
    public partial GetIssue Get<TValue>(TKey key, out TValue? value) where TValue : notnull;
    public partial IDict<TKey, TValue> Of<TValue>() where TValue : notnull;

    #endregion

    #region Generic Accessor

    public TValue? GetOrThrow<TValue>(TKey key) where TValue : notnull =>
        DictThrowHelpers.GetOrThrow(key, Get<TValue>(key, out TValue? value), value, "DurableOrderedDict");

    public bool TryGet<TValue>(TKey key, out TValue? value) where TValue : notnull =>
        Get<TValue>(key, out value) == GetIssue.None;

    #endregion

    #region IDict<TKey>

    public abstract bool ContainsKey(TKey key);
    public abstract int Count { get; }
    public abstract bool Remove(TKey key);
    public abstract IEnumerable<TKey> Keys { get; }

    public bool TryGetValueKind(TKey key, out ValueKind kind) {
        if (!TryGetValueBox(key, out ValueBox box)) {
            kind = default;
            return false;
        }
        kind = box.GetValueKind();
        return true;
    }

    #endregion

    #region Ordered operations

    /// <summary>按升序返回所有 key。</summary>
    public abstract IReadOnlyList<TKey> GetKeys();

    /// <summary>从 <paramref name="minInclusive"/> 开始按升序返回最多 <paramref name="maxCount"/> 个 key。</summary>
    public abstract IReadOnlyList<TKey> GetKeysFrom(TKey minInclusive, int maxCount);

    #endregion

    #region Core Hooks

    private protected virtual void OnCurrentValueRemoved(ValueBox removedValue) { }
    private protected virtual void OnCurrentValueUpserted(ValueBox oldValue, ValueBox newValue, bool existed) { }

    #endregion

    #region Private Impl

    private protected abstract GetIssue GetCore<TValue, VFace>(TKey key, out TValue? value)
        where TValue : notnull
        where VFace : ValueBox.ITypedFace<TValue>;

    private protected abstract UpsertStatus UpsertCore<TValue, VFace>(TKey key, TValue? value)
        where TValue : notnull
        where VFace : ValueBox.ITypedFace<TValue>;

    private protected abstract bool TryGetValueBox(TKey key, out ValueBox box);

    #endregion

    #region DurableObject Helpers

    private protected DurableRef ToDurableRef(DurableObject? value) {
        if (value is not null) { Revision.EnsureCanReference(value); }
        return value is not null ? new DurableRef(value.Kind, value.LocalId) : default;
    }

    private protected GetIssue GetDurableObject(TKey key, out DurableObject? value) {
        value = null;

        var issue = GetCore<DurableRef, ValueBox.DurableRefFace>(key, out var durRef);
        if (issue != GetIssue.None) { return issue; }

        if (durRef.IsNull) { return GetIssue.None; }

        AteliaResult<DurableObject> loadResult = Revision.Load(durRef.Id);
        if (loadResult.IsFailure) { return GetIssue.LoadFailed; }
        DurableObject? loaded = loadResult.Value;
        if (loaded is null || loaded.Kind != durRef.Kind) { return GetIssue.LoadFailed; }

        value = loaded;
        return GetIssue.None;
    }

    #endregion

    #region Symbol Helpers

    private protected GetIssue GetSymbol(TKey key, out Symbol value) {
        var issue = GetCore<SymbolId, ValueBox.SymbolIdFace>(key, out var symbolId);
        if (issue != GetIssue.None) {
            value = default;
            return issue;
        }
        if (symbolId.IsNull) {
            // slot 存的是 ValueBox.Null 或非 Symbol 类型；Symbol 本身契约非 null，不应被误读为 Symbol.Empty。
            value = default;
            return GetIssue.TypeMismatch;
        }
        if (!Revision.TryGetSymbol(symbolId, out string? str)) {
            value = default;
            return GetIssue.LoadFailed;
        }
        value = new Symbol(str!);
        return GetIssue.None;
    }

    private protected UpsertStatus UpsertSymbol(TKey key, Symbol value) {
        SymbolId id = Revision.InternSymbol(value.Value);
        return UpsertCore<SymbolId, ValueBox.SymbolIdFace>(key, id);
    }

    #endregion

    #region Double Helpers

    public UpsertStatus UpsertExactDouble(TKey key, double value) =>
        UpsertCore<double, ValueBox.ExactDoubleFace>(key, value);

    #endregion
}
