using System.Buffers;
using Xunit;

namespace Atelia.StateJournal.Serialization.Tests;

public class TaggedFloatTests {
    [Fact]
    public void Write_ExactHalfValue_UsesTag2AndHalfPayload() {
        double value = 1.5;

        AssertEncoding(value, 3, ExpectedHalf(0xC2, 0x3E00));
    }

    [Fact]
    public void Write_NegativeZero_UsesTag2AndPreservesSignBit() {
        double value = BitConverter.UInt64BitsToDouble(0x8000_0000_0000_0000);

        AssertEncoding(value, 3, ExpectedHalf(0xC2, 0x8000));
    }

    [Fact]
    public void Write_PositiveInfinity_UsesTag2() {
        double value = double.PositiveInfinity;

        AssertEncoding(value, 3, ExpectedHalf(0xC2, 0x7C00));
    }

    [Fact]
    public void Write_ExactFloatButNotHalf_UsesTag4AndSinglePayload() {
        float source = BitConverter.UInt32BitsToSingle(0x3F80_0001);
        double value = source;

        AssertEncoding(value, 5, ExpectedSingle(0xC4, 0x3F80_0001));
    }

    [Fact]
    public void Write_NonFloatDouble_UsesTag8AndRawDoubleBits() {
        ulong bits = 0x3FF0_0000_0000_0001;
        double value = BitConverter.UInt64BitsToDouble(bits);

        AssertEncoding(value, 9, ExpectedDouble(0xC8, bits));
    }

    [Fact]
    public void Write_NaN_UsesTag8AndPreservesPayload() {
        ulong bits = 0x7FF8_0000_0000_0001;
        double value = BitConverter.UInt64BitsToDouble(bits);

        AssertEncoding(value, 9, ExpectedDouble(0xC8, bits));
    }

    [Fact]
    public void Write_SignalingNaN_AlsoUsesTag8AndPreservesPayload() {
        ulong bits = 0x7FF0_0000_0000_0001;
        double value = BitConverter.UInt64BitsToDouble(bits);

        AssertEncoding(value, 9, ExpectedDouble(0xC8, bits));
    }

    [Fact]
    public void Write_ScalarRules_HalfValue_UsesCborMajorSevenTag() {
        var writer = new ArrayBufferWriter<byte>(3);

        TaggedFloat<ScalarRules.FloatingPoint>.Write(writer, 1.5);

        Assert.Equal([0xF9, 0x00, 0x3E], writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Write_ScalarRules_NaN_PreservesDoublePayload() {
        ulong bits = 0x7FF8_0000_0000_0001;
        double value = BitConverter.UInt64BitsToDouble(bits);
        var writer = new ArrayBufferWriter<byte>(9);

        TaggedFloat<ScalarRules.FloatingPoint>.Write(writer, value);

        Assert.Equal([0xFB, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x7F], writer.WrittenSpan.ToArray());
    }

    private static void AssertEncoding(double value, int expectedLength, byte[] expectedBytes) {
        var writer = new ArrayBufferWriter<byte>(expectedLength);

        int written = TaggedFloat<SampleRule>.Write(writer, value);

        Assert.Equal(expectedLength, written);
        Assert.Equal(expectedLength, writer.WrittenCount);
        Assert.Equal(expectedBytes, writer.WrittenSpan.ToArray());
    }

    private static byte[] ExpectedHalf(byte tag, ushort bits) =>
        [tag, (byte)bits, (byte)(bits >> 8)];

    private static byte[] ExpectedSingle(byte tag, uint bits) =>
        [tag, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24)];

    private static byte[] ExpectedDouble(byte tag, ulong bits) =>
        [tag, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24), (byte)(bits >> 32), (byte)(bits >> 40), (byte)(bits >> 48), (byte)(bits >> 56)];

    private readonly struct SampleRule : ITaggedFloatRule {
        public static byte Tag2 => 0xC2;
        public static byte Tag4 => 0xC4;
        public static byte Tag8 => 0xC8;
    }
}
