using System.Diagnostics;

namespace Atelia.StateJournal.Internal;

partial struct ValueBox {
    internal void Write(IDiffWriter writer) {
        switch (GetLzc()) {
            case BoxLzc.InlineDouble:
                writer.TaggedFloatingPoint(DecodeInlineDouble());
                break;
            case BoxLzc.InlineNonnegInt:
                writer.TaggedNonnegativeInteger(DecodeInlineNonnegInt());
                break;
            case BoxLzc.InlineNegInt:
                writer.TaggedNegativeInteger(DecodeInlineNegInt());
                break;
            case BoxLzc.HeapSlot:
                WriteHeapValue(writer);
                break;
            case BoxLzc.Boolean:
                writer.TaggedBoolean(DecodeBoolean());
                break;
            case BoxLzc.Null:
                writer.TaggedNull();
                break;
            default:
                throw new UnreachableException();
        }
    }

    private void WriteHeapValue(IDiffWriter writer) {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        ValueKind valueKind = GetHeapKind();
        switch (valueKind) {
            case ValueKind.String:
                writer.TaggedString(DecodeString());
                break;
            case ValueKind.FloatingPoint:
                writer.TaggedFloatingPoint(DecodeHeapDouble());
                break;
            case ValueKind.NonnegativeInteger:
                writer.TaggedNonnegativeInteger(DecodeHeapNonnegInt());
                break;
            case ValueKind.NegativeInteger:
                writer.TaggedNegativeInteger(DecodeHeapNegInt());
                break;
            case ValueKind.MixedDict:
            case ValueKind.TypedDict:
            case ValueKind.MixedList:
            case ValueKind.TypedList:
                writer.TaggedDurableObjectRef(DecodeDurableObject().LocalId);
                break;
            case ValueKind.Uninitialized: // 未初始化的ValueBox不应该参与序列化
            case ValueKind.Null: // `null`值应该永远使用简单值而非堆分配
            case ValueKind.Boolean: // Boolean应该永远内联
            default:
                throw new UnreachableException();
        }
    }
}
