using System.Linq.Expressions;
using System.Reflection;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// <see cref="DurableOrderedDict{TKey, TValue}"/> 的工厂缓存。
/// 模式与 <see cref="TypedDictFactory{TKey, TValue}"/> 完全对称。
/// </summary>
internal static class TypedOrderedDictFactory<TKey, TValue>
    where TKey : notnull
    where TValue : notnull {

    internal static readonly Func<DurableOrderedDict<TKey, TValue>>? Create;
    internal static readonly byte[]? TypeCode;
    internal static readonly string? ErrorReason;

    static TypedOrderedDictFactory() {
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

        if (typeof(DurableObject).IsAssignableFrom(typeof(TValue))) {
            ErrorReason = $"DurableOrderedDict does not support DurableObject values. Use DurableDict for object-valued dictionaries.";
            return;
        }

        var implType = typeof(TypedOrderedDictImpl<,,,>)
            .MakeGenericType(typeof(TKey), typeof(TValue), kEntry.HelperType!, vEntry.HelperType!);

        var ctor = implType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes
        )!;

        Create = Expression.Lambda<Func<DurableOrderedDict<TKey, TValue>>>(
            Expression.New(ctor)
        ).Compile();

        // TypeCode: TValue, TKey, MakeTypedOrderedDict
        var tc = new byte[vEntry.TypeCode!.Length + kEntry.TypeCode!.Length + 1];
        vEntry.TypeCode.CopyTo(tc, 0);
        kEntry.TypeCode.CopyTo(tc, vEntry.TypeCode.Length);
        tc[^1] = (byte)TypeOpCode.MakeTypedOrderedDict;
        TypeCode = tc;
        DurableOrderedDict<TKey, TValue>.s_typeCode = tc;
    }
}
