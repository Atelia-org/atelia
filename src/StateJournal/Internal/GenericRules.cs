namespace Atelia.StateJournal.Internal;

/// <summary>
/// 对 Durable 系列泛型容器的类型参数进行运行时合法性验证。
/// 实际的类型映射与缓存由 <see cref="HelperRegistry"/> 完成，
/// 本类提供面向验证的语义封装（抛出带友好消息的异常）。
/// </summary>
internal static class GenericRules {

    #region 公开入口

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
                $"Unsupported value type: {HelperRegistry.FormatTypeName(typeofValue)}."
            );
        }
    }

    #endregion

    // Key 验证
    internal static bool IsValidKey(Type t) => HelperRegistry.ResolveKeyHelper(t).IsValid;

    // Value 验证（递归，缓存由 HelperRegistry 管理）
    internal static bool IsValidValue(Type t) => HelperRegistry.ResolveValueHelper(t).IsValid;
}
