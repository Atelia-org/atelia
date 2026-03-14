using System.Diagnostics;
using System.Runtime.InteropServices;
using Atelia.Data;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>MixedDict — 异构值字典，内部使用 <see cref="ValueBox"/> 存储。</summary>
/// <typeparam name="TKey"></typeparam>
public abstract class DurableDict<TKey> : DurableDictBase<TKey>, IDict<TKey>
, IDict<TKey, bool>, IDict<TKey, string>, IDict<TKey, DurableObject>
, IDict<TKey, double>, IDict<TKey, float>, IDict<TKey, Half>
, IDict<TKey, ulong>, IDict<TKey, uint>, IDict<TKey, ushort>, IDict<TKey, byte>
, IDict<TKey, long>, IDict<TKey, int>, IDict<TKey, short>, IDict<TKey, sbyte>

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
    #endregion

    internal DurableDict() {
        // TODO: LazyLoad
    }

    #region DurableObject
    public override DurableObjectKind Kind => DurableObjectKind.MixedDict;
    #endregion

    #region Generic Accessor

    /// <summary>
    /// 获取指定键的值，以请求的类型 <typeparamref name="TValue"/> 返回。
    /// 支持直接类型（int/double/bool/string）和容器子类型（如 DurableDict&lt;string&gt;、DurableList&lt;int&gt;）。
    /// </summary>
    /// <exception cref="KeyNotFoundException">Key 不存在。</exception>
    /// <exception cref="InvalidCastException">Key 存在但值类型不匹配。</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="TValue"/> 不是受支持的值类型。</exception>
    public TValue? Get<TValue>(TKey key) where TValue : notnull =>
        GetCore<TValue>(key, out var value) switch {
            GetIssue.None => value,
            GetIssue.PrecisionLost => throw new InvalidCastException(
                $"Value for key '{key}' cannot be cast to {typeof(TValue).Name} without losing precision."
            ),
            GetIssue.OverflowedToInfinity => throw new OverflowException(
                $"Value for key '{key}' overflows to infinity when cast to {typeof(TValue).Name}."
            ),
            GetIssue.Saturated => throw new OverflowException(
                $"Value for key '{key}' is out of bounds for {typeof(TValue).Name}."
            ),
            GetIssue.LoadFailed => throw new InvalidDataException(
                $"Value for key '{key}' references a DurableObject that cannot be loaded."
            ),
            GetIssue.UnsupportedType => throw new NotSupportedException(
                $"Type {typeof(TValue)} is not a supported value type for DurableDict"
            ),
            GetIssue.TypeMismatch => throw new InvalidCastException(
                $"Value for key '{key}' is not of type {typeof(TValue).Name}."
            ),
            GetIssue.NotFound => throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary."),
            _ => throw new UnreachableException()
        };

    /// <summary>尝试获取值。支持容器子类型。</summary>
    public bool TryGet<TValue>(TKey key, out TValue? value) where TValue : notnull => GetCore(key, out value) == GetIssue.None;

    public UpsertStatus Upsert<TValue>(TKey key, TValue? value) where TValue : notnull {
        if (this is IDict<TKey, TValue> typed) { return typed.Upsert(key, value); }
        if (typeof(DurableObject).IsAssignableFrom(typeof(TValue))) { return Upsert(key, (DurableObject?)(object?)value); }
        throw new NotSupportedException($"Type {typeof(TValue)} is not a supported value type for DurableDict");
    }

    /// <summary>
    /// 获取指定值类型的字典视图，便于使用类型化索引器。
    /// 支持内建值类型（int/double/bool/string 等）以及 <see cref="DurableObject"/> 子类型。
    /// </summary>
    /// <exception cref="NotSupportedException"><typeparamref name="TValue"/> 不是受支持的值类型。</exception>
    public IDict<TKey, TValue> Of<TValue>() where TValue : notnull {
        if (this is IDict<TKey, TValue> typed) { return typed; }
        // 此接口摩擦太低，为防止堆分配DurableSubtypeDictView被滥用，暂不支持。
        // if (typeof(DurableObject).IsAssignableFrom(typeof(TValue))) {
        //     return new DurableSubtypeDictView<TValue>(this);
        // }
        throw new NotSupportedException($"Type {typeof(TValue)} is not a supported value type for DurableDict");
    }

    #endregion

    #region IDict<TKey>

    public bool ContainsKey(TKey key) => _core.Current.ContainsKey(key);
    public int Count => _core.Current.Count;
    public bool Remove(TKey key) {
        if (!_core.Current.Remove(key, out var removedValue)) { return false; }
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

    private sealed class DurableSubtypeDictView<TValue>(DurableDict<TKey> owner) : IDict<TKey, TValue>
        where TValue : notnull {
        private readonly DurableDict<TKey> _owner = owner;

        public UpsertStatus Upsert(TKey key, TValue? value) {
            if (value is null) { return _owner.Upsert(key, (DurableObject?)null); }
            if (value is not DurableObject durableValue) { throw new NotSupportedException($"Type {typeof(TValue)} is not a supported value type for DurableDict"); }
            return _owner.Upsert(key, durableValue);
        }

        public GetIssue Get(TKey key, out TValue? value) => _owner.GetCore(key, out value);
    }

    private GetIssue GetCore<TValue>(TKey key, out TValue? value) where TValue : notnull {
        // Path 1: Direct interface match (int/double/bool/string/DurableObject)
        if (this is IDict<TKey, TValue> typed) { return typed.Get(key, out value); }

        // Path 2: Subtypes of DurableObject (e.g., DurableDict<string>)
        if (!typeof(DurableObject).IsAssignableFrom(typeof(TValue))) {
            value = default;
            return GetIssue.UnsupportedType;
        }
        var status = Get(key, out DurableObject? baseVal);
        if (status != GetIssue.None) {
            value = default;
            return status;
        }
        if (baseVal is not TValue castVal) {
            value = default;
            return GetIssue.TypeMismatch;
        }
        value = castVal;
        return GetIssue.None;
    }

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
        if (!VFace.UpdateOrInit(ref slot, value)) { return UpsertStatus.Updated; /* 值未变，跳过 AfterUpsert */ }
        return FinishUpsert(key, slot, exists);
    }

    #endregion

    #region IDict<TKey, TValue>

    public IDict<TKey, bool> OfBool => this;
    public GetIssue Get(TKey key, out bool value) => GetCore<bool, ValueBox.BooleanFace>(key, out value);
    public UpsertStatus Upsert(TKey key, bool value) => UpsertCore<bool, ValueBox.BooleanFace>(key, value);

    public IDict<TKey, string> OfString => this;
    public GetIssue Get(TKey key, out string? value) => GetCore<string, ValueBox.StringFace>(key, out value);
    public UpsertStatus Upsert(TKey key, string? value) => UpsertCore<string, ValueBox.StringFace>(key, value);

    public IDict<TKey, DurableObject> OfDurableObject => this;
    public GetIssue Get(TKey key, out DurableObject? value) {
        value = null;

        var issue = GetCore<DurableRef, ValueBox.DurableRefFace>(key, out var durRef);
        if (issue != GetIssue.None) { return issue; }

        if (durRef.IsNull) { return GetIssue.None; }

        AteliaResult<DurableObject> loadResult = Epoch.Load(durRef.Id);
        if (loadResult.IsFailure) { return GetIssue.LoadFailed; }
        DurableObject? loaded = loadResult.Value;
        if (loaded is null || loaded.Kind != durRef.Kind) { return GetIssue.LoadFailed; }

        value = loaded;
        return GetIssue.None;
    }
    public UpsertStatus Upsert(TKey key, DurableObject? value) {
        DurableRef durableRef;
        if (value is not null) {
            durableRef = new DurableRef(value.Kind, value.LocalId);
            Debug.Assert(!durableRef.IsNull);
            Debug.Assert(DurableRef.IsValidKind(durableRef.Kind));
        }
        else {
            durableRef = default;
        }
        return UpsertCore<DurableRef, ValueBox.DurableRefFace>(key, durableRef);
    }

    public IDict<TKey, double> OfDouble => this;
    public GetIssue Get(TKey key, out double value) => GetCore<double, ValueBox.RoundedDoubleFace>(key, out value);
    /// <summary>当尾数最低位为1时，采用round-to-odd sticky的方式舍入1位尾数（±1 ULP）;其他情况下精确存储。
    /// 需要精确存储所有double时请用<see cref="UpsertExactDouble"/>。</summary>
    public UpsertStatus Upsert(TKey key, double value) => UpsertCore<double, ValueBox.RoundedDoubleFace>(key, value);
    /// <summary>此方法可精确记录所有double，当尾数最低位为1时用堆分配记录。
    /// <see cref="Upsert(TKey, double)"/>采用round-to-odd sticky的方式舍入1位尾数（±1 ULP）。</summary>
    public UpsertStatus UpsertExactDouble(TKey key, double value) => UpsertCore<double, ValueBox.ExactDoubleFace>(key, value);

    public IDict<TKey, float> OfSingle => this;
    public GetIssue Get(TKey key, out float value) => GetCore<float, ValueBox.SingleFace>(key, out value);
    public UpsertStatus Upsert(TKey key, float value) => UpsertCore<float, ValueBox.SingleFace>(key, value);

    public IDict<TKey, Half> OfHalf => this;
    public GetIssue Get(TKey key, out Half value) => GetCore<Half, ValueBox.HalfFace>(key, out value);
    public UpsertStatus Upsert(TKey key, Half value) => UpsertCore<Half, ValueBox.HalfFace>(key, value);

    public IDict<TKey, ulong> OfUInt64 => this;
    public GetIssue Get(TKey key, out ulong value) => GetCore<ulong, ValueBox.UInt64Face>(key, out value);
    public UpsertStatus Upsert(TKey key, ulong value) => UpsertCore<ulong, ValueBox.UInt64Face>(key, value);

    public IDict<TKey, uint> OfUInt32 => this;
    public GetIssue Get(TKey key, out uint value) => GetCore<uint, ValueBox.UInt32Face>(key, out value);
    public UpsertStatus Upsert(TKey key, uint value) => UpsertCore<uint, ValueBox.UInt32Face>(key, value);

    public IDict<TKey, ushort> OfUInt16 => this;
    public GetIssue Get(TKey key, out ushort value) => GetCore<ushort, ValueBox.UInt16Face>(key, out value);
    public UpsertStatus Upsert(TKey key, ushort value) => UpsertCore<ushort, ValueBox.UInt16Face>(key, value);

    public IDict<TKey, byte> OfByte => this;
    public GetIssue Get(TKey key, out byte value) => GetCore<byte, ValueBox.ByteFace>(key, out value);
    public UpsertStatus Upsert(TKey key, byte value) => UpsertCore<byte, ValueBox.ByteFace>(key, value);

    public IDict<TKey, long> OfInt64 => this;
    public GetIssue Get(TKey key, out long value) => GetCore<long, ValueBox.Int64Face>(key, out value);
    public UpsertStatus Upsert(TKey key, long value) => UpsertCore<long, ValueBox.Int64Face>(key, value);

    public IDict<TKey, int> OfInt32 => this;
    public GetIssue Get(TKey key, out int value) => GetCore<int, ValueBox.Int32Face>(key, out value);
    public UpsertStatus Upsert(TKey key, int value) => UpsertCore<int, ValueBox.Int32Face>(key, value);

    public IDict<TKey, short> OfInt16 => this;
    public GetIssue Get(TKey key, out short value) => GetCore<short, ValueBox.Int16Face>(key, out value);
    public UpsertStatus Upsert(TKey key, short value) => UpsertCore<short, ValueBox.Int16Face>(key, value);

    public IDict<TKey, sbyte> OfSByte => this;
    public GetIssue Get(TKey key, out sbyte value) => GetCore<sbyte, ValueBox.SByteFace>(key, out value);
    public UpsertStatus Upsert(TKey key, sbyte value) => UpsertCore<sbyte, ValueBox.SByteFace>(key, value);

    #endregion
}
