using System.Diagnostics.CodeAnalysis;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// 由于其涉及持久化数据，所以一经投入使用就不可再变更，届时将internal可见性改为public进行标记。
/// 在序列化时使用前导长度而非`return`操作码。
/// </summary>
/// <remarks>
/// **TODO（持久化数据正式落地前）**：当真正开始记录长期 segment 时，需要系统性整理本枚举
/// 与其它涉及 wire format 的不可变枚举（<see cref="DurableObjectKind"/>、<see cref="VersionKind"/>、
/// <see cref="FrameUsage"/>、<see cref="FrameSource"/>、<see cref="HeapValueKind"/> 等），
/// 把所有成员都改为显式数值字面量并冻结分配，避免日后插入/重命名导致 numeric 漂移。
/// 当前阶段（个人自用 / 无下游用户 / 无历史数据）允许 numeric remap，参见本文件 13/14 注释。
/// </remarks>
internal enum TypeOpCode : byte {
    Invalid = 0,

    PushByte = 1,
    PushUInt16 = 2,
    PushUInt32 = 3,
    PushUInt64 = 4,

    PushSByte = 5,
    PushInt16 = 6,
    PushInt32 = 7,
    PushInt64 = 8,

    PushBoolean = 9,
    PushHalf = 10,
    PushSingle = 11,
    PushDouble = 12,

    // Legacy wire mapping:
    // 13 used to be PushString (symbol-backed string); it now decodes as explicit Symbol.
    // 14 used to be PushInlineString; it now decodes as value-payload string.
    PushSymbol = 13,
    PushString = 14,
    PushMixedDeque = 15,
    PushText = 16,

    MakeMixedDict = 128,
    MakeTypedDict,
    MakeTypedDeque,
    MakeMixedOrderedDict,
    MakeTypedOrderedDict,

    MakeValueTuple2 = 192,
    MakeValueTuple3,
    MakeValueTuple4,
    MakeValueTuple5,
    MakeValueTuple6,
    MakeValueTuple7,
}

internal static class TypeCodec {
    /// <summary>从已确定长度的完整TypeOpCode序列解码出所表示的Durable类型。</summary>
    /// <param name="bytes">不含头部长度，不含尾部无关字节。</param>
    internal static bool TryDecode(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out Type? result) {
        result = null;
        if (bytes.IsEmpty) { return false; }
        Stack<Type> operands = new(8);
        for (int i = 0; i < bytes.Length; ++i) {
            TypeOpCode op = (TypeOpCode)bytes[i];
            switch (op) {
                case TypeOpCode.PushByte:
                    operands.Push(typeof(byte));
                    break;
                case TypeOpCode.PushUInt16:
                    operands.Push(typeof(ushort));
                    break;
                case TypeOpCode.PushUInt32:
                    operands.Push(typeof(uint));
                    break;
                case TypeOpCode.PushUInt64:
                    operands.Push(typeof(ulong));
                    break;
                case TypeOpCode.PushSByte:
                    operands.Push(typeof(sbyte));
                    break;
                case TypeOpCode.PushInt16:
                    operands.Push(typeof(short));
                    break;
                case TypeOpCode.PushInt32:
                    operands.Push(typeof(int));
                    break;
                case TypeOpCode.PushInt64:
                    operands.Push(typeof(long));
                    break;
                case TypeOpCode.PushBoolean:
                    operands.Push(typeof(bool));
                    break;
                case TypeOpCode.PushHalf:
                    operands.Push(typeof(Half));
                    break;
                case TypeOpCode.PushSingle:
                    operands.Push(typeof(float));
                    break;
                case TypeOpCode.PushDouble:
                    operands.Push(typeof(double));
                    break;
                case TypeOpCode.PushSymbol:
                    operands.Push(typeof(Symbol));
                    break;
                case TypeOpCode.PushString:
                    operands.Push(typeof(string));
                    break;
                case TypeOpCode.PushMixedDeque:
                    operands.Push(typeof(DurableDeque));
                    break;
                case TypeOpCode.PushText:
                    operands.Push(typeof(DurableText));
                    break;

                case TypeOpCode.MakeMixedDict:
                    if (operands.Count < 1) { return false; }
                    operands.Push(typeof(DurableDict<>).MakeGenericType(operands.Pop()));
                    break;
                case TypeOpCode.MakeTypedDict:
                    if (operands.Count < 2) { return false; }
                    operands.Push(typeof(DurableDict<,>).MakeGenericType(operands.Pop(), operands.Pop())); // 编码时需按泛型参数列表从右向左编码。
                    break;
                case TypeOpCode.MakeTypedDeque:
                    if (operands.Count < 1) { return false; }
                    operands.Push(typeof(DurableDeque<>).MakeGenericType(operands.Pop()));
                    break;
                case TypeOpCode.MakeMixedOrderedDict:
                    if (operands.Count < 1) { return false; }
                    operands.Push(typeof(DurableOrderedDict<>).MakeGenericType(operands.Pop()));
                    break;
                case TypeOpCode.MakeTypedOrderedDict:
                    if (operands.Count < 2) { return false; }
                    operands.Push(typeof(DurableOrderedDict<,>).MakeGenericType(operands.Pop(), operands.Pop()));
                    break;

                case TypeOpCode.MakeValueTuple2:
                    if (operands.Count < 2) { return false; }
                    operands.Push(typeof(ValueTuple<,>).MakeGenericType(operands.Pop(), operands.Pop()));
                    break;
                case TypeOpCode.MakeValueTuple3:
                    if (operands.Count < 3) { return false; }
                    operands.Push(typeof(ValueTuple<,,>).MakeGenericType(operands.Pop(), operands.Pop(), operands.Pop()));
                    break;
                case TypeOpCode.MakeValueTuple4:
                    if (operands.Count < 4) { return false; }
                    operands.Push(typeof(ValueTuple<,,,>).MakeGenericType(operands.Pop(), operands.Pop(), operands.Pop(), operands.Pop()));
                    break;
                case TypeOpCode.MakeValueTuple5:
                    if (operands.Count < 5) { return false; }
                    operands.Push(typeof(ValueTuple<,,,,>).MakeGenericType(operands.Pop(), operands.Pop(), operands.Pop(), operands.Pop(), operands.Pop()));
                    break;
                case TypeOpCode.MakeValueTuple6:
                    if (operands.Count < 6) { return false; }
                    operands.Push(typeof(ValueTuple<,,,,,>).MakeGenericType(operands.Pop(), operands.Pop(), operands.Pop(), operands.Pop(), operands.Pop(), operands.Pop()));
                    break;
                case TypeOpCode.MakeValueTuple7:
                    if (operands.Count < 7) { return false; }
                    operands.Push(typeof(ValueTuple<,,,,,,>).MakeGenericType(operands.Pop(), operands.Pop(), operands.Pop(), operands.Pop(), operands.Pop(), operands.Pop(), operands.Pop()));
                    break;

                case TypeOpCode.Invalid:
                default:
                    return false;
            }
        }
        if (operands.Count != 1) { return false; }
        result = operands.Pop();
        return true;
    }
}
