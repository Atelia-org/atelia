using System.Buffers;
using Xunit;

namespace Atelia.StateJournal.Serialization.Tests;

public class VarIntTests {
    public static TheoryData<byte[]> InvalidBase128UInt64Inputs => [
        [],
        [0x80],
        [0x80, 0x00],
        [0x81, 0x00],
        [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
        [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80],
        [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02],
        [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80],
    ];

    [Fact]
    public void UInt64CanonicalValueRoundTrips() {
        foreach (ulong expected in EnumerateCanonicalValues()) {
            var writer = new ArrayBufferWriter<byte>(VarInt.MaxLength64);
            int written = VarInt.WriteUInt64(writer, expected);
            byte[] encoded = writer.WrittenSpan.ToArray();

            AssertSameUInt64(encoded, written, expected);
            AssertSameUInt64([.. encoded, 0xCC, 0x80], written, expected);
        }
    }

    [Fact]
    public void UInt32CanonicalValueRoundTrips() {
        foreach (uint expected in EnumerateCanonicalUInt32Values()) {
            var writer = new ArrayBufferWriter<byte>(VarInt.MaxLength64);
            int written = VarInt.WriteUInt32(writer, expected);
            byte[] encoded = writer.WrittenSpan.ToArray();

            AssertSameUInt32(encoded, written, expected);
            AssertSameUInt32([.. encoded, 0xCC, 0x80], written, expected);
        }
    }

    [Fact]
    public void UInt16CanonicalValueRoundTrips() {
        foreach (ushort expected in EnumerateCanonicalUInt16Values()) {
            var writer = new ArrayBufferWriter<byte>(VarInt.MaxLength64);
            int written = VarInt.WriteUInt16(writer, expected);
            byte[] encoded = writer.WrittenSpan.ToArray();

            AssertSameUInt16(encoded, written, expected);
            AssertSameUInt16([.. encoded, 0xCC, 0x80], written, expected);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidBase128UInt64Inputs))]
    public void ReadUInt64InvalidInputs(byte[] source) {
        int readLoop = VarInt.ReadUInt64(source, out ulong valueLoop);

        Assert.True(readLoop < 0);
    }

    [Theory]
    [MemberData(nameof(InvalidBase128UInt64Inputs))]
    public void ReadUInt32InvalidInputs(byte[] source) {
        int readLoop = VarInt.ReadUInt32(source, out uint valueLoop);

        Assert.True(readLoop < 0);
    }

    [Theory]
    [MemberData(nameof(InvalidBase128UInt64Inputs))]
    public void ReadUInt16InvalidInputs(byte[] source) {
        int readLoop = VarInt.ReadUInt16(source, out ushort valueLoop);

        Assert.True(readLoop < 0);
    }

    [Fact]
    public void ReadUInt32OverflowsAboveTypeRange() {
        var writer = new ArrayBufferWriter<byte>(VarInt.MaxLength64);
        VarInt.WriteUInt64(writer, (ulong)uint.MaxValue + 1);

        int read = VarInt.ReadUInt32(writer.WrittenSpan, out uint value);

        Assert.Equal(-(int)VarInt.ErrorCode.Overflow, read);
        Assert.Equal(default, value);
    }

    [Fact]
    public void ReadUInt16OverflowsAboveTypeRange() {
        var writer = new ArrayBufferWriter<byte>(VarInt.MaxLength64);
        VarInt.WriteUInt64(writer, (ulong)ushort.MaxValue + 1);

        int read = VarInt.ReadUInt16(writer.WrittenSpan, out ushort value);

        Assert.Equal(-(int)VarInt.ErrorCode.Overflow, read);
        Assert.Equal(default, value);
    }

    [Fact]
    public void ZigZag64RoundTrips() {
        foreach (long expected in EnumerateSignedInt64Values()) {
            ulong encoded = VarInt.ZigZagEncode64(expected);
            long decoded = VarInt.ZigZagDecode64(encoded);

            Assert.Equal(expected, decoded);
        }
    }

    [Fact]
    public void ZigZag32RoundTrips() {
        foreach (int expected in EnumerateSignedInt32Values()) {
            uint encoded = VarInt.ZigZagEncode32(expected);
            int decoded = VarInt.ZigZagDecode32(encoded);

            Assert.Equal(expected, decoded);
        }
    }

    [Fact]
    public void ZigZag16RoundTrips() {
        foreach (short expected in EnumerateSignedInt16Values()) {
            ushort encoded = VarInt.ZigZagEncode16(expected);
            short decoded = VarInt.ZigZagDecode16(encoded);

            Assert.Equal(expected, decoded);
        }
    }

    [Fact]
    public void Int64CanonicalValueRoundTrips() {
        foreach (long expected in EnumerateSignedInt64Values()) {
            var writer = new ArrayBufferWriter<byte>(VarInt.MaxLength64);
            int written = VarInt.WriteInt64(writer, expected);
            byte[] encoded = writer.WrittenSpan.ToArray();

            AssertSameInt64(encoded, written, expected);
            AssertSameInt64([.. encoded, 0xCC, 0x80], written, expected);
        }
    }

    [Fact]
    public void Int32CanonicalValueRoundTrips() {
        foreach (int expected in EnumerateSignedInt32Values()) {
            var writer = new ArrayBufferWriter<byte>(VarInt.MaxLength32);
            int written = VarInt.WriteInt32(writer, expected);
            byte[] encoded = writer.WrittenSpan.ToArray();

            AssertSameInt32(encoded, written, expected);
            AssertSameInt32([.. encoded, 0xCC, 0x80], written, expected);
        }
    }

    [Fact]
    public void Int16CanonicalValueRoundTrips() {
        foreach (short expected in EnumerateSignedInt16Values()) {
            var writer = new ArrayBufferWriter<byte>(VarInt.MaxLength16);
            int written = VarInt.WriteInt16(writer, expected);
            byte[] encoded = writer.WrittenSpan.ToArray();

            AssertSameInt16(encoded, written, expected);
            AssertSameInt16([.. encoded, 0xCC, 0x80], written, expected);
        }
    }

    private static IEnumerable<ulong> EnumerateCanonicalValues() {
        for (ulong value = 0; value <= 100_000; value++) {
            yield return value;
        }

        yield return ulong.MaxValue;

        for (int bits = 7; bits <= 63; bits += 7) {
            ulong threshold = 1UL << bits;
            yield return threshold - 2;
            yield return threshold - 1;
            yield return threshold;
            if (threshold < ulong.MaxValue) {
                yield return threshold + 1;
            }
        }
    }

    private static IEnumerable<uint> EnumerateCanonicalUInt32Values() {
        for (uint value = 0; value <= 100_000; value++) {
            yield return value;
        }

        yield return uint.MaxValue;

        for (int bits = 7; bits <= 28; bits += 7) {
            uint threshold = 1U << bits;
            yield return threshold - 2;
            yield return threshold - 1;
            yield return threshold;
            if (threshold < uint.MaxValue) {
                yield return threshold + 1;
            }
        }
    }

    private static IEnumerable<ushort> EnumerateCanonicalUInt16Values() {
        for (ushort value = 0; value <= 10_000; value++) {
            yield return value;
        }

        yield return ushort.MaxValue;

        for (int bits = 7; bits <= 14; bits += 7) {
            uint threshold = 1U << bits;
            yield return (ushort)(threshold - 2);
            yield return (ushort)(threshold - 1);
            yield return (ushort)threshold;
            if (threshold < ushort.MaxValue) {
                yield return (ushort)(threshold + 1);
            }
        }
    }

    private static IEnumerable<long> EnumerateSignedInt64Values() {
        for (long value = -100_000; value <= 100_000; value++) {
            yield return value;
        }

        yield return long.MinValue;
        yield return long.MaxValue;

        for (int bits = 0; bits <= 62; bits += 7) {
            long threshold = 1L << bits;
            yield return -threshold - 1;
            yield return -threshold;
            yield return -threshold + 1;
            yield return threshold - 1;
            yield return threshold;
            if (threshold < long.MaxValue) {
                yield return threshold + 1;
            }
        }
    }

    private static IEnumerable<int> EnumerateSignedInt32Values() {
        for (int value = -100_000; value <= 100_000; value++) {
            yield return value;
        }

        yield return int.MinValue;
        yield return int.MaxValue;

        for (int bits = 0; bits <= 28; bits += 7) {
            int threshold = 1 << bits;
            yield return -threshold - 1;
            yield return -threshold;
            yield return -threshold + 1;
            yield return threshold - 1;
            yield return threshold;
            if (threshold < int.MaxValue) {
                yield return threshold + 1;
            }
        }
    }

    private static IEnumerable<short> EnumerateSignedInt16Values() {
        for (int value = -10_000; value <= 10_000; value++) {
            yield return (short)value;
        }

        yield return short.MinValue;
        yield return short.MaxValue;

        for (int bits = 0; bits <= 14; bits += 7) {
            int threshold = 1 << bits;
            yield return (short)(-threshold - 1);
            yield return (short)(-threshold);
            yield return (short)(-threshold + 1);
            yield return (short)(threshold - 1);
            yield return (short)(threshold);
            if (threshold < short.MaxValue) {
                yield return (short)(threshold + 1);
            }
        }
    }

    private static void AssertSameUInt64(byte[] source, int expectedBytesConsumed, ulong expectedValue) {
        int readLoop = VarInt.ReadUInt64(source, out ulong valueLoop);

        Assert.Equal(expectedBytesConsumed, readLoop);
        Assert.Equal(expectedValue, valueLoop);
    }

    private static void AssertSameUInt32(byte[] source, int expectedBytesConsumed, uint expectedValue) {
        int readLoop = VarInt.ReadUInt32(source, out uint valueLoop);

        Assert.Equal(expectedBytesConsumed, readLoop);
        Assert.Equal(expectedValue, valueLoop);
    }

    private static void AssertSameUInt16(byte[] source, int expectedBytesConsumed, ushort expectedValue) {
        int readLoop = VarInt.ReadUInt16(source, out ushort valueLoop);

        Assert.Equal(expectedBytesConsumed, readLoop);
        Assert.Equal(expectedValue, valueLoop);
    }

    private static void AssertSameInt64(byte[] source, int expectedBytesConsumed, long expectedValue) {
        int readLoop = VarInt.ReadInt64(source, out long valueLoop);

        Assert.Equal(expectedBytesConsumed, readLoop);
        Assert.Equal(expectedValue, valueLoop);
    }

    private static void AssertSameInt32(byte[] source, int expectedBytesConsumed, int expectedValue) {
        int readLoop = VarInt.ReadInt32(source, out int valueLoop);

        Assert.Equal(expectedBytesConsumed, readLoop);
        Assert.Equal(expectedValue, valueLoop);
    }

    private static void AssertSameInt16(byte[] source, int expectedBytesConsumed, short expectedValue) {
        int readLoop = VarInt.ReadInt16(source, out short valueLoop);

        Assert.Equal(expectedBytesConsumed, readLoop);
        Assert.Equal(expectedValue, valueLoop);
    }
}
