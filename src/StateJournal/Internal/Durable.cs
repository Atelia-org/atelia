namespace Atelia.StateJournal.Internal;

/// <summary>
/// StateJournal 容器的统一工厂门面。
/// 所有 SJ 系列容器（<see cref="DurableDict{TKey,TValue}"/>、<see cref="DurableDeque{T}"/> 等）
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
    /// <typeparam name="TKey">键类型。当前支持：<c>int</c>、<c>double</c>、<c>string</c>、<see cref="Symbol"/> 等 HelperRegistry 支持的叶子类型。</typeparam>
    /// <typeparam name="TValue">
    /// 值类型。当前支持：<c>int</c>、<c>double</c>、<c>string</c>、<see cref="Symbol"/>、
    /// <see cref="DurableDict{TKey,TValue}"/>（嵌套）、<see cref="DurableDict{TKey}"/>（嵌套）、
    /// <see cref="DurableDeque{T}"/>（嵌套）、<see cref="DurableDeque"/>（嵌套）。
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
    /// <typeparam name="TKey">键类型。当前支持：<c>int</c>、<c>double</c>、<c>string</c>、<see cref="Symbol"/> 等 HelperRegistry 支持的叶子类型。</typeparam>
    /// <returns>空的 <see cref="DurableDict{TKey}"/> 实例。</returns>
    /// <exception cref="ArgumentException">泛型参数不在受支持的类型范围内。</exception>
    public static DurableDict<TKey> Dict<TKey>() where TKey : notnull =>
        MixedDictFactory<TKey>.Create?.Invoke()
        ?? throw new ArgumentException(MixedDictFactory<TKey>.ErrorReason);

    /// <summary>
    /// 创建 <see cref="DurableDeque{T}"/> (TypedDeque)。
    /// </summary>
    /// <typeparam name="T">
    /// 元素类型。当前支持：<c>int</c>、<c>double</c>、<c>string</c>、<see cref="Symbol"/>、
    /// <see cref="DurableDict{TKey,TValue}"/>（嵌套）、<see cref="DurableDeque{T}"/>（嵌套）。
    /// </typeparam>
    /// <returns>空的 <see cref="DurableDeque{T}"/> 实例。</returns>
    /// <exception cref="ArgumentException">泛型参数不在受支持的类型范围内。</exception>
    public static DurableDeque<T> Deque<T>() where T : notnull =>
        TypedDequeFactory<T>.Create?.Invoke()
        ?? throw new ArgumentException(TypedDequeFactory<T>.ErrorReason);

    /// <summary>
    /// 创建 <see cref="DurableDeque"/> (MixedDeque)。
    /// </summary>
    /// <returns>空的 <see cref="DurableDeque"/> 实例。</returns>
    public static DurableDeque Deque() => new MixedDequeImpl();

    /// <summary>
    /// 创建 <see cref="DurableOrderedDict{TKey, TValue}"/> (TypedOrderedDict)。
    /// </summary>
    public static DurableOrderedDict<TKey, TValue> OrderedDict<TKey, TValue>() where TKey : notnull where TValue : notnull =>
        TypedOrderedDictFactory<TKey, TValue>.Create?.Invoke()
        ?? throw new ArgumentException(TypedOrderedDictFactory<TKey, TValue>.ErrorReason);

    /// <summary>
    /// 创建 <see cref="DurableOrderedDict{TKey}"/> (MixedOrderedDict)。
    /// 值类型为异构混合，支持 int/double/bool/string/<see cref="DurableObject"/> 等多种类型。
    /// </summary>
    /// <typeparam name="TKey">键类型。当前支持：<c>int</c>、<c>double</c>、<c>string</c>、<see cref="Symbol"/> 等 HelperRegistry 支持的叶子类型。</typeparam>
    /// <returns>空的 <see cref="DurableOrderedDict{TKey}"/> 实例。</returns>
    /// <exception cref="ArgumentException">泛型参数不在受支持的类型范围内。</exception>
    public static DurableOrderedDict<TKey> OrderedDict<TKey>() where TKey : notnull =>
        MixedOrderedDictFactory<TKey>.Create?.Invoke()
        ?? throw new ArgumentException(MixedOrderedDictFactory<TKey>.ErrorReason);

    /// <summary>
    /// 创建 <see cref="DurableText"/>。
    /// </summary>
    /// <returns>空的 <see cref="DurableText"/> 实例。</returns>
    public static DurableText Text() => new();
}
