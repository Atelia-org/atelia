using System.Linq.Expressions;
using System.Reflection;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// <see cref="DurableOrderedDict{TKey}"/> (MixedOrderedDict) 的工厂缓存。
/// 模式与 <see cref="MixedDictFactory{TKey}"/> 对称。
/// </summary>
internal static class MixedOrderedDictFactory<TKey>
    where TKey : notnull {

    internal static readonly Func<DurableOrderedDict<TKey>>? Create;
    internal static readonly byte[]? TypeCode;
    internal static readonly string? ErrorReason;

    static MixedOrderedDictFactory() {
        var kEntry = HelperRegistry.ResolveKeyHelper(typeof(TKey));
        if (!kEntry.IsValid) {
            ErrorReason = $"Unsupported key type: {typeof(TKey)}.";
            return;
        }

        var implType = typeof(MixedOrderedDictImpl<,>)
            .MakeGenericType(typeof(TKey), kEntry.HelperType!);

        var ctor = implType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes
        )!;

        Create = Expression.Lambda<Func<DurableOrderedDict<TKey>>>(
            Expression.New(ctor)
        ).Compile();

        // TypeCode: TKey, MakeMixedOrderedDict
        var tc = new byte[kEntry.TypeCode!.Length + 1];
        kEntry.TypeCode.CopyTo(tc, 0);
        tc[^1] = (byte)TypeOpCode.MakeMixedOrderedDict;
        TypeCode = tc;
        DurableOrderedDict<TKey>.s_typeCode = tc;
    }
}
