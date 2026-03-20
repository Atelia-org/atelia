using System.Diagnostics;

namespace Atelia.StateJournal;

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

public interface IDict<TKey> where TKey : notnull {
    bool ContainsKey(TKey key);
    int Count { get; }
    bool Remove(TKey key);
    IEnumerable<TKey> Keys { get; }
}

public enum UpsertStatus { Inserted, Updated }

public interface IDict<in TKey, TValue> where TKey : notnull where TValue : notnull {
    UpsertStatus Upsert(TKey key, TValue? value);
    GetIssue Get(TKey key, out TValue? value);

    sealed TValue? this[TKey key] {
        set => Upsert(key, value);
        get => Get(key, out TValue? value) == GetIssue.None ? value : throw new KeyNotFoundException();
    }
}

public static class DictExtensions {
    public static bool TryGet<TKey, TValue>(this IDict<TKey, TValue> dict, TKey key, out TValue? value)
        where TKey : notnull where TValue : notnull
        => dict.Get(key, out value) == GetIssue.None;

    public static TValue? GetOr<TKey, TValue>(this IDict<TKey, TValue> dict, TKey key, TValue? defaultValue)
        where TKey : notnull where TValue : notnull
        => dict.Get(key, out var value) == GetIssue.None ? value : defaultValue;

    public static TValue? GetOr<TKey, TValue>(this IDict<TKey, TValue> dict, TKey key, Func<TValue?> factory)
        where TKey : notnull where TValue : notnull
        => dict.Get(key, out var value) == GetIssue.None ? value : factory();
}

internal class DictDemo {
    public static void Run() {
        var model = Durable.Dict<string>();

        // ── Write: Upsert 重载自动推断类型 ──────────────────────
        model.Upsert("title", "文档标题");
        model.Upsert("wordCount", 1234);
        model.Upsert("zoom", 1.5);
        model.Upsert("darkMode", true);
        model.Upsert("items", Durable.Deque<int>());
        model.Upsert("metadata", Durable.Dict<string>());

        // 类型化视图（主要用于索引器）：As<T>() 与 As* 属性
        model.OfString["title"] = "新文档标题";
        model.OfInt32["wordCount"] = 2048;
        model.Of<double>()["zoom"] = 1.25;

        // ── Get: Get<T> 系列 ───────────────────────────────────
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

        // ── ✅ Wish 3: Keys 枚举 ────────────────────────────────
        foreach (var key in model.Keys) {
            bool exists = model.TryGetValueKind(key, out var kind);
            Debug.Assert(exists);
            Console.WriteLine($"  {key}: {kind}");
        }

        // ── ✅ Wish 4: 嵌套容器泛型取回 ─────────────────────────
        // 便捷方法：直接拿到具体泛型类型，无需手动 cast
        DurableDict<string>? meta = model.Get<DurableDict<string>>("metadata");
        DurableDeque<int>? items = model.Get<DurableDeque<int>>("items");
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

