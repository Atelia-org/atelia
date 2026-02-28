using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>
/// StateJournal 容器的统一工厂门面。
/// <para>
/// 所有 SJ 系列容器（<see cref="DurableDict{TKey,TValue}"/>、<see cref="DurableList{T}"/> 等）
/// 均通过此类创建，工厂方法会在运行时验证泛型参数的合法性。
/// </para>
/// </summary>
/// <example>
/// <code>
/// var flat   = SJ.Dict&lt;int, double&gt;();
/// var nested = SJ.Dict&lt;int, SJDict&lt;double, int&gt;&gt;();
/// var list   = SJ.List&lt;SJValue&gt;();
/// </code>
/// </example>
public static class Durable {

    /// <summary>
    /// 创建 <see cref="DurableDict{TKey, TValue}"/>。
    /// </summary>
    /// <typeparam name="TKey">键类型。当前支持：<c>int</c>、<c>double</c>。</typeparam>
    /// <typeparam name="TValue">
    /// 值类型。当前支持：<c>int</c>、<c>double</c>、<see cref="ValueBox"/>、
    /// <see cref="DurableDict{TKey,TValue}"/>（嵌套）、<see cref="DurableList{T}"/>（嵌套）。
    /// </typeparam>
    /// <returns>空的 <see cref="DurableDict{TKey, TValue}"/> 实例。</returns>
    /// <exception cref="ArgumentException">泛型参数不在受支持的类型范围内。</exception>
    public static DurableDict<TKey, TValue> Dict<TKey, TValue>() where TKey : notnull where TValue : notnull {
        GenericRules.ValidateDict(typeof(TKey), typeof(TValue));
        return new DurableDict<TKey, TValue>();
    }

    public static DurableDict<TKey> Dict<TKey>() where TKey : notnull {
        GenericRules.ValidateKey(typeof(TKey));
        return new DurableDict<TKey>();
    }

    /// <summary>
    /// 创建 <see cref="DurableList{T}"/>。
    /// </summary>
    /// <typeparam name="T">
    /// 元素类型。当前支持：<c>int</c>、<c>double</c>、<see cref="ValueBox"/>、
    /// <see cref="DurableDict{TKey,TValue}"/>（嵌套）、<see cref="DurableList{T}"/>（嵌套）。
    /// </typeparam>
    /// <returns>空的 <see cref="DurableList{T}"/> 实例。</returns>
    /// <exception cref="ArgumentException">泛型参数不在受支持的类型范围内。</exception>
    public static DurableList<T> List<T>() where T : notnull {
        GenericRules.ValidateValue(typeof(T));
        return new DurableList<T>();
    }

    public static DurableList List() {
        return new DurableList();
    }
}
