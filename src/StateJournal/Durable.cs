using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>
/// StateJournal 容器的统一工厂门面。
/// 所有 SJ 系列容器（<see cref="DurableDict{TKey,TValue}"/>、<see cref="DurableList{T}"/> 等）
/// 均通过此类创建。工厂方法通过 <see cref="HelperRegistry"/> 完成泛型实参的
/// 验证与 <see cref="ITypeHelper{T}"/> 映射（合二为一），
/// 并通过 Static Generic Class Cache 将构造委托编译缓存，后续调用近乎 native 开销。
/// </summary>
/// <example>
/// <code>
/// var flat   = Durable.Dict&lt;int, double&gt;();
/// var nested = Durable.Dict&lt;int, DurableDict&lt;double, int&gt;&gt;();
/// var mixed  = Durable.Dict&lt;string&gt;();
/// </code>
/// </example>
internal static class Durable {

    /// <summary>
    /// 创建 <see cref="DurableDict{TKey, TValue}"/> (TypedDict)。
    /// </summary>
    /// <typeparam name="TKey">键类型。当前支持：<c>int</c>、<c>double</c>、<c>string</c>。</typeparam>
    /// <typeparam name="TValue">
    /// 值类型。当前支持：<c>int</c>、<c>double</c>、<c>string</c>、
    /// <see cref="DurableDict{TKey,TValue}"/>（嵌套）、<see cref="DurableDict{TKey}"/>（嵌套）、
    /// <see cref="DurableList{T}"/>（嵌套）、<see cref="DurableList"/>（嵌套）。
    /// </typeparam>
    /// <returns>空的 <see cref="DurableDict{TKey, TValue}"/> 实例。</returns>
    /// <exception cref="ArgumentException">泛型参数不在受支持的类型范围内。</exception>
    public static DurableDict<TKey, TValue> Dict<TKey, TValue>() where TKey : notnull where TValue : notnull =>
        TypedDictFactory<TKey, TValue>.Create?.Invoke()
        ?? throw new ArgumentException(TypedDictFactory<TKey, TValue>.ErrorReason);

    /// <summary>
    /// 创建 <see cref="DurableDict{TKey}"/> (MixedDict)。
    /// 值类型为异构混合，支持 int/double/bool/string/<see cref="DurableObject"/> 等多种类型。
    /// </summary>
    /// <typeparam name="TKey">键类型。当前支持：<c>int</c>、<c>double</c>、<c>string</c>。</typeparam>
    /// <returns>空的 <see cref="DurableDict{TKey}"/> 实例。</returns>
    /// <exception cref="ArgumentException">泛型参数不在受支持的类型范围内。</exception>
    public static DurableDict<TKey> Dict<TKey>() where TKey : notnull =>
        MixedDictFactory<TKey>.Create?.Invoke()
        ?? throw new ArgumentException(MixedDictFactory<TKey>.ErrorReason);

    /// <summary>
    /// 创建 <see cref="DurableList{T}"/> (TypedList)。
    /// </summary>
    /// <typeparam name="T">
    /// 元素类型。当前支持：<c>int</c>、<c>double</c>、<c>string</c>、
    /// <see cref="DurableDict{TKey,TValue}"/>（嵌套）、<see cref="DurableList{T}"/>（嵌套）。
    /// </typeparam>
    /// <returns>空的 <see cref="DurableList{T}"/> 实例。</returns>
    /// <exception cref="ArgumentException">泛型参数不在受支持的类型范围内。</exception>
    public static DurableList<T> List<T>() where T : notnull =>
        TypedListFactory<T>.Create?.Invoke()
        ?? throw new ArgumentException(TypedListFactory<T>.ErrorReason);

    /// <summary>
    /// 创建 <see cref="DurableList"/> (MixedList)。
    /// </summary>
    /// <returns>空的 <see cref="DurableList"/> 实例。</returns>
    public static DurableList List() => new MixedListImpl();
}
