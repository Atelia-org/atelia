using System.Buffers;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class InlineStringTests {
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

    private readonly struct FixFastPath : InlineString.IFastPathStrategy {
        public static bool IsFastPath => true;
    }

    private readonly struct FixCompatibilityPath : InlineString.IFastPathStrategy {
        public static bool IsFastPath => false;
    }

    private static byte[] WriteFastPath(string value) {
        var buffer = new ArrayBufferWriter<byte>();
        InlineString.WriteTo<FixFastPath>(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WriteCompatibilityPath(string value) {
        var buffer = new ArrayBufferWriter<byte>();
        InlineString.WriteTo<FixCompatibilityPath>(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    private static string ReadFastPath(byte[] data) {
        var reader = new BinaryDiffReader(data);
        return InlineString.ReadFrom<FixFastPath>(ref reader);
    }

    private static string ReadCompatibilityPath(byte[] data) {
        var reader = new BinaryDiffReader(data);
        return InlineString.ReadFrom<FixCompatibilityPath>(ref reader);
    }

    private static void ReadTruncatedUtf16LePayloadOnCompatibilityPath() {
        var reader = new BinaryDiffReader([0x04, 0x60, 0x4F, 0x7D]);
        InlineString.ReadFrom<FixCompatibilityPath>(ref reader);
    }
}
