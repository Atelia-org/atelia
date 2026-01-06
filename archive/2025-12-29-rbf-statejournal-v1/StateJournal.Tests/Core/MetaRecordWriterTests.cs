// Tests for MetaRecordWriter: write + scan reverse roundtrip

using System.Buffers;
using Atelia.Rbf;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Core;

/// <summary>
/// MetaRecordWriter 写入 + RbfScanner 逆向扫描回读测试。
/// </summary>
public class MetaRecordWriterTests {
    // =========================================================================
    // Basic Roundtrip
    // =========================================================================

    /// <summary>
    /// 最小闭环：写入 2 条 MetaCommitRecord，逆向扫描读回，验证顺序和内容。
    /// </summary>
    [Fact]
    public void AppendCommit_TwoRecords_ScanReverseReadsAllInOrder() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new MetaRecordWriter(framer);

        var record1 = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17,
        };
        var record2 = new MetaCommitRecord {
            EpochSeq = 2,
            RootObjectId = 16,
            VersionIndexPtr = 0x3000,
            DataTail = 0x4000,
            NextObjectId = 18,
        };

        // Act - 写入 2 条 commit 记录
        var addr1 = writer.AppendCommit(record1);
        var addr2 = writer.AppendCommit(record2);

        // Assert - 地址有效
        addr1.IsNull.Should().BeFalse("addr1 should be valid");
        addr2.IsNull.Should().BeFalse("addr2 should be valid");
        addr2.Value.Should().BeGreaterThan(addr1.Value, "addr2 should be after addr1");

        // Act - 逆向扫描读回
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        // Assert - 帧数量
        frames.Should().HaveCount(2);

        // Assert - 逆向顺序：record2, record1
        // 验证 FrameTag
        frames[0].FrameTag.Should().Be(FrameTags.MetaCommit.Value, "frame[0] should be record2");
        frames[1].FrameTag.Should().Be(FrameTags.MetaCommit.Value, "frame[1] should be record1");

        // 验证 payload 内容
        var payload2 = scanner.ReadPayload(frames[0]);
        var payload1 = scanner.ReadPayload(frames[1]);

        // 解析 MetaCommitRecord
        var result2 = MetaCommitRecordSerializer.TryRead(payload2);
        var result1 = MetaCommitRecordSerializer.TryRead(payload1);

        result2.IsSuccess.Should().BeTrue();
        result1.IsSuccess.Should().BeTrue();

        // 验证内容
        result2.Value.Should().Be(record2);
        result1.Value.Should().Be(record1);
    }

    // =========================================================================
    // Single Record
    // =========================================================================

    [Fact]
    public void AppendCommit_SingleRecord_CanBeScannedBack() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new MetaRecordWriter(framer);

        var record = new MetaCommitRecord {
            EpochSeq = 42,
            RootObjectId = 100,
            VersionIndexPtr = 0xDEADBEEF,
            DataTail = 0xCAFEBABE,
            NextObjectId = 200,
        };

        // Act
        var addr = writer.AppendCommit(record);

        // Assert - Address 有效
        addr.IsNull.Should().BeFalse();

        // Scan back
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        frames.Should().HaveCount(1);
        frames[0].FrameTag.Should().Be(FrameTags.MetaCommit.Value);

        var payload = scanner.ReadPayload(frames[0]);
        var result = MetaCommitRecordSerializer.TryRead(payload);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(record);
    }

    // =========================================================================
    // Overload with Individual Parameters
    // =========================================================================

    [Fact]
    public void AppendCommit_IndividualParams_WorksEquivalently() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new MetaRecordWriter(framer);

        // Act
        var addr = writer.AppendCommit(
            epochSeq: 5,
            rootObjectId: 16,
            versionIndexPtr: 0x5000,
            dataTail: 0x6000,
            nextObjectId: 25
        );

        // Assert
        addr.IsNull.Should().BeFalse();

        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        frames.Should().HaveCount(1);

        var payload = scanner.ReadPayload(frames[0]);
        var result = MetaCommitRecordSerializer.TryRead(payload);

        result.IsSuccess.Should().BeTrue();
        result.Value.EpochSeq.Should().Be(5UL);
        result.Value.RootObjectId.Should().Be(16UL);
        result.Value.VersionIndexPtr.Should().Be(0x5000UL);
        result.Value.DataTail.Should().Be(0x6000UL);
        result.Value.NextObjectId.Should().Be(25UL);
    }

    // =========================================================================
    // TryReadAt Verification
    // =========================================================================

    [Fact]
    public void AppendCommit_ReturnedAddress_CanBeReadBack() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new MetaRecordWriter(framer);

        var record = new MetaCommitRecord {
            EpochSeq = 100,
            RootObjectId = 16,
            VersionIndexPtr = 0xABCD1234,
            DataTail = 0xDCBA4321,
            NextObjectId = 50,
        };

        // Act
        var addr = writer.AppendCommit(record);

        // Assert - 使用 TryReadAt 直接读取
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var success = scanner.TryReadAt(addr, out var frame);

        success.Should().BeTrue();
        frame.FrameTag.Should().Be(FrameTags.MetaCommit.Value);

        var payload = scanner.ReadPayload(frame);
        var result = MetaCommitRecordSerializer.TryRead(payload);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(record);
    }

    // =========================================================================
    // Boundary Values
    // =========================================================================

    [Fact]
    public void AppendCommit_MinimalValues_Succeeds() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new MetaRecordWriter(framer);

        var record = new MetaCommitRecord {
            EpochSeq = 0,
            RootObjectId = 0,
            VersionIndexPtr = 0,
            DataTail = 0,
            NextObjectId = 0,
        };

        // Act
        var addr = writer.AppendCommit(record);

        // Assert
        addr.IsNull.Should().BeFalse();

        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        frames.Should().HaveCount(1);

        var payload = scanner.ReadPayload(frames[0]);
        var result = MetaCommitRecordSerializer.TryRead(payload);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(record);
    }

    [Fact]
    public void AppendCommit_MaxValues_Succeeds() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new MetaRecordWriter(framer);

        var record = new MetaCommitRecord {
            EpochSeq = ulong.MaxValue,
            RootObjectId = ulong.MaxValue,
            VersionIndexPtr = ulong.MaxValue,
            DataTail = ulong.MaxValue,
            NextObjectId = ulong.MaxValue,
        };

        // Act
        var addr = writer.AppendCommit(record);

        // Assert
        addr.IsNull.Should().BeFalse();

        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        frames.Should().HaveCount(1);

        var payload = scanner.ReadPayload(frames[0]);
        var result = MetaCommitRecordSerializer.TryRead(payload);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(record);
    }

    // =========================================================================
    // Multiple Records Sequence
    // =========================================================================

    [Fact]
    public void AppendCommit_MultipleRecords_MaintainsSequence() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new MetaRecordWriter(framer);

        var records = new List<MetaCommitRecord>();
        var addresses = new List<<deleted-place-holder>>();

        // 写入 5 条 commit 记录，模拟连续提交
        for (ulong i = 1; i <= 5; i++) {
            var record = new MetaCommitRecord {
                EpochSeq = i,
                RootObjectId = 16,
                VersionIndexPtr = i * 0x1000,
                DataTail = i * 0x2000,
                NextObjectId = 16 + i,
            };
            records.Add(record);
            addresses.Add(writer.AppendCommit(record));
        }

        // Act - 逆向扫描读回
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        // Assert - 帧数量
        frames.Should().HaveCount(5);

        // 验证逆向顺序：record5, record4, record3, record2, record1
        for (int i = 0; i < 5; i++) {
            var expectedRecord = records[4 - i]; // 逆向
            var payload = scanner.ReadPayload(frames[i]);
            var result = MetaCommitRecordSerializer.TryRead(payload);

            result.IsSuccess.Should().BeTrue($"frame[{i}] should parse successfully");
            result.Value.Should().Be(expectedRecord, $"frame[{i}] should be record{5 - i}");
        }

        // 验证地址单调递增
        for (int i = 1; i < addresses.Count; i++) {
            addresses[i].Value.Should().BeGreaterThan(addresses[i - 1].Value,
                $"address[{i}] should be after address[{i - 1}]"
            );
        }
    }

    // =========================================================================
    // Flush
    // =========================================================================

    [Fact]
    public void Flush_DoesNotThrow() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new MetaRecordWriter(framer);

        writer.AppendCommit(
            new MetaCommitRecord {
                EpochSeq = 1,
                RootObjectId = 16,
                VersionIndexPtr = 0x1000,
                DataTail = 0x2000,
                NextObjectId = 17,
            }
        );

        // Act & Assert - 不抛异常
        var action = () => writer.Flush();
        action.Should().NotThrow();
    }

    // =========================================================================
    // Wire Format Verification
    // =========================================================================

    [Fact]
    public void AppendCommit_FrameTag_IsMetaCommit() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new MetaRecordWriter(framer);

        // Act
        writer.AppendCommit(
            new MetaCommitRecord {
                EpochSeq = 1,
                RootObjectId = 16,
                VersionIndexPtr = 0x1000,
                DataTail = 0x2000,
                NextObjectId = 17,
            }
        );

        // Assert - 验证 FrameTag 是 MetaCommit (0x00000002)
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var frames = scanner.ScanReverse().ToList();

        frames[0].FrameTag.Should().Be(0x00000002U, "FrameTag should be MetaCommit");
        FrameTags.IsMetaFrameTag(new Atelia.Rbf.FrameTag(frames[0].FrameTag)).Should().BeTrue();
    }
}
