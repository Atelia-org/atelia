using System.Collections.Concurrent;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// 泛型实参到 <see cref="ITypeHelper{T}"/> 实现类型的映射注册表。
/// 查找成功即验证通过，返回 <c>null</c> 即表示该类型不被支持。
/// 合并了类型验证与 Helper 解析，避免先验证再查找的双重遍历。
/// </summary>
internal static class HelperRegistry {

    private static readonly ConcurrentDictionary<Type, Type?> _valueHelperCache = new();

    // ── Key Helper 解析 ─────────────────────────────────────────

    /// <summary>
    /// 解析 Key 类型对应的 <see cref="ITypeHelper{T}"/> 实现类型。
    /// 返回 <c>null</c> 表示该类型不是受支持的 Key 类型。
    /// </summary>
    internal static Type? ResolveKeyHelper(Type t) {
        if (t == typeof(int)) { return typeof(Int32Helper); }
        if (t == typeof(double)) { return typeof(DoubleHelper); }
        if (t == typeof(string)) { return typeof(StringHelper); }
        return null;
    }

    // ── Value Helper 解析（带缓存） ────────────────────────────

    /// <summary>
    /// 解析 Value 类型对应的 <see cref="ITypeHelper{T}"/> 实现类型。
    /// 返回 <c>null</c> 表示该类型不是受支持的 Value 类型。
    /// 对嵌套容器类型递归验证其子类型参数。
    /// </summary>
    internal static Type? ResolveValueHelper(Type t) =>
        _valueHelperCache.GetOrAdd(t, ResolveValueHelperCore);

    private static Type? ResolveValueHelperCore(Type t) {
        // 基元类型（同时也可作为 Key 使用的类型）
        var h = ResolveKeyHelper(t);
        if (h != null) { return h; }

        // 非泛型容器: DurableList (MixedList)
        if (t == typeof(DurableList)) { return typeof(ContainerHelper<DurableList>); }

        // 泛型容器: 递归验证子类型参数
        if (t.IsGenericType) {
            var def = t.GetGenericTypeDefinition();

            // DurableDict<TKey, TValue>
            if (def == typeof(DurableDict<,>)) {
                var args = t.GenericTypeArguments;
                if (ResolveKeyHelper(args[0]) == null) { return null; }
                if (ResolveValueHelper(args[1]) == null) { return null; }
                return typeof(ContainerHelper<>).MakeGenericType(t);
            }

            // DurableDict<TKey> (MixedDict)
            if (def == typeof(DurableDict<>)) {
                if (ResolveKeyHelper(t.GenericTypeArguments[0]) == null) { return null; }
                return typeof(ContainerHelper<>).MakeGenericType(t);
            }

            // DurableList<T>
            if (def == typeof(DurableList<>)) {
                if (ResolveValueHelper(t.GenericTypeArguments[0]) == null) { return null; }
                return typeof(ContainerHelper<>).MakeGenericType(t);
            }
        }

        return null;
    }

    // ── 友好类型名 ──────────────────────────────────────────────

    /// <summary>格式化类型名称以用于错误消息。处理泛型嵌套。</summary>
    internal static string FormatTypeName(Type t) {
        if (!t.IsGenericType) { return t.Name; }
        var baseName = t.Name[..t.Name.IndexOf('`')];
        var args = string.Join(", ", t.GenericTypeArguments.Select(FormatTypeName));
        return $"{baseName}<{args}>";
    }
}
