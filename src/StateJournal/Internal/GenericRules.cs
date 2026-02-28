using System.Collections.Concurrent;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// 对 Durable 系列泛型容器的类型参数进行运行时合法性验证。
/// <para>
/// C# 泛型约束无法逐一列举受支持的类型，因此由此类在运行时递归检查。
/// 支持嵌套泛型（如 <c>DurableDict&lt;int, DurableDict&lt;double, int&gt;&gt;</c>）。
/// </para>
/// </summary>
internal static class GenericRules {

    // ── 缓存 ────────────────────────────────────────────────────────
    // Key/Value 验证结果都以 Type 为键缓存，避免反复反射。

    private static readonly ConcurrentDictionary<Type, bool> ValueCache = new();

    // ── 公开入口 ────────────────────────────────────────────────────

    /// <summary>验证 <c>DurableDict&lt;TKey, TValue&gt;</c> 的类型参数组合是否合法。</summary>
    public static void ValidateDict(Type typeofKey, Type typeofValue) {
        ValidateKey(typeofKey);
        ValidateValue(typeofValue);
    }

    public static void ValidateKey(Type typeofKey) {
        if (!IsValidKey(typeofKey)) {
            throw new ArgumentException(
                $"Unsupported key type: {typeofKey}."
            );
        }
    }

    public static void ValidateValue(Type typeofValue) {
        if (!IsValidValue(typeofValue)) {
            throw new ArgumentException(
                $"Unsupported value type: {FormatTypeName(typeofValue)}."
            );
        }
    }

    // ── Key 验证 ────────────────────────────────────────────────────

    internal static bool IsValidKey(Type t) => t == typeof(int)
        || t == typeof(double)
        || t == typeof(string);

    // ── Value 验证（递归） ──────────────────────────────────────────

    internal static bool IsValidValue(Type t) => IsValidKey(t)
        || t == typeof(DurableList)
        || (t.IsGenericType && ValueCache.GetOrAdd(t, CheckValue));

    private static bool CheckValue(Type t) {
        var def = t.GetGenericTypeDefinition();

        // DurableDict<TKey, TValue> — 递归验证
        if (def == typeof(DurableDict<,>)) {
            var args = t.GenericTypeArguments;
            return IsValidKey(args[0]) && IsValidValue(args[1]);
        }

        // DurableDict<TKey> — 递归验证
        if (def == typeof(DurableDict<>)) { return IsValidKey(t.GenericTypeArguments[0]); }

        // DurableList<T> — 递归验证
        if (def == typeof(DurableList<>)) { return IsValidValue(t.GenericTypeArguments[0]); }

        return false;
    }

    // ── 友好类型名 ──────────────────────────────────────────────────

    private static string FormatTypeName(Type t) {
        if (!t.IsGenericType) { return t.Name; }
        var baseName = t.Name[..t.Name.IndexOf('`')];
        var args = string.Join(", ", t.GenericTypeArguments.Select(FormatTypeName));
        return $"{baseName}<{args}>";
    }
}
