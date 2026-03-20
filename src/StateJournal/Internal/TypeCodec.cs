using System.Diagnostics.CodeAnalysis;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// 由于其涉及持久化数据，所以一经投入使用就不可再变更，届时将internal可见性改为public进行标记。
/// 在序列化时使用前导长度而非`return`操作码。
/// </summary>
internal enum TypeOpCode : byte {
    Invalid = 0,

    PushByte,
    PushUInt16,
    PushUInt32,
    PushUInt64,

    PushSByte,
    PushInt16,
    PushInt32,
    PushInt64,

    PushBoolean,
    PushHalf,
    PushSingle,
    PushDouble,

    PushString,
    PushMixedDeque,

    MakeMixedDict = 128,
    MakeTypedDict,
    MakeTypedDeque,
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
                case TypeOpCode.PushString:
                    operands.Push(typeof(string));
                    break;
                case TypeOpCode.PushMixedDeque:
                    operands.Push(typeof(DurableDeque));
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
