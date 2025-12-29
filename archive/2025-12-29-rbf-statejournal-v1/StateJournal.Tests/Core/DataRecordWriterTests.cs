// Tests for DataRecordWriter: write + scan reverse roundtrip

using System.Buffers;
using Atelia.Rbf;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Core;

/// <summary>
/// DataRecordWriter 写入 + RbfScanner 逆向扫描回读测试。
/// </summary>
public class DataRecordWriterTests {
    // =========================================================================
    // Basic Roundtrip
    // =========================================================================

    /// <summary>
    /// 最小闭环：写入 3 条 DictVersion 记录，逆向扫描读回，验证顺序和内容。
    /// </summary>
    [Fact]
    public void AppendDictVersion_ThreeRecords_ScanReverseReadsAllInOrder() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new DataRecordWriter(framer);

        // 准备 3 条记录的数据
        var record1 = new TestRecord(PrevVersionPtr: 0, DiffPayload: [0x01, 0x02, 0x03]);
        var record2 = new TestRecord(PrevVersionPtr: 0, DiffPayload: [0x11, 0x12]); // 暂用 0，后面会用实际地址
        var record3 = new TestRecord(PrevVersionPtr: 0, DiffPayload: [0x21]);

        // Act - 写入 3 条记录（链式 prevVersionPtr）
        var addr1 = writer.AppendDictVersion(0, record1.DiffPayload.AsSpan()); // Base Version
        var addr2 = writer.AppendDictVersion(addr1.Value, record2.DiffPayload.AsSpan()); // 指向 record1
        var addr3 = writer.AppendDictVersion(addr2.Value, record3.DiffPayload.AsSpan()); // 指向 record2

        // Act - 逆向扫描读回
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        // Assert - 帧数量
        frames.Should().HaveCount(3);

        // Assert - 逆向顺序：record3, record2, record1
        // 验证 FrameTag
        frames[0].FrameTag.Should().Be(FrameTags.DictVersion.Value, "frame[0] should be record3");
        frames[1].FrameTag.Should().Be(FrameTags.DictVersion.Value, "frame[1] should be record2");
        frames[2].FrameTag.Should().Be(FrameTags.DictVersion.Value, "frame[2] should be record1");

        // 验证 payload 内容
        var payload3 = scanner.ReadPayload(frames[0]);
        var payload2 = scanner.ReadPayload(frames[1]);
        var payload1 = scanner.ReadPayload(frames[2]);

        // 解析 ObjectVersionRecord
        var result3 = ObjectVersionRecord.TryParse(payload3, out var prevPtr3, out var diff3);
        var result2 = ObjectVersionRecord.TryParse(payload2, out var prevPtr2, out var diff2);
        var result1 = ObjectVersionRecord.TryParse(payload1, out var prevPtr1, out var diff1);

        result3.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        result1.IsSuccess.Should().BeTrue();

        // 验证 PrevVersionPtr 链
        prevPtr1.Should().Be(0UL, "record1 is Base Version");
        prevPtr2.Should().Be(addr1.Value, "record2 points to record1");
        prevPtr3.Should().Be(addr2.Value, "record3 points to record2");

        // 验证 DiffPayload 内容
        diff1.ToArray().Should().BeEquivalentTo(record1.DiffPayload);
        diff2.ToArray().Should().BeEquivalentTo(record2.DiffPayload);
        diff3.ToArray().Should().BeEquivalentTo(record3.DiffPayload);
    }

    // =========================================================================
    // Single Record
    // =========================================================================

    [Fact]
    public void AppendDictVersion_SingleRecord_CanBeScannedBack() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new DataRecordWriter(framer);

        byte[] diffPayload = [0xAB, 0xCD, 0xEF, 0x12, 0x34];

        // Act
        var addr = writer.AppendDictVersion(0, diffPayload.AsSpan());

        // Assert - Address 有效
        addr.IsNull.Should().BeFalse();

        // Scan back
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        frames.Should().HaveCount(1);
        frames[0].FrameTag.Should().Be(FrameTags.DictVersion.Value);

        var payload = scanner.ReadPayload(frames[0]);
        var result = ObjectVersionRecord.TryParse(payload, out var prevPtr, out var diff);

        result.IsSuccess.Should().BeTrue();
        prevPtr.Should().Be(0UL);
        diff.ToArray().Should().BeEquivalentTo(diffPayload);
    }

    // =========================================================================
    // Empty DiffPayload
    // =========================================================================

    [Fact]
    public void AppendDictVersion_EmptyDiffPayload_Succeeds() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new DataRecordWriter(framer);

        // Act - Empty diff (valid for Checkpoint Base with empty dict)
        var addr = writer.AppendDictVersion(0, ReadOnlySpan<byte>.Empty);

        // Assert
        addr.IsNull.Should().BeFalse();

        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        frames.Should().HaveCount(1);

        var payload = scanner.ReadPayload(frames[0]);
        var result = ObjectVersionRecord.TryParse(payload, out var prevPtr, out var diff);

        result.IsSuccess.Should().BeTrue();
        prevPtr.Should().Be(0UL);
        diff.Length.Should().Be(0);
    }

    // =========================================================================
    // Memory Overload
    // =========================================================================

    [Fact]
    public void AppendDictVersion_MemoryOverload_WorksEquivalently() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new DataRecordWriter(framer);

        byte[] diffPayload = [0x01, 0x02, 0x03];
        ReadOnlyMemory<byte> diffMemory = diffPayload;

        // Act
        var addr = writer.AppendDictVersion(42, diffMemory);

        // Assert
        addr.IsNull.Should().BeFalse();

        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        frames.Should().HaveCount(1);

        var payload = scanner.ReadPayload(frames[0]);
        var result = ObjectVersionRecord.TryParse(payload, out var prevPtr, out var diff);

        result.IsSuccess.Should().BeTrue();
        prevPtr.Should().Be(42UL);
        diff.ToArray().Should().BeEquivalentTo(diffPayload);
    }

    // =========================================================================
    // TryReadAt Verification
    // =========================================================================

    [Fact]
    public void AppendDictVersion_ReturnedAddress_CanBeReadBack() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new DataRecordWriter(framer);

        byte[] diffPayload = [0x99, 0x88, 0x77];

        // Act
        var addr = writer.AppendDictVersion(0x123456789ABCDEF0UL, diffPayload.AsSpan());

        // Assert - 使用 TryReadAt 直接读取
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var success = scanner.TryReadAt(addr, out var frame);

        success.Should().BeTrue();
        frame.FrameTag.Should().Be(FrameTags.DictVersion.Value);

        var payload = scanner.ReadPayload(frame);
        var result = ObjectVersionRecord.TryParse(payload, out var prevPtr, out var diff);

        result.IsSuccess.Should().BeTrue();
        prevPtr.Should().Be(0x123456789ABCDEF0UL);
        diff.ToArray().Should().BeEquivalentTo(diffPayload);
    }

    // =========================================================================
    // Large DiffPayload
    // =========================================================================

    [Fact]
    public void AppendDictVersion_LargeDiffPayload_Succeeds() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new DataRecordWriter(framer);

        // 4 KB diff payload
        byte[] diffPayload = new byte[4096];
        for (int i = 0; i < diffPayload.Length; i++) {
            diffPayload[i] = (byte)(i & 0xFF);
        }

        // Act
        var addr = writer.AppendDictVersion(0, diffPayload.AsSpan());

        // Assert
        addr.IsNull.Should().BeFalse();

        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        frames.Should().HaveCount(1);

        var payload = scanner.ReadPayload(frames[0]);
        var result = ObjectVersionRecord.TryParse(payload, out var prevPtr, out var diff);

        result.IsSuccess.Should().BeTrue();
        prevPtr.Should().Be(0UL);
        diff.ToArray().Should().BeEquivalentTo(diffPayload);
    }

    // =========================================================================
    // Version Chain Traversal
    // =========================================================================

    [Fact]
    public void AppendDictVersion_VersionChain_CanBeTraversedBackward() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new DataRecordWriter(framer);

        // 写入 5 条链式记录
        var addr1 = writer.AppendDictVersion(0, new byte[] { 0x01 }.AsSpan());
        var addr2 = writer.AppendDictVersion(addr1.Value, new byte[] { 0x02 }.AsSpan());
        var addr3 = writer.AppendDictVersion(addr2.Value, new byte[] { 0x03 }.AsSpan());
        var addr4 = writer.AppendDictVersion(addr3.Value, new byte[] { 0x04 }.AsSpan());
        var addr5 = writer.AppendDictVersion(addr4.Value, new byte[] { 0x05 }.AsSpan());

        // Act - 从 addr5 开始回溯版本链
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var chain = new List<(ulong Ptr, byte[] Diff)>();

        var currentPtr = addr5.Value;
        while (currentPtr != 0) {
            var success = scanner.TryReadAt(Address64.FromOffset((long)currentPtr), out var frame);
            success.Should().BeTrue($"Should be able to read at {currentPtr}");

            var payload = scanner.ReadPayload(frame);
            var result = ObjectVersionRecord.TryParse(payload, out var prevPtr, out var diff);
            result.IsSuccess.Should().BeTrue();

            chain.Add((currentPtr, diff.ToArray()));
            currentPtr = prevPtr;
        }

        // Assert - 链长度和内容
        chain.Should().HaveCount(5);
        chain[0].Diff.Should().BeEquivalentTo(new byte[] { 0x05 });
        chain[1].Diff.Should().BeEquivalentTo(new byte[] { 0x04 });
        chain[2].Diff.Should().BeEquivalentTo(new byte[] { 0x03 });
        chain[3].Diff.Should().BeEquivalentTo(new byte[] { 0x02 });
        chain[4].Diff.Should().BeEquivalentTo(new byte[] { 0x01 });

        // 验证指针关系
        chain[0].Ptr.Should().Be(addr5.Value);
        chain[1].Ptr.Should().Be(addr4.Value);
        chain[2].Ptr.Should().Be(addr3.Value);
        chain[3].Ptr.Should().Be(addr2.Value);
        chain[4].Ptr.Should().Be(addr1.Value);
    }

    // =========================================================================
    // Flush
    // =========================================================================

    [Fact]
    public void Flush_DoesNotThrow() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new DataRecordWriter(framer);

        writer.AppendDictVersion(0, new byte[] { 0x01 }.AsSpan());

        // Act & Assert - 不抛异常
        var action = () => writer.Flush();
        action.Should().NotThrow();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private record TestRecord(ulong PrevVersionPtr, byte[] DiffPayload);
}
