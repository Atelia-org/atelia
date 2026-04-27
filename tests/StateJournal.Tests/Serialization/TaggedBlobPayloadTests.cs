using System;
using System.Buffers;
using Xunit;

namespace Atelia.StateJournal.Serialization.Tests;

public class TaggedBlobPayloadTests {
    private static byte[] WriteTaggedBlob(ByteString value) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.TaggedBlob(value);
        return buffer.WrittenSpan.ToArray();
    }

    private static ByteString ReadTaggedBlob(byte[] data) {
        var reader = new BinaryDiffReader(data);
        byte tag = reader.ReadTag();
        Assert.Equal(ScalarRules.BlobPayload.Tag, tag);
        ByteString value = reader.TaggedBlobPayload();
        Assert.True(reader.End, "expected reader to fully consume the tagged blob payload");
        return value;
    }

    private static byte[] WriteBareBlob(ReadOnlySpan<byte> value) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.BareBlobPayload(value, asKey: false);
        return buffer.WrittenSpan.ToArray();
    }

    private static ByteString ReadBareBlob(byte[] data) {
        var reader = new BinaryDiffReader(data);
        ByteString value = reader.BareBlobPayload(asKey: false);
        Assert.True(reader.End, "expected reader to fully consume the bare blob payload");
        return value;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(1024)]
    [InlineData(16384)]
    public void BareBlobPayload_RoundTrip_VariousLengths(int length) {
        byte[] payload = new byte[length];
        for (int i = 0; i < length; i++) { payload[i] = (byte)(i * 31 + 7); }

        byte[] data = WriteBareBlob(payload);
        ByteString decoded = ReadBareBlob(data);
        Assert.Equal(length, decoded.Length);
        Assert.True(decoded.AsSpan().SequenceEqual(payload));
    }

    [Fact]
    public void TaggedBlob_NonEmpty_RoundTrip() {
        var input = new ByteString(new byte[] { 1, 2, 3, 4, 5 });
        byte[] data = WriteTaggedBlob(input);
        Assert.Equal(ScalarRules.BlobPayload.Tag, data[0]);
        ByteString decoded = ReadTaggedBlob(data);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void TaggedBlob_Empty_EncodesAsTagPlusZeroLength() {
        byte[] data = WriteTaggedBlob(ByteString.Empty);
        // 1 byte tag (0xC1) + 1 byte VarUInt(0) header.
        Assert.Equal(new byte[] { ScalarRules.BlobPayload.Tag, 0x00 }, data);
        ByteString decoded = ReadTaggedBlob(data);
        Assert.True(decoded.IsEmpty);
        Assert.Equal(ByteString.Empty, decoded);
    }

    [Fact]
    public void TaggedBlob_Tag_IsC1() {
        byte[] data = WriteTaggedBlob(new ByteString(new byte[] { 0xAA }));
        Assert.Equal(0xC1, data[0]);
    }

    [Fact]
    public void TaggedBlob_PayloadContainingZeros_RoundTrip() {
        // 验证 VarInt 长度前缀准确，payload 内含 0x00 字节不会被截断。
        var input = new ByteString(new byte[] { 0x00, 0x01, 0x00, 0xFF, 0x00 });
        byte[] data = WriteTaggedBlob(input);
        ByteString decoded = ReadTaggedBlob(data);
        Assert.Equal(input, decoded);
        Assert.Equal(5, decoded.Length);
    }

    [Fact]
    public void TaggedBlob_HighEntropyBytes_RoundTrip() {
        var input = new ByteString(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE });
        byte[] data = WriteTaggedBlob(input);
        ByteString decoded = ReadTaggedBlob(data);
        Assert.True(decoded.AsSpan().SequenceEqual(input.AsSpan()));
    }

    [Fact]
    public void Dispatcher_BlobPayloadTag_RoutesToBlobPayloadFace() {
        // CMS Step 3b 起 dispatcher 0xC1 真正路由到 ValueBox.BlobPayloadFace。
        // BinaryDiffReader 是 ref struct，不能被 lambda 捕获，所以直接顺序调用。
        var input = new ByteString(new byte[] { 1, 2, 3 });
        byte[] data = WriteTaggedBlob(input);
        var reader = new BinaryDiffReader(data);
        Internal.ValueBox box = Internal.ValueBox.Null;
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader, ref box);
        try {
            Assert.True(changed);
            Assert.Equal(ValueKind.Blob, box.GetValueKind());
            Assert.Equal(GetIssue.None, Internal.ValueBox.BlobPayloadFace.Get(box, out ByteString decoded));
            Assert.Equal(input, decoded);
        }
        finally { Internal.ValueBox.ReleaseOwnedHeapSlot(box); }
    }
}
