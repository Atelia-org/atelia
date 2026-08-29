using System.Text;
using Atelia.MemoPod;

namespace Atelia.MemoPod.Tests.Prompt;

public sealed class MemoPodPromptJsonStringEncoderTests {
    [Fact]
    public void EncoderUsesLockedEscapesAndPreservesOtherScalars() {
        string value = "\0\u0001\u0007\b\t\n\u000b\f\r\u000e\u000f"
            + "\u0010\u001a\u001f\u007f\u0080\u008f\u0090\u009f"
            + "\u00a0\u2028\u2029\"\\/<> & é界😀";
        string expected =
            """\u0000\u0001\u0007\b\t\n\u000b\f\r\u000e\u000f\u0010\u001a\u001f\u007f\u0080\u008f\u0090\u009f"""
            + "\u00a0"
            + """\u2028\u2029\"\\/<> & é界😀""";
        int byteCount =
            MemoPodPromptJsonStringEncoder.GetEncodedUtf8ByteCount(
                value,
                nameof(value)
            );
        byte[] encoded = new byte[byteCount];

        int written = MemoPodPromptJsonStringEncoder.WriteEncodedUtf8(
            value,
            encoded,
            nameof(value)
        );

        Assert.Equal(byteCount, written);
        Assert.Equal(expected, Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void EncoderRejectsInvalidSurrogates() {
        string high = new((char)0xD800, 1);
        string low = new((char)0xDC00, 1);
        foreach (string value in new[] { high, low, "before" + high + "after" }) {
            Assert.Throws<ArgumentException>(() =>
                MemoPodPromptJsonStringEncoder.GetEncodedUtf8ByteCount(
                    value,
                    nameof(value)
                ));
            Assert.Throws<ArgumentException>(() =>
                MemoPodPromptJsonStringEncoder.WriteEncodedUtf8(
                    value,
                    new byte[64],
                    nameof(value)
                ));
        }
    }

    [Fact]
    public void EncoderRejectsShortDestination() {
        Assert.Throws<ArgumentException>(() =>
            MemoPodPromptJsonStringEncoder.WriteEncodedUtf8(
                "é",
                new byte[1],
                "value"
            ));
    }
}
