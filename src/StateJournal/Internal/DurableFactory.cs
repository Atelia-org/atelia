using System.Linq.Expressions;
using System.Reflection;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// <see cref="DurableDict{TKey, TValue}"/> (TypedDict) 的工厂缓存。
/// CLR 对每组 &lt;TKey, TValue&gt; 仅初始化一次静态泛型类，天然线程安全且零锁。
/// 首次访问时通过反射构建 <see cref="TypedDictImpl{TKey,TValue,KHelper,VHelper}"/>
/// 的编译委托，后续调用近乎 native 开销。
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

        var implType = typeof(TypedDictImpl<,,,>)
            .MakeGenericType(typeof(TKey), typeof(TValue), kEntry.HelperType!, vEntry.HelperType!);

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
    }
}

/// <summary>
/// <see cref="DurableList{T}"/> (TypedList) 的工厂缓存。
/// 通过 <see cref="HelperRegistry"/> 将元素类型映射到 <see cref="ITypeHelper{T}"/>
/// 并构建 <see cref="TypedListImpl{T, VHelper}"/> 的编译委托。
/// </summary>
internal static class TypedListFactory<T>
    where T : notnull {

    internal static readonly Func<DurableList<T>>? Create;
    internal static readonly byte[]? TypeCode;
    internal static readonly string? ErrorReason;

    static TypedListFactory() {
        var vEntry = HelperRegistry.ResolveValueHelper(typeof(T));
        if (!vEntry.IsValid) {
            ErrorReason = $"Unsupported value type: {HelperRegistry.FormatTypeName(typeof(T))}.";
            return;
        }

        var implType = typeof(TypedListImpl<,>)
            .MakeGenericType(typeof(T), vEntry.HelperType!);

        var ctor = implType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes
        )!;

        Create = Expression.Lambda<Func<DurableList<T>>>(
            Expression.New(ctor)
        ).Compile();

        // TypeCode: T, MakeTypedList
        var tc = new byte[vEntry.TypeCode!.Length + 1];
        vEntry.TypeCode.CopyTo(tc, 0);
        tc[^1] = (byte)TypeOpCode.MakeTypedList;
        TypeCode = tc;
    }
}

/// <summary>
/// <see cref="DurableList"/> (MixedList) 的工厂缓存。
/// </summary>
internal static class MixedListFactory {
    private static readonly Func<DurableList> _create = BuildCreate();

    internal static DurableList Create() => _create();

    private static Func<DurableList> BuildCreate() {
        var ctor = typeof(MixedListImpl).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes
        )!;

        return Expression.Lambda<Func<DurableList>>(
            Expression.New(ctor)
        ).Compile();
    }
}
