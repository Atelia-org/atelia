using System.Buffers;
using System.Buffers.Binary;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Serialization;

internal interface ITaggedFloatRule {
    static abstract byte Tag2 { get; }
    static abstract byte Tag4 { get; }
    static abstract byte Tag8 { get; }
}

// ai:test `tests/StateJournal.Tests/Serialization/TaggedFloatTests.cs`
internal static class TaggedFloat<Tags> where Tags : ITaggedFloatRule {
    internal const int TagLen = 1;
    internal static int Write(IBufferWriter<byte> writer, double value) {
        int written = WriteCore(writer, value);
        writer.Advance(written);
        return written;
    }

    static int WriteCore(IBufferWriter<byte> writer, double value) {
        ulong doubleBits = BitConverter.DoubleToUInt64Bits(value);
        if (!ValueBox.IsNaNBits(doubleBits)) {
            {
                Half narrowed = (Half)value;
                if (BitConverter.DoubleToUInt64Bits((double)narrowed) == doubleBits) {
                    var destSpan = writer.GetSpan(TagLen + 2);
                    destSpan[0] = Tags.Tag2;
                    BinaryPrimitives.WriteHalfLittleEndian(destSpan[1..], narrowed);
                    return TagLen + 2;
                }
            }
            {
                float narrowed = (float)value;
                if (BitConverter.DoubleToUInt64Bits((double)narrowed) == doubleBits) {
                    var destSpan = writer.GetSpan(TagLen + 4);
                    destSpan[0] = Tags.Tag4;
                    BinaryPrimitives.WriteSingleLittleEndian(destSpan[1..], narrowed);
                    return TagLen + 4;
                }
            }
        }
        {
            var destSpan = writer.GetSpan(TagLen + 8);
            destSpan[0] = Tags.Tag8;
            BinaryPrimitives.WriteUInt64LittleEndian(destSpan[1..], doubleBits);
            return TagLen + 8;
        }
    }
}
