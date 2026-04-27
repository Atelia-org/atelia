using System.Buffers;
using System.IO;
using System.Text;
using Xunit;

namespace Atelia.StateJournal.Serialization.Tests;

public class TaggedStringPayloadTests {
    private static byte[] WriteTaggedString(string? value) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.TaggedString(value);
        return buffer.WrittenSpan.ToArray();
    }

    private static string ReadTaggedString(byte[] data) {
        var reader = new BinaryDiffReader(data);
        byte tag = reader.ReadTag();
        Assert.Equal(ScalarRules.StringPayload.Tag, tag);
        string value = reader.TaggedStringPayload();
        Assert.True(reader.End, "expected reader to fully consume the tagged string payload");
        return value;
    }

    [Fact]
    public void TaggedString_NonEmpty_RoundTrip() {
        byte[] data = WriteTaggedString("hello");
        Assert.Equal(ScalarRules.StringPayload.Tag, data[0]);
        Assert.Equal("hello", ReadTaggedString(data));
    }

    [Fact]
    public void TaggedString_Empty_RoundTrip() {
        byte[] data = WriteTaggedString(string.Empty);
        // 1 byte tag (0xC0) + 1 byte VarUInt header (0x00 → UTF-16LE, 0 bytes payload).
        Assert.Equal(new byte[] { ScalarRules.StringPayload.Tag, 0x00 }, data);
        Assert.Equal(string.Empty, ReadTaggedString(data));
    }

    [Fact]
    public void TaggedString_Null_WritesTaggedNull() {
        byte[] data = WriteTaggedString(null);
        Assert.Single(data);
        Assert.Equal(ScalarRules.Null, data[0]);
    }

    [Fact]
    public void TaggedString_Utf8_AsciiOptimal() {
        const string s = "abc";
        byte[] data = WriteTaggedString(s);
        // ASCII: UTF-8 (3) < UTF-16 (6) → 选 UTF-8 分支。
        // 1 (tag 0xC0) + 1 (VarUInt header = (3<<1)|1 = 0x07) + 3 (UTF-8 payload).
        Assert.Equal(1 + 1 + Encoding.UTF8.GetByteCount(s), data.Length);
        Assert.Equal(ScalarRules.StringPayload.Tag, data[0]);
        Assert.Equal((byte)((3 << 1) | 1), data[1]);
        Assert.Equal(s, ReadTaggedString(data));
    }

    [Fact]
    public void TaggedString_Utf16_NonBmp_OK() {
        // 中文 + emoji (surrogate pair)，验证 UTF-16 / UTF-8 自适应分支均能正确 round-trip。
        const string s = "你好🌟世界";
        byte[] data = WriteTaggedString(s);
        Assert.Equal(ScalarRules.StringPayload.Tag, data[0]);
        Assert.Equal(s, ReadTaggedString(data));
    }

    [Fact]
    public void TaggedString_Tag_IsC0() {
        byte[] data = WriteTaggedString("x");
        Assert.Equal(0xC0, data[0]);
    }

    [Fact]
    public void Dispatcher_StringPayloadTag_RoundTripsViaStringPayloadFace() {
        // B2: dispatcher 把 0xC0 路由到 ValueBox.StringPayloadFace；ValueBox 应携带等价 payload。
        byte[] data = WriteTaggedString("hello");
        var reader = new BinaryDiffReader(data);
        Internal.ValueBox box = Internal.ValueBox.Null;
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader, ref box);
        Assert.True(changed);
        Assert.Equal(ValueKind.String, box.GetValueKind());
        Assert.Equal(GetIssue.None, Internal.ValueBox.StringPayloadFace.Get(box, out string? value));
        Assert.Equal("hello", value);
        Internal.ValueBox.ReleaseOwnedHeapSlot(box);
    }
}
