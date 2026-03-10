using System.Collections.Concurrent;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// HelperType + TypeCode 的复合解析结果。
/// <see cref="IsValid"/> 为 <c>false</c> 时表示该类型不被支持。
/// </summary>
internal readonly struct TypeEntry {
    internal readonly Type? HelperType;
    internal readonly byte[]? TypeCode;

    internal TypeEntry(Type helperType, byte[] typeCode) {
        HelperType = helperType;
        TypeCode = typeCode;
    }

    internal bool IsValid => HelperType != null;
}

/// <summary>
/// 泛型实参到 <see cref="ITypeHelper{T}"/> 实现类型的映射注册表。
/// 查找成功即验证通过，返回 <c>default</c> 即表示该类型不被支持。
/// 合并了类型验证与 Helper 解析，避免先验证再查找的双重遍历。
/// </summary>
internal static class HelperRegistry {

    private static readonly ConcurrentDictionary<Type, TypeEntry> _valueHelperCache = new() {
        [typeof(DurableList)] = new(typeof(DurableObjectHelper<DurableList>), [(byte)TypeOpCode.PushMixedList])
    };

    #region Key Helper 单例

    private static readonly TypeEntry _bool = new(typeof(BooleanHelper), [(byte)TypeOpCode.PushBoolean]);
    private static readonly TypeEntry _string = new(typeof(StringHelper), [(byte)TypeOpCode.PushString]);
    private static readonly TypeEntry _double = new(typeof(DoubleHelper), [(byte)TypeOpCode.PushDouble]);
    private static readonly TypeEntry _single = new(typeof(SingleHelper), [(byte)TypeOpCode.PushSingle]);
    private static readonly TypeEntry _half = new(typeof(HalfHelper), [(byte)TypeOpCode.PushHalf]);
    private static readonly TypeEntry _uint64 = new(typeof(UInt64Helper), [(byte)TypeOpCode.PushUInt64]);
    private static readonly TypeEntry _uint32 = new(typeof(UInt32Helper), [(byte)TypeOpCode.PushUInt32]);
    private static readonly TypeEntry _uint16 = new(typeof(UInt16Helper), [(byte)TypeOpCode.PushUInt16]);
    private static readonly TypeEntry _byte = new(typeof(ByteHelper), [(byte)TypeOpCode.PushByte]);
    private static readonly TypeEntry _int64 = new(typeof(Int64Helper), [(byte)TypeOpCode.PushInt64]);
    private static readonly TypeEntry _int32 = new(typeof(Int32Helper), [(byte)TypeOpCode.PushInt32]);
    private static readonly TypeEntry _int16 = new(typeof(Int16Helper), [(byte)TypeOpCode.PushInt16]);
    private static readonly TypeEntry _sbyte = new(typeof(SByteHelper), [(byte)TypeOpCode.PushSByte]);

    #endregion
    #region Key Helper 解析

    /// <summary>
    /// 解析 Key 类型对应的 <see cref="ITypeHelper{T}"/> 实现类型与 TypeCode。
    /// 返回 <c>default</c> 表示该类型不是受支持的 Key 类型。
    /// </summary>
    internal static TypeEntry ResolveKeyHelper(Type t) {
        if (t == typeof(bool)) { return _bool; }
        if (t == typeof(string)) { return _string; }
        // if (t == typeof(LocalId)) { ... } 暂时不支持，后续如果碰到需求再引入同时支持LocalId和DurableObjectRef两种语义

        if (t == typeof(double)) { return _double; }
        if (t == typeof(float)) { return _single; }
        if (t == typeof(Half)) { return _half; }

        if (t == typeof(ulong)) { return _uint64; }
        if (t == typeof(uint)) { return _uint32; }
        if (t == typeof(ushort)) { return _uint16; }
        if (t == typeof(byte)) { return _byte; }

        if (t == typeof(long)) { return _int64; }
        if (t == typeof(int)) { return _int32; }
        if (t == typeof(short)) { return _int16; }
        if (t == typeof(sbyte)) { return _sbyte; }
        return default;
    }

    #endregion
    #region Value Helper 解析（带缓存）

    /// <summary>
    /// 解析 Value 类型对应的 <see cref="ITypeHelper{T}"/> 实现类型与 TypeCode。
    /// 返回 <c>default</c> 表示该类型不是受支持的 Value 类型。
    /// 对嵌套容器类型递归验证其子类型参数。
    /// </summary>
    internal static TypeEntry ResolveValueHelper(Type t) =>
        _valueHelperCache.GetOrAdd(t, ResolveValueHelperCore);

    private static TypeEntry ResolveValueHelperCore(Type t) {
        // 基元类型（同时也可作为 Key 使用的类型）
        var h = ResolveKeyHelper(t);
        if (h.IsValid) { return h; }

        // DurableList (MixedList) 已作为 _valueHelperCache 初值处理

        // 泛型容器: 递归验证子类型参数并拼接 TypeCode
        if (t.IsGenericType) {
            var def = t.GetGenericTypeDefinition();

            // DurableDict<TKey, TValue> (TypedDict)
            if (def == typeof(DurableDict<,>)) {
                var args = t.GenericTypeArguments;
                var kEntry = ResolveKeyHelper(args[0]);
                if (!kEntry.IsValid) { return default; }
                var vEntry = ResolveValueHelper(args[1]);
                if (!vEntry.IsValid) { return default; }
                // 编码顺序: TValue, TKey, MakeTypedDict（与栈式解码器匹配）
                var typeCode = new byte[vEntry.TypeCode!.Length + kEntry.TypeCode!.Length + 1];
                vEntry.TypeCode.CopyTo(typeCode, 0);
                kEntry.TypeCode.CopyTo(typeCode, vEntry.TypeCode.Length);
                typeCode[^1] = (byte)TypeOpCode.MakeTypedDict;
                return new(typeof(DurableObjectHelper<>).MakeGenericType(t), typeCode);
            }

            // DurableDict<TKey> (MixedDict)
            if (def == typeof(DurableDict<>)) {
                var kEntry = ResolveKeyHelper(t.GenericTypeArguments[0]);
                if (!kEntry.IsValid) { return default; }
                var typeCode = new byte[kEntry.TypeCode!.Length + 1];
                kEntry.TypeCode.CopyTo(typeCode, 0);
                typeCode[^1] = (byte)TypeOpCode.MakeMixedDict;
                return new(typeof(DurableObjectHelper<>).MakeGenericType(t), typeCode);
            }

            // DurableList<T> (TypedList)
            if (def == typeof(DurableList<>)) {
                var vEntry = ResolveValueHelper(t.GenericTypeArguments[0]);
                if (!vEntry.IsValid) { return default; }
                var typeCode = new byte[vEntry.TypeCode!.Length + 1];
                vEntry.TypeCode.CopyTo(typeCode, 0);
                typeCode[^1] = (byte)TypeOpCode.MakeTypedList;
                return new(typeof(DurableObjectHelper<>).MakeGenericType(t), typeCode);
            }
        }

        return default;
    }

    #endregion
    #region 友好类型名

    /// <summary>格式化类型名称以用于错误消息。处理泛型嵌套。</summary>
    internal static string FormatTypeName(Type t) {
        if (!t.IsGenericType) { return t.Name; }
        var baseName = t.Name[..t.Name.IndexOf('`')];
        var args = string.Join(", ", t.GenericTypeArguments.Select(FormatTypeName));
        return $"{baseName}<{args}>";
    }

    #endregion
    #region 适配历史单元测试
    // Key 验证
    internal static bool IsValidKey(Type t) => ResolveKeyHelper(t).IsValid;

    // Value 验证（递归，缓存由 HelperRegistry 管理）
    internal static bool IsValidValue(Type t) => ResolveValueHelper(t).IsValid;
    #endregion
}
