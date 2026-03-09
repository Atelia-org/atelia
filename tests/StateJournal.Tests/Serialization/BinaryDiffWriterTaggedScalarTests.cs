using System.Buffers;
using Xunit;

namespace Atelia.StateJournal.Serialization.Tests;

public class BinaryDiffWriterTaggedScalarTests {
    public static TheoryData<ulong, byte[]> NonnegativeIntegerCases => new() {
        { 0, [0x00] },
        { 23, [0x17] },
        { 24, [0x18, 0x18] },
        { 255, [0x18, 0xFF] },
        { 256, [0x19, 0x00, 0x01] },
    };

    public static TheoryData<long, byte[]> NegativeIntegerCases => new() {
        { -1, [0x20] },
        { -24, [0x37] },
        { -25, [0x38, 0x18] },
        { -256, [0x38, 0xFF] },
        { -257, [0x39, 0x00, 0x01] },
    };

    [Theory]
    [InlineData(false, new byte[] { 0xF4 })]
    [InlineData(true, new byte[] { 0xF5 })]
    public void TaggedBoolean_WritesExpectedSimpleValue(bool value, byte[] expectedBytes) {
        var writer = new ArrayBufferWriter<byte>(expectedBytes.Length);
        var diffWriter = new BinaryDiffWriter(writer);

        diffWriter.TaggedBoolean(value);

        Assert.Equal(expectedBytes, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void TaggedNull_WritesExpectedSimpleValue() {
        var writer = new ArrayBufferWriter<byte>(1);
        var diffWriter = new BinaryDiffWriter(writer);

        diffWriter.TaggedNull();

        Assert.Equal([0xF6], writer.WrittenSpan.ToArray());
    }

    [Theory]
    [MemberData(nameof(NonnegativeIntegerCases))]
    public void TaggedNonnegativeInteger_WritesHead(ulong value, byte[] expectedBytes) {
        var writer = new ArrayBufferWriter<byte>(expectedBytes.Length);
        var diffWriter = new BinaryDiffWriter(writer);

        diffWriter.TaggedNonnegativeInteger(value);

        Assert.Equal(expectedBytes, writer.WrittenSpan.ToArray());
    }

    [Theory]
    [MemberData(nameof(NegativeIntegerCases))]
    public void TaggedNegativeInteger_WritesHead(long value, byte[] expectedBytes) {
        var writer = new ArrayBufferWriter<byte>(expectedBytes.Length);
        var diffWriter = new BinaryDiffWriter(writer);

        diffWriter.TaggedNegativeInteger(value);

        Assert.Equal(expectedBytes, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void TaggedFloatingPoint_ExactHalfValue_UsesMajorTypeSevenHalfHead() {
        var writer = new ArrayBufferWriter<byte>(3);
        var diffWriter = new BinaryDiffWriter(writer);

        diffWriter.TaggedFloatingPoint(1.5);

        Assert.Equal([0xF9, 0x00, 0x3E], writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void TaggedFloatingPoint_NaN_PreservesDoublePayload() {
        ulong bits = 0x7FF8_0000_0000_0001;
        double value = BitConverter.UInt64BitsToDouble(bits);
        var writer = new ArrayBufferWriter<byte>(9);
        var diffWriter = new BinaryDiffWriter(writer);

        diffWriter.TaggedFloatingPoint(value);

        Assert.Equal([0xFB, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x7F], writer.WrittenSpan.ToArray());
    }
}
