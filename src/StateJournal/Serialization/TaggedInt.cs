using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace Atelia.StateJournal.Serialization;

internal interface ITaggedIntRule {
    static abstract ulong TagOnlyMaxValue { get; }
    static abstract byte EncodeTagOnly(ulong value);
    static abstract byte Tag1 { get; }
    static abstract byte Tag2 { get; }
    static abstract byte Tag4 { get; }
    static abstract byte Tag8 { get; }
}

internal static class TaggedInt {
    internal const int TagLen = 1;

    internal static int GetCodewordLength<Rule>(ulong value) where Rule : unmanaged, ITaggedIntRule {
        return value <= byte.MaxValue
            ? value <= Rule.TagOnlyMaxValue ? TagLen + 0 : TagLen + 1
            : value <= ushort.MaxValue ? TagLen + 2
            : value <= uint.MaxValue ? TagLen + 4
            : TagLen + 8;
    }

    internal static int WriteNonnegative<Rule>(IBufferWriter<byte> writer, ulong value) where Rule : unmanaged, ITaggedIntRule {
        int written;
        if (value <= byte.MaxValue) {
            if (value <= Rule.TagOnlyMaxValue) {
                writer.GetSpan(TagLen)[0] = Rule.EncodeTagOnly(value);
                written = TagLen + 0;
            }
            else {
                var destSpan = writer.GetSpan(TagLen + 1);
                destSpan[0] = Rule.Tag1;
                destSpan[1] = (byte)value;
                written = TagLen + 1;
            }
        }
        else if (value <= ushort.MaxValue) {
            var destSpan = writer.GetSpan(TagLen + 2);
            destSpan[0] = Rule.Tag2;
            BinaryPrimitives.WriteUInt16LittleEndian(destSpan[1..], (ushort)value);
            written = TagLen + 2;
        }
        else if (value <= uint.MaxValue) {
            var destSpan = writer.GetSpan(TagLen + 4);
            destSpan[0] = Rule.Tag4;
            BinaryPrimitives.WriteUInt32LittleEndian(destSpan[1..], (uint)value);
            written = TagLen + 4;
        }
        else {
            var destSpan = writer.GetSpan(TagLen + 8);
            destSpan[0] = Rule.Tag8;
            BinaryPrimitives.WriteUInt64LittleEndian(destSpan[1..], value);
            written = TagLen + 8;
        }
        writer.Advance(written);
        return written;
    }

    internal static int WriteNegative<NegRule>(IBufferWriter<byte> writer, long negValue) where NegRule : unmanaged, ITaggedIntRule {
        if (negValue >= 0) { throw new ArgumentOutOfRangeException(nameof(negValue), negValue, "Expected a negative value."); }
        Debug.Assert(negValue < 0);
        return WriteNonnegative<NegRule>(writer, unchecked((ulong)(-1 - negValue)));
    }
}
