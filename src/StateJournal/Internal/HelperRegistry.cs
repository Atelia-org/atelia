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

    /// <summary>仅提供 TypeCode、不提供 HelperType 的条目。
    /// 用于 DurableObject 容器值类型（由各工厂的 DurableObject 分支独立处理，不需要通用 VHelper）。</summary>
    internal TypeEntry(byte[] typeCode) {
        HelperType = null;
        TypeCode = typeCode;
    }

    internal bool IsValid => TypeCode != null;
}

/// <summary>
/// 泛型实参到 <see cref="ITypeHelper{T}"/> 实现类型的映射注册表。
/// 查找成功即验证通过，返回 <c>default</c> 即表示该类型不被支持。
/// 合并了类型验证与 Helper 解析，避免先验证再查找的双重遍历。
/// </summary>
internal static class HelperRegistry {
    private static readonly Type ValueTuple2Definition = typeof(ValueTuple<,>);
    private static readonly Type ValueTuple3Definition = typeof(ValueTuple<,,>);
    private static readonly Type ValueTuple4Definition = typeof(ValueTuple<,,,>);
    private static readonly Type ValueTuple5Definition = typeof(ValueTuple<,,,,>);
    private static readonly Type ValueTuple6Definition = typeof(ValueTuple<,,,,,>);
    private static readonly Type ValueTuple7Definition = typeof(ValueTuple<,,,,,,>);
    private static readonly Type ValueTuple2HelperDefinition = typeof(ValueTuple2Helper<,,,>);
    private static readonly Type ValueTuple3HelperDefinition = typeof(ValueTuple3Helper<,,,,,>);
    private static readonly Type ValueTuple4HelperDefinition = typeof(ValueTuple4Helper<,,,,,,,>);
    private static readonly Type ValueTuple5HelperDefinition = typeof(ValueTuple5Helper<,,,,,,,,,>);
    private static readonly Type ValueTuple6HelperDefinition = typeof(ValueTuple6Helper<,,,,,,,,,,,>);
    private static readonly Type ValueTuple7HelperDefinition = typeof(ValueTuple7Helper<,,,,,,,,,,,,,>);

    #region Key Helper 单例

    private static readonly TypeEntry _bool = new(typeof(BooleanHelper), [(byte)TypeOpCode.PushBoolean]);
    private static readonly TypeEntry _string = new(typeof(StringHelper), [(byte)TypeOpCode.PushString]);
    private static readonly TypeEntry _inlineString = new(typeof(InlineStringHelper), [(byte)TypeOpCode.PushInlineString]);
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
    #region 不可为Key的非泛型单例

    private static readonly TypeEntry _mixedDeque = new([(byte)TypeOpCode.PushMixedDeque]);
    internal static TypeEntry MixedDeque => _mixedDeque;

    #endregion

    private static readonly ConcurrentDictionary<Type, TypeEntry> _valueHelperCache = new() {
        [typeof(DurableDeque)] = _mixedDeque
    };

    #region Key Helper 解析

    /// <summary>
    /// 解析 Key 类型对应的 <see cref="ITypeHelper{T}"/> 实现类型与 TypeCode。
    /// 返回 <c>default</c> 表示该类型不是受支持的 Key 类型。
    /// </summary>
    internal static TypeEntry ResolveKeyHelper(Type t) {
        if (t == typeof(bool)) { return _bool; }
        if (t == typeof(string)) { return _string; }
        if (t == typeof(InlineString)) { return _inlineString; }
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

        if (TryResolveTupleHelper(t, out TypeEntry tupleEntry)) { return tupleEntry; }

        // DurableDeque (MixedDeque) 已作为 _valueHelperCache 初值处理

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
                return new(typeCode);
            }

            // DurableDict<TKey> (MixedDict)
            if (def == typeof(DurableDict<>)) {
                var kEntry = ResolveKeyHelper(t.GenericTypeArguments[0]);
                if (!kEntry.IsValid) { return default; }
                var typeCode = new byte[kEntry.TypeCode!.Length + 1];
                kEntry.TypeCode.CopyTo(typeCode, 0);
                typeCode[^1] = (byte)TypeOpCode.MakeMixedDict;
                return new(typeCode);
            }

            // DurableDeque<T> (TypedDeque)
            if (def == typeof(DurableDeque<>)) {
                var vEntry = ResolveValueHelper(t.GenericTypeArguments[0]);
                if (!vEntry.IsValid) { return default; }
                var typeCode = new byte[vEntry.TypeCode!.Length + 1];
                vEntry.TypeCode.CopyTo(typeCode, 0);
                typeCode[^1] = (byte)TypeOpCode.MakeTypedDeque;
                return new(typeCode);
            }
        }

        return default;
    }

    private static bool TryResolveTupleHelper(Type t, out TypeEntry entry) {
        entry = default;
        if (!t.IsGenericType) { return false; }

        var def = t.GetGenericTypeDefinition();
        var args = t.GenericTypeArguments;

        if (def == ValueTuple2Definition) {
            var first = ResolveTupleElementHelper(args[0]);
            if (!first.IsValid) { return false; }
            var second = ResolveTupleElementHelper(args[1]);
            if (!second.IsValid) { return false; }

            byte[] typeCode = BuildCompositeTypeCode(TypeOpCode.MakeValueTuple2, second.TypeCode!, first.TypeCode!);
            Type helperType = ValueTuple2HelperDefinition.MakeGenericType(args[0], args[1], first.HelperType!, second.HelperType!);
            entry = new(helperType, typeCode);
            return true;
        }

        if (def == ValueTuple3Definition) {
            var first = ResolveTupleElementHelper(args[0]);
            if (!first.IsValid) { return false; }
            var second = ResolveTupleElementHelper(args[1]);
            if (!second.IsValid) { return false; }
            var third = ResolveTupleElementHelper(args[2]);
            if (!third.IsValid) { return false; }

            byte[] typeCode = BuildCompositeTypeCode(TypeOpCode.MakeValueTuple3, third.TypeCode!, second.TypeCode!, first.TypeCode!);
            Type helperType = ValueTuple3HelperDefinition.MakeGenericType(args[0], args[1], args[2], first.HelperType!, second.HelperType!, third.HelperType!);
            entry = new(helperType, typeCode);
            return true;
        }

        if (def == ValueTuple4Definition) {
            var first = ResolveTupleElementHelper(args[0]);
            if (!first.IsValid) { return false; }
            var second = ResolveTupleElementHelper(args[1]);
            if (!second.IsValid) { return false; }
            var third = ResolveTupleElementHelper(args[2]);
            if (!third.IsValid) { return false; }
            var fourth = ResolveTupleElementHelper(args[3]);
            if (!fourth.IsValid) { return false; }

            byte[] typeCode = BuildCompositeTypeCode(TypeOpCode.MakeValueTuple4, fourth.TypeCode!, third.TypeCode!, second.TypeCode!, first.TypeCode!);
            Type helperType = ValueTuple4HelperDefinition.MakeGenericType(args[0], args[1], args[2], args[3], first.HelperType!, second.HelperType!, third.HelperType!, fourth.HelperType!);
            entry = new(helperType, typeCode);
            return true;
        }

        if (def == ValueTuple5Definition) {
            var first = ResolveTupleElementHelper(args[0]);
            if (!first.IsValid) { return false; }
            var second = ResolveTupleElementHelper(args[1]);
            if (!second.IsValid) { return false; }
            var third = ResolveTupleElementHelper(args[2]);
            if (!third.IsValid) { return false; }
            var fourth = ResolveTupleElementHelper(args[3]);
            if (!fourth.IsValid) { return false; }
            var fifth = ResolveTupleElementHelper(args[4]);
            if (!fifth.IsValid) { return false; }

            byte[] typeCode = BuildCompositeTypeCode(TypeOpCode.MakeValueTuple5, fifth.TypeCode!, fourth.TypeCode!, third.TypeCode!, second.TypeCode!, first.TypeCode!);
            Type helperType = ValueTuple5HelperDefinition.MakeGenericType(args[0], args[1], args[2], args[3], args[4], first.HelperType!, second.HelperType!, third.HelperType!, fourth.HelperType!, fifth.HelperType!);
            entry = new(helperType, typeCode);
            return true;
        }

        if (def == ValueTuple6Definition) {
            var first = ResolveTupleElementHelper(args[0]);
            if (!first.IsValid) { return false; }
            var second = ResolveTupleElementHelper(args[1]);
            if (!second.IsValid) { return false; }
            var third = ResolveTupleElementHelper(args[2]);
            if (!third.IsValid) { return false; }
            var fourth = ResolveTupleElementHelper(args[3]);
            if (!fourth.IsValid) { return false; }
            var fifth = ResolveTupleElementHelper(args[4]);
            if (!fifth.IsValid) { return false; }
            var sixth = ResolveTupleElementHelper(args[5]);
            if (!sixth.IsValid) { return false; }

            byte[] typeCode = BuildCompositeTypeCode(TypeOpCode.MakeValueTuple6, sixth.TypeCode!, fifth.TypeCode!, fourth.TypeCode!, third.TypeCode!, second.TypeCode!, first.TypeCode!);
            Type helperType = ValueTuple6HelperDefinition.MakeGenericType(args[0], args[1], args[2], args[3], args[4], args[5], first.HelperType!, second.HelperType!, third.HelperType!, fourth.HelperType!, fifth.HelperType!, sixth.HelperType!);
            entry = new(helperType, typeCode);
            return true;
        }

        if (def == ValueTuple7Definition) {
            var first = ResolveTupleElementHelper(args[0]);
            if (!first.IsValid) { return false; }
            var second = ResolveTupleElementHelper(args[1]);
            if (!second.IsValid) { return false; }
            var third = ResolveTupleElementHelper(args[2]);
            if (!third.IsValid) { return false; }
            var fourth = ResolveTupleElementHelper(args[3]);
            if (!fourth.IsValid) { return false; }
            var fifth = ResolveTupleElementHelper(args[4]);
            if (!fifth.IsValid) { return false; }
            var sixth = ResolveTupleElementHelper(args[5]);
            if (!sixth.IsValid) { return false; }
            var seventh = ResolveTupleElementHelper(args[6]);
            if (!seventh.IsValid) { return false; }

            byte[] typeCode = BuildCompositeTypeCode(TypeOpCode.MakeValueTuple7, seventh.TypeCode!, sixth.TypeCode!, fifth.TypeCode!, fourth.TypeCode!, third.TypeCode!, second.TypeCode!, first.TypeCode!);
            Type helperType = ValueTuple7HelperDefinition.MakeGenericType(args[0], args[1], args[2], args[3], args[4], args[5], args[6], first.HelperType!, second.HelperType!, third.HelperType!, fourth.HelperType!, fifth.HelperType!, sixth.HelperType!, seventh.HelperType!);
            entry = new(helperType, typeCode);
            return true;
        }

        return false;
    }

    private static TypeEntry ResolveTupleElementHelper(Type t) {
        TypeEntry entry = ResolveValueHelper(t);
        return entry.HelperType is null ? default : entry;
    }

    private static byte[] BuildCompositeTypeCode(TypeOpCode opCode, params byte[][] operandTypeCodes) {
        int totalLength = 1;
        foreach (byte[] code in operandTypeCodes) {
            totalLength += code.Length;
        }

        byte[] result = new byte[totalLength];
        int offset = 0;
        foreach (byte[] code in operandTypeCodes) {
            code.CopyTo(result, offset);
            offset += code.Length;
        }

        result[^1] = (byte)opCode;
        return result;
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
