using System.Diagnostics;
using System.Runtime.InteropServices;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>MixedDict — 异构值字典，内部使用 <see cref="ValueBox"/> 存储。</summary>
/// <typeparam name="TKey"></typeparam>
[UseMixedValueCatalog(typeof(MixedValueCatalog), MixedContainers.Dict)]
public abstract partial class DurableDict<TKey> : DurableDictBase<TKey>, IDict<TKey>,
    IDict<TKey, bool>, IDict<TKey, string>, IDict<TKey, DurableObject>,
    IDict<TKey, double>, IDict<TKey, float>, IDict<TKey, Half>,
    IDict<TKey, ulong>, IDict<TKey, uint>, IDict<TKey, ushort>, IDict<TKey, byte>,
    IDict<TKey, long>, IDict<TKey, int>, IDict<TKey, short>, IDict<TKey, sbyte>
where TKey : notnull {

    private protected DictChangeTracker<TKey, ValueBox> _core;

    #region TypeCode

    /// <summary>由<see cref="MixedDictFactory{TKey}"/>初始化。</summary>
    internal static byte[]? s_typeCode;
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    #endregion

    #region DurableDictBase abstract properties

    public override bool HasChanges => _core.HasChanges;
    private protected override int RebaseCount => _core.RebaseCount;
    private protected override int DeltifyCount => _core.DeltifyCount;

    #endregion

    #region Core
    private UpsertStatus FinishUpsert(TKey key, ValueBox value, bool exists) {
        Debug.Assert(!value.IsUninitialized); // 未初始化的ValueBox不应被存入容器
        _core.AfterUpsert<ValueBoxHelper>(key, value);
        return exists ? UpsertStatus.Updated : UpsertStatus.Inserted;
    }

    private protected virtual void OnCurrentValueRemoved(ValueBox removedValue) { }
    private protected virtual void OnCurrentValueUpserted(ValueBox oldValue, ValueBox newValue, bool existed) { }
    #endregion

    internal DurableDict() {
        // TODO: LazyLoad
    }

    #region DurableObject
    public override DurableObjectKind Kind => DurableObjectKind.MixedDict;
    #endregion

    #region Generated Dispatch (partial — bodies in .g.cs)

    public partial UpsertStatus Upsert<TValue>(TKey key, TValue? value) where TValue : notnull;
    public partial GetIssue Get<TValue>(TKey key, out TValue? value) where TValue : notnull;
    public partial IDict<TKey, TValue> Of<TValue>() where TValue : notnull;

    #endregion

    #region Generic Accessor

    /// <summary>
    /// 获取指定键的值，以请求的类型 <typeparamref name="TValue"/> 返回。
    /// 支持直接类型（int/double/bool/string）和容器子类型（如 <see cref="DurableDict{string}"/>、<see cref="DurableDeque{int}"/>）。
    /// </summary>
    public TValue? GetOrThrow<TValue>(TKey key) where TValue : notnull =>
        DictThrowHelpers.GetOrThrow(key, Get<TValue>(key, out TValue? value), value);

    /// <summary>尝试获取值。支持容器子类型。</summary>
    public bool TryGet<TValue>(TKey key, out TValue? value) where TValue : notnull =>
        Get<TValue>(key, out value) == GetIssue.None;

    #endregion

    #region IDict<TKey>

    public bool ContainsKey(TKey key) => _core.Current.ContainsKey(key);
    public int Count => _core.Current.Count;
    public bool Remove(TKey key) {
        if (!_core.Current.Remove(key, out var removedValue)) { return false; }
        OnCurrentValueRemoved(removedValue);
        _core.AfterRemove<ValueBoxHelper>(key, removedValue);
        return true;
    }
    public bool TryGetValueKind(TKey key, out ValueKind kind) {
        if (!_core.Current.TryGetValue(key, out ValueBox box)) {
            kind = default;
            return false;
        }
        kind = box.GetValueKind();
        return true;
    }

    /// <summary>所有键的枚举。</summary>
    public IEnumerable<TKey> Keys => _core.Current.Keys;

    #endregion

    #region Private Impl

    private GetIssue GetCore<TValue, VFace>(TKey key, out TValue? value)
        where TValue : notnull
        where VFace : ValueBox.ITypedFace<TValue> {
        if (!_core.Current.TryGetValue(key, out ValueBox box)) {
            value = default;
            return GetIssue.NotFound;
        }
        return VFace.Get(box, out value);
    }

    private UpsertStatus UpsertCore<TValue, VFace>(TKey key, TValue? value)
        where TValue : notnull
        where VFace : ValueBox.ITypedFace<TValue> {
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_core.Current, key, out bool exists);
        ValueBox oldValue = exists ? slot : default;
        if (!VFace.UpdateOrInit(ref slot, value)) { return UpsertStatus.Updated; /* 值未变，跳过 AfterUpsert */ }
        OnCurrentValueUpserted(oldValue, slot, exists);
        return FinishUpsert(key, slot, exists);
    }

    #endregion

    #region Double Helpers

    /// <summary>当尾数最低位为1时，采用round-to-odd sticky的方式舍入1位尾数（±1 ULP）;其他情况下精确存储。
    /// 需要精确存储所有double时请用<see cref="UpsertExactDouble"/>。</summary>
    public UpsertStatus UpsertExactDouble(TKey key, double value) => UpsertCore<double, ValueBox.ExactDoubleFace>(key, value);

    #endregion

    #region DurableObject Helpers

    private DurableRef ToDurableRef(DurableObject? value) {
        if (value is not null) { Revision.EnsureCanReference(value); }
        return value is not null ? new DurableRef(value.Kind, value.LocalId) : default;
    }

    private GetIssue GetDurableObject(TKey key, out DurableObject? value) {
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
}
