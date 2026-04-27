using System.Diagnostics;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

partial struct ValueBox {
    internal void Write(BinaryDiffWriter writer) {
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
            case BoxLzc.DurableRef:
                writer.TaggedDurableRef(DecodeDurableRef());
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

    private void WriteHeapValue(BinaryDiffWriter writer) {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        HeapValueKind valueKind = GetHeapKind();
        switch (valueKind) {
            case HeapValueKind.FloatingPoint:
                writer.TaggedFloatingPoint(DecodeHeapDouble());
                break;
            case HeapValueKind.NonnegativeInteger:
                writer.TaggedNonnegativeInteger(DecodeHeapNonnegInt());
                break;
            case HeapValueKind.NegativeInteger:
                writer.TaggedNegativeInteger(DecodeHeapNegInt());
                break;
            case HeapValueKind.Symbol:
                writer.TaggedSymbolId(DecodeSymbolId());
                break;
            case HeapValueKind.StringPayload:
                writer.TaggedString(ValuePools.OfOwnedString[GetHeapHandle()]);
                break;
            case HeapValueKind.BlobPayload:
                // CMS Step D: writer 仅读取 ByteString 字节流且 pool byte[] 由 face 独占，无 mutation 风险，走 FromTrustedOwned 跳过 ctor clone。
                writer.TaggedBlob(ByteString.FromTrustedOwned(ValuePools.OfOwnedBlob[GetHeapHandle()]));
                break;
            case HeapValueKind.Blank: // 未初始化的ValueBox不应该参与序列化
            default:
                throw new UnreachableException();
        }
    }
}
