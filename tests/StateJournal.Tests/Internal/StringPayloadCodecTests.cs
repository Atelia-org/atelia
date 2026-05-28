using System.Buffers;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class StringPayloadCodecTests {
    [Fact]
    public void WriteTo_Ascii_PrefersUtf8() {
        byte[] data = WriteFastPath("hello");

        Assert.Equal([0x0B, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'], data);
    }

    [Fact]
    public void WriteTo_Cjk_PrefersUtf16Le() {
        byte[] data = WriteFastPath("你好");

        Assert.Equal([0x04, 0x60, 0x4F, 0x7D, 0x59], data);
    }

    [Fact]
    public void ReadWrite_Utf16Le_RoundTrips_OnCompatibilityPath() {
        const string value = "你好，Atelia";

        byte[] data = WriteCompatibilityPath(value);
        string decoded = ReadCompatibilityPath(data);

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void CompatibilityPath_WritesSameUtf16LePayload_AsFastPath() {
        const string value = "你好";

        byte[] fastPath = WriteFastPath(value);
        byte[] compatibilityPath = WriteCompatibilityPath(value);

        Assert.Equal(fastPath, compatibilityPath);
    }

    [Fact]
    public void FastPathAndCompatibilityPath_CanReadEachOthersUtf16LePayload() {
        const string value = "你好，Atelia";

        byte[] fastPath = WriteFastPath(value);
        byte[] compatibilityPath = WriteCompatibilityPath(value);

        Assert.Equal(value, ReadCompatibilityPath(fastPath));
        Assert.Equal(value, ReadFastPath(compatibilityPath));
    }

    [Fact]
    public void ReadFrom_TruncatedUtf16LePayload_OnCompatibilityPath_ThrowsInvalidDataException() {
        Assert.Throws<InvalidDataException>(ReadTruncatedUtf16LePayloadOnCompatibilityPath);
    }

    [Fact]
    public void WriteNullableTo_Null_RoundTrips() {
        byte[] data = WriteNullableFastPath(null);
        Assert.Equal([0x00], data);
        Assert.Null(ReadNullableFastPath(data));
    }

    [Fact]
    public void WriteNullableTo_Empty_UsesHeaderOne() {
        byte[] data = WriteNullableFastPath(string.Empty);
        Assert.Equal([0x01], data);
        Assert.Equal(string.Empty, ReadNullableFastPath(data));
    }

    [Fact]
    public void WriteNullableTo_Ascii_PrefersUtf8_AndOffsetsHeaderByOne() {
        byte[] data = WriteNullableFastPath("hello");

        Assert.Equal([0x0C, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'], data);
        Assert.Equal("hello", ReadNullableFastPath(data));
    }

    [Fact]
    public void WriteNullableTo_CanRoundTrip_OnCompatibilityPath() {
        const string value = "你好，Atelia";

        byte[] data = WriteNullableCompatibilityPath(value);
        string? decoded = ReadNullableCompatibilityPath(data);

        Assert.Equal(value, decoded);
    }

    private readonly struct FixFastPath : StringPayloadCodec.IFastPathStrategy {
        public static bool IsFastPath => true;
    }

    private readonly struct FixCompatibilityPath : StringPayloadCodec.IFastPathStrategy {
        public static bool IsFastPath => false;
    }

    private static byte[] WriteFastPath(string value) {
        var buffer = new ArrayBufferWriter<byte>();
        StringPayloadCodec.WriteTo<FixFastPath>(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteCompatibilityPath(string value) {
        var buffer = new ArrayBufferWriter<byte>();
        StringPayloadCodec.WriteTo<FixCompatibilityPath>(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteNullableFastPath(string? value) {
        var buffer = new ArrayBufferWriter<byte>();
        StringPayloadCodec.WriteNullableTo<FixFastPath>(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteNullableCompatibilityPath(string? value) {
        var buffer = new ArrayBufferWriter<byte>();
        StringPayloadCodec.WriteNullableTo<FixCompatibilityPath>(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    private static string ReadFastPath(byte[] data) {
        var reader = new BinaryDiffReader(data);
        return StringPayloadCodec.ReadFrom<FixFastPath>(ref reader);
    }

    private static string ReadCompatibilityPath(byte[] data) {
        var reader = new BinaryDiffReader(data);
        return StringPayloadCodec.ReadFrom<FixCompatibilityPath>(ref reader);
    }

    private static string? ReadNullableFastPath(byte[] data) {
        var reader = new BinaryDiffReader(data);
        return StringPayloadCodec.ReadNullableFrom<FixFastPath>(ref reader);
    }

    private static string? ReadNullableCompatibilityPath(byte[] data) {
        var reader = new BinaryDiffReader(data);
        return StringPayloadCodec.ReadNullableFrom<FixCompatibilityPath>(ref reader);
    }

    private static void ReadTruncatedUtf16LePayloadOnCompatibilityPath() {
        var reader = new BinaryDiffReader([0x04, 0x60, 0x4F, 0x7D]);
        StringPayloadCodec.ReadFrom<FixCompatibilityPath>(ref reader);
    }
}
