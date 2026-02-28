
using System.Diagnostics;

namespace Atelia.StateJournal;

public abstract class DurableDictBase {

}

// 关于Dict<TKey>实例之间的不关心类型的值复制该如何提供API我还没想好：
// A: 提供DurableUnion作为临时中转类型，`dstDict["dstKey"] = srcDict["srcKey"]`，需要实现IDict<TKey, DurableUnion>接口。
// B: 提供静态辅助方法，`Durable.Copy(dstDict, "dstKey", srcDict, "srcKey")`，内部实现无需union有机会更高效。
// C: 提供成员辅助方法，`dstDict.CopyFrom("dstKey", srcDict, "srcKey")`，`srcDict.CopyTo("srcKey", dstDict, "dstKey")`，不那么对称。
// [StructLayout(LayoutKind.Explicit)]
// public readonly ref struct DurableUnion {
//     [FieldOffset(16)]
//     private readonly DurableValueKind _kind;

//     [FieldOffset(8)]
//     private readonly object? _ref;

//     [FieldOffset(0)]
//     private readonly ulong _u64;

//     [FieldOffset(0)]
//     private readonly double _fp64;
// }

public interface IDict<in TKey> where TKey : notnull {
    bool ContainsKey(TKey key);
    DurableValueKind? GetValueKind(TKey key);
    int Count { get; }
    bool Remove(TKey key);
}

public enum UpsertStatus { Inserted, Updated }

public interface IDict<in TKey, TValue> where TKey : notnull where TValue : notnull {
    UpsertStatus Upsert(TKey key, TValue? value);
    GetStatus Get(TKey key, out TValue? value);
    GetStatus GetCoerced(TKey key, out TValue? value) => Get(key, out value);

    sealed TValue? this[TKey key] {
        set => Upsert(key, value);
        get => Get(key, out TValue? value) == GetStatus.Success ? value : throw new KeyNotFoundException();
    }
}

public static class DictExtensions {
    public static bool TryGet<TKey, TValue>(this IDict<TKey, TValue> dict, TKey key, out TValue? value)
        where TKey : notnull where TValue : notnull
        => dict.Get(key, out value) == GetStatus.Success;

    public static TValue? GetOr<TKey, TValue>(this IDict<TKey, TValue> dict, TKey key, TValue? defaultValue)
        where TKey : notnull where TValue : notnull
        => dict.Get(key, out var value) == GetStatus.Success ? value : defaultValue;

    public static TValue? GetOr<TKey, TValue>(this IDict<TKey, TValue> dict, TKey key, Func<TValue?> factory)
        where TKey : notnull where TValue : notnull
        => dict.Get(key, out var value) == GetStatus.Success ? value : factory();
}

/// <summary>替代<see cref="DurableDict{TKey,DurableValue}"/></summary>
/// <typeparam name="TKey"></typeparam>
public class DurableDict<TKey> : DurableDictBase, IDict<TKey>, IDict<TKey, int>, IDict<TKey, double>, IDict<TKey, bool>, IDict<TKey, string>, IDict<TKey, DurableDictBase>, IDict<TKey, DurableListBase>
where TKey : notnull {
    internal DurableDict() { }

    private readonly Dictionary<TKey, ValueEntry> _entries = new();

    private struct ValueEntry {
        public DurableValueKind Kind;
        public ValueBox Box;
        public object? Ref;
    }

    private UpsertStatus UpsertEntry(TKey key, ValueEntry entry) {
        bool existed = _entries.ContainsKey(key);
        _entries[key] = entry;
        return existed ? UpsertStatus.Updated : UpsertStatus.Inserted;
    }

    private static ValueEntry CreateReferenceEntry(DurableValueKind kind, object? value) => new() {
        Kind = value is null ? DurableValueKind.Null : kind,
        Ref = value,
    };

    // ── Typed Read API ──────────────────────────────────────────

    /// <summary>
    /// 获取指定键的值，以请求的类型 <typeparamref name="TValue"/> 返回。
    /// 支持直接类型（int/double/bool/string）和容器子类型（如 DurableDict&lt;string&gt;、DurableList&lt;int&gt;）。
    /// </summary>
    /// <exception cref="KeyNotFoundException">Key 不存在。</exception>
    /// <exception cref="InvalidCastException">Key 存在但值类型不匹配。</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="TValue"/> 不是受支持的值类型。</exception>
    public TValue? Get<TValue>(TKey key) where TValue : notnull =>
        GetCore<TValue>(key, out var value) switch {
            GetStatus.Success => value,
            GetStatus.NotFound => throw new KeyNotFoundException(key?.ToString()),
            GetStatus.TypeMismatch => throw new InvalidCastException(
                $"Key '{key}' exists but value is not of type {typeof(TValue).Name}"
            ),
            GetStatus.OutOfRange => throw new OverflowException(
                $"Key '{key}' exists but value is out of range for type {typeof(TValue).Name}"
            ),
            GetStatus.PrecisionLost => throw new InvalidCastException(
                $"Key '{key}' exists but value would lose precision when cast to {typeof(TValue).Name}"
            ),
            GetStatus.Truncated => throw new InvalidCastException(
                $"Key '{key}' exists but value would be truncated when cast to {typeof(TValue).Name}"
            ),
            GetStatus.SignednessChanged => throw new InvalidCastException(
                $"Key '{key}' exists but value would change sign when cast to {typeof(TValue).Name}"
            ),
            _ => throw new UnreachableException()
        };

    /// <summary>尝试获取值。支持容器子类型。</summary>
    public bool TryGet<TValue>(TKey key, out TValue? value) where TValue : notnull =>
        GetCore(key, out value) == GetStatus.Success;

    /// <summary>获取值或返回默认值。支持容器子类型。</summary>
    public TValue? GetOr<TValue>(TKey key, TValue? defaultValue) where TValue : notnull =>
        GetCore<TValue>(key, out var value) == GetStatus.Success ? value : defaultValue;

    /// <summary>获取值或调用工厂获取默认值。支持容器子类型。</summary>
    public TValue? GetOr<TValue>(TKey key, Func<TValue?> factory) where TValue : notnull =>
        GetCore<TValue>(key, out var value) == GetStatus.Success ? value : factory();

    // ── Convenience: Nested container read ──────────────────────

    /// <summary>获取嵌套的 <see cref="DurableDict{TKey2}"/>。</summary>
    public DurableDict<TKey2>? GetDict<TKey2>(TKey key) where TKey2 : notnull =>
        Get<DurableDict<TKey2>>(key);

    /// <summary>获取嵌套的 <see cref="DurableDict{TKey2, TValue2}"/>。</summary>
    public DurableDict<TKey2, TValue2>? GetDict<TKey2, TValue2>(TKey key)
        where TKey2 : notnull where TValue2 : notnull =>
        Get<DurableDict<TKey2, TValue2>>(key);

    /// <summary>获取嵌套的 <see cref="DurableList{T}"/>。</summary>
    public DurableList<T>? GetList<T>(TKey key) where T : notnull =>
        Get<DurableList<T>>(key);

    // ── Get routing core ────────────────────────────────────────

    private GetStatus GetCore<TValue>(TKey key, out TValue? value) where TValue : notnull {
        // Path 1: Direct interface match (int/double/bool/string/DurableDictBase/DurableListBase)
        if (this is IDict<TKey, TValue> typed) { return typed.Get(key, out value); }

        // Path 2: Subtypes of DurableDictBase (e.g., DurableDict<string>)
        if (typeof(DurableDictBase).IsAssignableFrom(typeof(TValue))) {
            var status = ((IDict<TKey, DurableDictBase>)this).Get(key, out var baseVal);
            if (status == GetStatus.Success && baseVal is TValue castVal) {
                value = castVal;
                return GetStatus.Success;
            }
            value = default;
            return status == GetStatus.Success ? GetStatus.TypeMismatch : status;
        }

        // Path 3: Subtypes of DurableListBase (e.g., DurableList<int>)
        if (typeof(DurableListBase).IsAssignableFrom(typeof(TValue))) {
            var status = ((IDict<TKey, DurableListBase>)this).Get(key, out var baseVal);
            if (status == GetStatus.Success && baseVal is TValue castVal) {
                value = castVal;
                return GetStatus.Success;
            }
            value = default;
            return status == GetStatus.Success ? GetStatus.TypeMismatch : status;
        }

        throw new NotSupportedException(
            $"Type {typeof(TValue)} is not a supported value type for DurableDict"
        );
    }

    // ── IDict<TKey> ─────────────────────────────────────────────

    public bool ContainsKey(TKey key) => _entries.ContainsKey(key);
    public DurableValueKind? GetValueKind(TKey key) => _entries.TryGetValue(key, out var entry) ? entry.Kind : null;
    public int Count => _entries.Count;
    public bool Remove(TKey key) => _entries.Remove(key);

    /// <summary>所有键的枚举。</summary>
    public IEnumerable<TKey> Keys => _entries.Keys;

    // ── Type-accessor aliases ───────────────────────────────────

    public IDict<TKey, double> Doubles => this;
    public IDict<TKey, int> Int32s => this;

    // ── IDict<TKey, T> implementations ──────────────────────────

    public GetStatus Get(TKey key, out int value) {
        if (!_entries.TryGetValue(key, out var entry)) {
            value = default;
            return GetStatus.NotFound;
        }
        if (entry.Kind == DurableValueKind.NonnegativeInteger || entry.Kind == DurableValueKind.NegativeInteger) {
            return entry.Box.Get(out value);
        }
        value = default;
        return GetStatus.TypeMismatch;
    }

    public GetStatus GetCoerced(TKey key, out int value) {
        if (!_entries.TryGetValue(key, out var entry)) {
            value = default;
            return GetStatus.NotFound;
        }
        if (entry.Kind == DurableValueKind.NonnegativeInteger
            || entry.Kind == DurableValueKind.NegativeInteger
            || entry.Kind == DurableValueKind.FloatingPoint) {
            return entry.Box.GetCoerced(out value);
        }
        value = default;
        return GetStatus.TypeMismatch;
    }

    public GetStatus Get(TKey key, out double value) {
        if (!_entries.TryGetValue(key, out var entry)) {
            value = default;
            return GetStatus.NotFound;
        }
        if (entry.Kind == DurableValueKind.FloatingPoint
            || entry.Kind == DurableValueKind.NonnegativeInteger
            || entry.Kind == DurableValueKind.NegativeInteger) {
            return entry.Box.Get(out value);
        }
        value = default;
        return GetStatus.TypeMismatch;
    }

    public UpsertStatus Upsert(TKey key, int value) {
        return UpsertEntry(key, new ValueEntry {
            Kind = value >= 0 ? DurableValueKind.NonnegativeInteger : DurableValueKind.NegativeInteger,
            Box = ValueBox.FromInt32(value)
        });
    }

    /// <summary>当尾数最低位为1时，采用round-to-odd sticky的方式舍入1位尾数（±1 ULP）;其他情况下精确存储。</summary>
    public UpsertStatus Upsert(TKey key, double value) {
        return UpsertEntry(key, new ValueEntry {
            Kind = DurableValueKind.FloatingPoint,
            Box = ValueBox.FromRoundedDouble(value)
        });
    }

    public UpsertStatus Upsert(TKey key, string? value) {
        return UpsertEntry(key, CreateReferenceEntry(DurableValueKind.String, value));
    }

    public GetStatus Get(TKey key, out string? value) {
        if (!_entries.TryGetValue(key, out var entry)) {
            value = default;
            return GetStatus.NotFound;
        }
        if (entry.Kind == DurableValueKind.Null) {
            value = null;
            return GetStatus.Success;
        }
        if (entry.Kind == DurableValueKind.String && entry.Ref is string s) {
            value = s;
            return GetStatus.Success;
        }
        value = default;
        return GetStatus.TypeMismatch;
    }

    public UpsertStatus Upsert(TKey key, DurableDictBase? value) {
        return UpsertEntry(key, CreateReferenceEntry(DurableValueKind.Undefined, value));
    }

    public GetStatus Get(TKey key, out DurableDictBase? value) {
        if (!_entries.TryGetValue(key, out var entry)) {
            value = default;
            return GetStatus.NotFound;
        }
        if (entry.Kind == DurableValueKind.Null) {
            value = null;
            return GetStatus.Success;
        }
        if (entry.Ref is DurableDictBase dict) {
            value = dict;
            return GetStatus.Success;
        }
        value = default;
        return GetStatus.TypeMismatch;
    }

    public UpsertStatus Upsert(TKey key, DurableListBase? value) {
        return UpsertEntry(key, CreateReferenceEntry(DurableValueKind.Undefined, value));
    }

    public GetStatus Get(TKey key, out DurableListBase? value) {
        if (!_entries.TryGetValue(key, out var entry)) {
            value = default;
            return GetStatus.NotFound;
        }
        if (entry.Kind == DurableValueKind.Null) {
            value = null;
            return GetStatus.Success;
        }
        if (entry.Ref is DurableListBase list) {
            value = list;
            return GetStatus.Success;
        }
        value = default;
        return GetStatus.TypeMismatch;
    }

    public UpsertStatus Upsert(TKey key, bool value) {
        return UpsertEntry(key, new ValueEntry {
            Kind = DurableValueKind.Boolean,
            Ref = value
        });
    }

    public GetStatus Get(TKey key, out bool value) {
        if (!_entries.TryGetValue(key, out var entry)) {
            value = default;
            return GetStatus.NotFound;
        }
        if (entry.Kind == DurableValueKind.Boolean && entry.Ref is bool b) {
            value = b;
            return GetStatus.Success;
        }
        value = default;
        return GetStatus.TypeMismatch;
    }

    #region exact double
    /// <summary>此方法可精确记录所有double，当尾数最低位为1时用堆分配记录。
    /// <see cref="Upsert(TKey, double)"/>采用round-to-odd sticky的方式舍入1位尾数（±1 ULP）。</summary>
    public UpsertStatus UpsertExactDouble(TKey key, double value) {
        return UpsertEntry(key, new ValueEntry {
            Kind = DurableValueKind.FloatingPoint,
            Box = ValueBox.FromExactDouble(value)
        });
    }
    #endregion
}

public class DurableDict<TKey, TValue> : DurableDictBase, IDict<TKey>, IDict<TKey, TValue>
where TKey : notnull where TValue : notnull {
    internal DurableDict() {

    }

    private readonly Dictionary<TKey, TValue?> _entries = new();

    // ── IDict<TKey> ─────────────────────────────────────────────

    public bool ContainsKey(TKey key) => _entries.ContainsKey(key);
    public DurableValueKind? GetValueKind(TKey key) =>
        _entries.TryGetValue(key, out var value) ? GetKindForValue(value) : null;
    public int Count => _entries.Count;
    public bool Remove(TKey key) => _entries.Remove(key);
    public IEnumerable<TKey> Keys => _entries.Keys;

    // ── IDict<TKey, TValue> ─────────────────────────────────────

    public GetStatus Get(TKey key, out TValue? value) {
        if (_entries.TryGetValue(key, out var stored)) {
            value = stored;
            return GetStatus.Success;
        }
        value = default;
        return GetStatus.NotFound;
    }

    public UpsertStatus Upsert(TKey key, TValue? value) {
        bool existed = _entries.ContainsKey(key);
        _entries[key] = value;
        return existed ? UpsertStatus.Updated : UpsertStatus.Inserted;
    }

    private static DurableValueKind GetKindForValue(TValue? value) {
        if (value is null) { return DurableValueKind.Null; }

        return value switch {
            int i => i >= 0 ? DurableValueKind.NonnegativeInteger : DurableValueKind.NegativeInteger,
            double => DurableValueKind.FloatingPoint,
            bool => DurableValueKind.Boolean,
            string => DurableValueKind.String,
            DurableDictBase => DurableValueKind.Undefined,
            DurableListBase => DurableValueKind.Undefined,
            _ => DurableValueKind.Undefined
        };
    }
}

internal class DictDemo {
    public static void Run() {
        var model = Durable.Dict<string>();

        // ── Write: Upsert 重载自动推断类型 ──────────────────────
        model.Upsert("title", "文档标题");
        model.Upsert("wordCount", 1234);
        model.Upsert("zoom", 1.5);
        model.Upsert("darkMode", true);
        model.Upsert("items", Durable.List<int>());
        model.Upsert("metadata", Durable.Dict<string>());

        // ── Read: Get<T> 系列 ───────────────────────────────────
        string? title = model.Get<string>("title");
        int wc = model.Get<int>("wordCount");

        // TryGet: out 参数推断类型
        if (model.TryGet("zoom", out double y)) {
            Console.WriteLine(y);
        }

        // GetOr: 默认值
        double zoom = model.GetOr("zoom", 1.0);

        // ── ✅ Wish 1: Remove ───────────────────────────────────
        model.Remove("tempFlag");

        // ── ✅ Wish 2: IDict<TKey> 基础查询 ─────────────────────
        bool has = model.ContainsKey("title");
        int count = model.Count;
        DurableValueKind? kind = model.GetValueKind("zoom");

        // ── ✅ Wish 3: Keys 枚举 ────────────────────────────────
        foreach (var key in model.Keys) {
            Console.WriteLine($"  {key}: {model.GetValueKind(key)}");
        }

        // ── ✅ Wish 4: 嵌套容器泛型取回 ─────────────────────────
        // 便捷方法：直接拿到具体泛型类型，无需手动 cast
        DurableDict<string>? meta = model.GetDict<string>("metadata");
        DurableList<int>? items = model.GetList<int>("items");
        // 也可用 Get<T> 直接指定子类型（GetCore 自动中转基类再 cast）：
        DurableDict<string>? meta2 = model.Get<DurableDict<string>>("metadata");
        // TryGet 同样支持子类型：
        if (model.TryGet("metadata", out DurableDict<string>? meta3)) {
            Console.WriteLine(meta3);
        }
    }

    // ── ✅ Wish 5: TEA update 完整模拟 ──────────────────────────
    static void TeaUpdate(DurableDict<string> model, string msg) {
        switch (msg) {
            case "increment":
                model.Upsert("wordCount", model.Get<int>("wordCount") + 1);
                break;
            case "setTitle":
                model.Upsert("title", "新标题");
                model.Upsert("lastModified", 1740700000);
                break;
            case "reset":
                model.Upsert("wordCount", 0);
                model.Upsert("zoom", 1.0);
                model.Remove("tempFlag");
                break;
            case "toggleDark":
                model.Upsert("darkMode", !model.Get<bool>("darkMode"));
                break;
        }
    }

    // ── ✅ Wish 6: Get<T> TypeMismatch 已区分 ───────────────────
    // model.Upsert("zoom", 1.5);
    // model.Get<int>("zoom");         → InvalidCastException: "Key 'zoom' exists but value is not of type Int32"
    // model.Get<int>("nonexistent");  → KeyNotFoundException
    // model.Get<DateTime>("zoom");    → NotSupportedException: "Type DateTime is not a supported value type"

    // ── Wish 7: 批量 Upsert ─────────────────────────────────────
    // 结论：连续 Upsert 就是最务实的，TEA update 本来就是命令式。
}

