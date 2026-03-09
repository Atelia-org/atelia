using System.Buffers;
using Xunit;

namespace Atelia.StateJournal.Serialization.Tests;

public class TaggedIntTests {
    public static TheoryData<ulong, int, byte[]> NonnegativeCases => new() {
        { 0, 1, [0xA0] },
        { 3, 1, [0xA3] },
        { 4, 2, [0xB1, 0x04] },
        { byte.MaxValue, 2, [0xB1, 0xFF] },
        { (ulong)byte.MaxValue + 1, 3, [0xB2, 0x00, 0x01] },
        { ushort.MaxValue, 3, [0xB2, 0xFF, 0xFF] },
        { (ulong)ushort.MaxValue + 1, 5, [0xB4, 0x00, 0x00, 0x01, 0x00] },
        { uint.MaxValue, 5, [0xB4, 0xFF, 0xFF, 0xFF, 0xFF] },
        { (ulong)uint.MaxValue + 1, 9, [0xB8, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00] },
        { ulong.MaxValue, 9, [0xB8, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF] },
    };

    public static TheoryData<long, int, byte[]> NegativeCases => new() {
        { -1, 1, [0xA0] },
        { -2, 1, [0xA1] },
        { -4, 1, [0xA3] },
        { -5, 2, [0xB1, 0x04] },
        { -256, 2, [0xB1, 0xFF] },
        { -257, 3, [0xB2, 0x00, 0x01] },
        { -65536, 3, [0xB2, 0xFF, 0xFF] },
        { -65537, 5, [0xB4, 0x00, 0x00, 0x01, 0x00] },
        { long.MinValue, 9, [0xB8, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F] },
    };

    [Theory]
    [MemberData(nameof(NonnegativeCases))]
    public void WriteNonnegative_EncodesExpectedLayout(ulong value, int expectedLength, byte[] expectedBytes) {
        var writer = new ArrayBufferWriter<byte>(expectedLength);

        int written = TaggedInt.WriteNonnegative<SampleRule>(writer, value);

        Assert.Equal(expectedLength, TaggedInt.GetCodewordLength<SampleRule>(value));
        Assert.Equal(expectedLength, written);
        Assert.Equal(expectedBytes, writer.WrittenSpan.ToArray());
    }

    [Theory]
    [MemberData(nameof(NegativeCases))]
    public void WriteNegative_EncodesCborStyleNegativeArgument(long value, int expectedLength, byte[] expectedBytes) {
        var writer = new ArrayBufferWriter<byte>(expectedLength);

        int written = TaggedInt.WriteNegative<SampleRule>(writer, value);

        Assert.Equal(expectedLength, written);
        Assert.Equal(expectedBytes, writer.WrittenSpan.ToArray());
    }

    public static TheoryData<ulong, byte[]> CborNonnegativeCases => new() {
        { 0, [0x00] },
        { 23, [0x17] },
        { 24, [0x18, 0x18] },
        { 255, [0x18, 0xFF] },
        { 256, [0x19, 0x00, 0x01] },
    };

    public static TheoryData<long, byte[]> CborNegativeCases => new() {
        { -1, [0x20] },
        { -24, [0x37] },
        { -25, [0x38, 0x18] },
        { -256, [0x38, 0xFF] },
        { -257, [0x39, 0x00, 0x01] },
    };

    [Theory]
    [MemberData(nameof(CborNonnegativeCases))]
    public void WriteNonnegative_ScalarRules_ProducesCborStandardEncoding(ulong value, byte[] expectedBytes) {
        var writer = new ArrayBufferWriter<byte>(expectedBytes.Length);

        TaggedInt.WriteNonnegative<ScalarRules.NonnegativeInteger>(writer, value);

        Assert.Equal(expectedBytes, writer.WrittenSpan.ToArray());
    }

    [Theory]
    [MemberData(nameof(CborNegativeCases))]
    public void WriteNegative_ScalarRules_ProducesCborStandardEncoding(long value, byte[] expectedBytes) {
        var writer = new ArrayBufferWriter<byte>(expectedBytes.Length);

        TaggedInt.WriteNegative<ScalarRules.NegativeInteger>(writer, value);

        Assert.Equal(expectedBytes, writer.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void WriteNegative_RejectsNonnegativeInput(long value) {
        var writer = new ArrayBufferWriter<byte>(TaggedInt.TagLen + 1);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => TaggedInt.WriteNegative<SampleRule>(writer, value));

        Assert.Equal("negValue", ex.ParamName);
        Assert.Equal(0, writer.WrittenCount);
    }

    private readonly struct SampleRule : ITaggedIntRule {
        public static ulong TagOnlyMaxValue => 3;
        public static byte EncodeTagOnly(ulong value) {
            if (value > TagOnlyMaxValue) { throw new ArgumentOutOfRangeException(nameof(value), value, "Value is outside the tag-only range."); }

            return (byte)(0xA0 + value);
        }
        public static byte Tag1 => 0xB1;
        public static byte Tag2 => 0xB2;
        public static byte Tag4 => 0xB4;
        public static byte Tag8 => 0xB8;
    }
}
