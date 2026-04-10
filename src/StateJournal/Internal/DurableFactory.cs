using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// <see cref="DurableDict{TKey, TValue}"/> (TypedDict) 的工厂缓存。
/// CLR 对每组 &lt;TKey, TValue&gt; 仅初始化一次静态泛型类，天然线程安全且零锁。
/// 首次访问时通过反射构建 <see cref="TypedDictImpl{TKey,TValue,KHelper,VHelper}"/> 或
/// <see cref="DurObjDictImpl{TKey,TDurObj,KHelper}"/> 的编译委托，后续调用近乎 native 开销。
/// </summary>
internal static class TypedDictFactory<TKey, TValue>
    where TKey : notnull
    where TValue : notnull {

    internal static readonly Func<DurableDict<TKey, TValue>>? Create;
    internal static readonly byte[]? TypeCode;
    internal static readonly string? ErrorReason;

    static TypedDictFactory() {
        var kEntry = HelperRegistry.ResolveKeyHelper(typeof(TKey));
        if (!kEntry.IsValid) {
            ErrorReason = $"Unsupported key type: {typeof(TKey)}.";
            return;
        }

        var vEntry = HelperRegistry.ResolveValueHelper(typeof(TValue));
        if (!vEntry.IsValid) {
            ErrorReason = $"Unsupported value type: {HelperRegistry.FormatTypeName(typeof(TValue))}.";
            return;
        }

        Type implType;
        if (typeof(DurableObject).IsAssignableFrom(typeof(TValue))) {
            // DurableObject 值 → DurableObjectDictImpl（内部存 LocalId，Get 时懒加载）
            implType = typeof(DurObjDictImpl<,,>)
                .MakeGenericType(typeof(TKey), typeof(TValue), kEntry.HelperType!);
        }
        else {
            // 基元/值类型 → TypedDictImpl
            implType = typeof(TypedDictImpl<,,,>)
                .MakeGenericType(typeof(TKey), typeof(TValue), kEntry.HelperType!, vEntry.HelperType!);
        }

        var ctor = implType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes
        )!;

        Create = Expression.Lambda<Func<DurableDict<TKey, TValue>>>(
            Expression.New(ctor)
        ).Compile();

        // TypeCode: TValue, TKey, MakeTypedDict
        var tc = new byte[vEntry.TypeCode!.Length + kEntry.TypeCode!.Length + 1];
        vEntry.TypeCode.CopyTo(tc, 0);
        kEntry.TypeCode.CopyTo(tc, vEntry.TypeCode.Length);
        tc[^1] = (byte)TypeOpCode.MakeTypedDict;
        TypeCode = tc;
        DurableDict<TKey, TValue>.s_typeCode = tc;
    }
}

/// <summary>
/// <see cref="DurableDict{TKey}"/> (MixedDict) 的工厂缓存。
/// MixedDict 内部固定使用 <see cref="ValueBoxHelper"/> 作为值 Helper，
/// 工厂仅需解析 Key 的 Helper 类型。
/// </summary>
internal static class MixedDictFactory<TKey>
    where TKey : notnull {

    internal static readonly Func<DurableDict<TKey>>? Create;
    internal static readonly byte[]? TypeCode;
    internal static readonly string? ErrorReason;

    static MixedDictFactory() {
        var kEntry = HelperRegistry.ResolveKeyHelper(typeof(TKey));
        if (!kEntry.IsValid) {
            ErrorReason = $"Unsupported key type: {typeof(TKey)}.";
            return;
        }

        var implType = typeof(MixedDictImpl<,>)
            .MakeGenericType(typeof(TKey), kEntry.HelperType!);

        var ctor = implType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes
        )!;

        Create = Expression.Lambda<Func<DurableDict<TKey>>>(
            Expression.New(ctor)
        ).Compile();

        // TypeCode: TKey, MakeMixedDict
        var tc = new byte[kEntry.TypeCode!.Length + 1];
        kEntry.TypeCode.CopyTo(tc, 0);
        tc[^1] = (byte)TypeOpCode.MakeMixedDict;
        TypeCode = tc;
        DurableDict<TKey>.s_typeCode = tc;
    }
}

/// <summary>
/// <see cref="DurableDeque{T}"/> (TypedDeque) 的工厂缓存。
/// 通过 <see cref="HelperRegistry"/> 将元素类型映射到 <see cref="ITypeHelper{T}"/>
/// 并构建 <see cref="TypedDequeImpl{T, VHelper}"/> 的编译委托。
/// </summary>
internal static class TypedDequeFactory<T>
    where T : notnull {

    internal static readonly Func<DurableDeque<T>>? Create;
    internal static readonly byte[]? TypeCode;
    internal static readonly string? ErrorReason;

    static TypedDequeFactory() {
        var vEntry = HelperRegistry.ResolveValueHelper(typeof(T));
        if (!vEntry.IsValid) {
            ErrorReason = $"Unsupported value type: {HelperRegistry.FormatTypeName(typeof(T))}.";
            return;
        }

        Type implType;
        if (typeof(DurableObject).IsAssignableFrom(typeof(T))) {
            // DurableObject 值 → DurableObjectDequeImpl（占位，未来实现 LocalId 存储）
            implType = typeof(DurObjDequeImpl<>).MakeGenericType(typeof(T));
        }
        else {
            // 基元/值类型 → TypedDequeImpl
            implType = typeof(TypedDequeImpl<,>).MakeGenericType(typeof(T), vEntry.HelperType!);
        }

        var ctor = implType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes
        )!;

        Create = Expression.Lambda<Func<DurableDeque<T>>>(
            Expression.New(ctor)
        ).Compile();

        // TypeCode: T, MakeTypedDeque
        var tc = new byte[vEntry.TypeCode!.Length + 1];
        vEntry.TypeCode.CopyTo(tc, 0);
        tc[^1] = (byte)TypeOpCode.MakeTypedDeque;
        TypeCode = tc;
        DurableDeque<T>.s_typeCode = tc;
    }
}

/// <summary>
/// 非泛型工厂：从运行时 <see cref="Type"/> 实例构建 <see cref="DurableObject"/>。
/// 通过反射访问泛型 Factory 的 <c>Create</c> 委托并用 Expression 包装为
/// <c>Func&lt;DurableObject&gt;</c>，结果缓存于 <see cref="ConcurrentDictionary{TKey,TValue}"/>。
/// </summary>
internal static class DurableFactory {
    private static readonly ConcurrentDictionary<Type, Func<DurableObject>?> _cache = new();

    internal static bool TryCreate(Type type, [NotNullWhen(true)] out DurableObject? result) {
        var factory = _cache.GetOrAdd(type, BuildFactory);
        if (factory == null) {
            result = null;
            return false;
        }
        result = factory();
        return true;
    }

    private static Func<DurableObject>? BuildFactory(Type type) {
        if (type == typeof(DurableDeque)) { return static () => new MixedDequeImpl(); }
        if (!type.IsGenericType) { return null; }

        var def = type.GetGenericTypeDefinition();
        var args = type.GenericTypeArguments;

        Type? factoryType =
            def == typeof(DurableDict<>) ? typeof(MixedDictFactory<>).MakeGenericType(args) :
            def == typeof(DurableDict<,>) ? typeof(TypedDictFactory<,>).MakeGenericType(args) :
            def == typeof(DurableDeque<>) ? typeof(TypedDequeFactory<>).MakeGenericType(args) :
            def == typeof(DurableOrderedDict<>) ? typeof(MixedOrderedDictFactory<>).MakeGenericType(args) :
            def == typeof(DurableOrderedDict<,>) ? typeof(TypedOrderedDictFactory<,>).MakeGenericType(args) :
            null;

        if (factoryType == null) { return null; }

        // 读取 Factory 的 Create 字段（触发其静态构造函数，完成 s_typeCode 初始化等副作用）
        var createDelegate = (Delegate?)factoryType
            .GetField("Create", BindingFlags.Static | BindingFlags.NonPublic)?
            .GetValue(null);
        if (createDelegate == null) { return null; }

        // Expression.Invoke 避免 DynamicInvoke 的反射开销
        return Expression.Lambda<Func<DurableObject>>(
            Expression.Convert(
                Expression.Invoke(Expression.Constant(createDelegate)),
                typeof(DurableObject)
            )
        ).Compile();
    }
}
