// Tests for MetaRecordReader: roundtrip with MetaRecordWriter

using System.Buffers;
using Atelia.Rbf;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Core;

/// <summary>
/// MetaRecordReader 测试：与 MetaRecordWriter 配合的 roundtrip 验证。
/// </summary>
public class MetaRecordReaderTests {
    // =========================================================================
    // ScanReverse - Single Record
    // =========================================================================

    /// <summary>
    /// 单条记录 roundtrip：写入后逆向扫描读回。
    /// </summary>
    [Fact]
    public void ScanReverse_SingleRecord_ReturnsCorrectly() {
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

        var expectedAddr = writer.AppendCommit(record);

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new MetaRecordReader(scanner);
        var results = reader.ScanReverse().ToList();

        // Assert
        results.Should().HaveCount(1);

        var parsed = results[0];
        parsed.Address.Should().Be(expectedAddr);
        parsed.Record.Should().Be(record);
        parsed.Status.IsValid.Should().BeTrue();
    }

    // =========================================================================
    // ScanReverse - Multiple Records
    // =========================================================================

    /// <summary>
    /// 多条记录逆序：后写入的记录先被扫描到。
    /// </summary>
    [Fact]
    public void ScanReverse_MultipleRecords_ReturnsInReverseOrder() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new MetaRecordWriter(framer);

        var records = new List<MetaCommitRecord>();
        var addresses = new List<Address64>();

        // 写入 3 条 commit 记录
        for (ulong i = 1; i <= 3; i++) {
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

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new MetaRecordReader(scanner);
        var results = reader.ScanReverse().ToList();

        // Assert - 逆序：record3, record2, record1
        results.Should().HaveCount(3);

        results[0].Record.Should().Be(records[2], "first result should be record3");
        results[0].Address.Should().Be(addresses[2]);

        results[1].Record.Should().Be(records[1], "second result should be record2");
        results[1].Address.Should().Be(addresses[1]);

        results[2].Record.Should().Be(records[0], "third result should be record1");
        results[2].Address.Should().Be(addresses[0]);
    }

    // =========================================================================
    // TryReadAt - Valid Address
    // =========================================================================

    /// <summary>
    /// 随机读取成功：返回正确的记录。
    /// </summary>
    [Fact]
    public void TryReadAt_ValidAddress_ReturnsRecord() {
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

        var addr1 = writer.AppendCommit(record1);
        var addr2 = writer.AppendCommit(record2);

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new MetaRecordReader(scanner);

        var result1 = reader.TryReadAt(addr1);
        var result2 = reader.TryReadAt(addr2);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result1.Value.Record.Should().Be(record1);
        result1.Value.Address.Should().Be(addr1);

        result2.IsSuccess.Should().BeTrue();
        result2.Value.Record.Should().Be(record2);
        result2.Value.Address.Should().Be(addr2);
    }

    // =========================================================================
    // TryReadAt - Invalid Address
    // =========================================================================

    /// <summary>
    /// 随机读取失败：无效地址返回错误。
    /// </summary>
    [Fact]
    public void TryReadAt_InvalidAddress_ReturnsFailure() {
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

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new MetaRecordReader(scanner);

        // 尝试读取一个无效地址
        var invalidAddr = Address64.FromOffset(0x9999);
        var result = reader.TryReadAt(invalidAddr);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<MetaRecordReadError>();
    }

    // =========================================================================
    // ScanReverse - Skips Unknown FrameTag
    // =========================================================================

    /// <summary>
    /// 过滤未知 FrameTag：混入 Data 记录后，仅返回 Meta 记录。
    /// </summary>
    [Fact]
    public void ScanReverse_SkipsUnknownFrameTag() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);

        // 写入 1 条 Meta 记录
        var metaWriter = new MetaRecordWriter(framer);
        var metaRecord = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17,
        };
        var metaAddr = metaWriter.AppendCommit(metaRecord);

        // 混入 1 条 Data 记录（使用 DictVersion FrameTag）
        var dataWriter = new DataRecordWriter(framer);
        dataWriter.AppendDictVersion(0, new byte[] { 0x01, 0x02, 0x03 }.AsSpan());

        // 写入另 1 条 Meta 记录
        var metaRecord2 = new MetaCommitRecord {
            EpochSeq = 2,
            RootObjectId = 16,
            VersionIndexPtr = 0x3000,
            DataTail = 0x4000,
            NextObjectId = 18,
        };
        var metaAddr2 = metaWriter.AppendCommit(metaRecord2);

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new MetaRecordReader(scanner);
        var results = reader.ScanReverse().ToList();

        // Assert - 只返回 2 条 Meta 记录，Data 记录被过滤
        results.Should().HaveCount(2);

        results[0].Record.Should().Be(metaRecord2);
        results[0].Address.Should().Be(metaAddr2);

        results[1].Record.Should().Be(metaRecord);
        results[1].Address.Should().Be(metaAddr);
    }

    // =========================================================================
    // TryReadAt - FrameTag Mismatch
    // =========================================================================

    /// <summary>
    /// 随机读取时 FrameTag 不匹配：返回相应错误。
    /// </summary>
    [Fact]
    public void TryReadAt_FrameTagMismatch_ReturnsFailure() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);

        // 写入 1 条 Data 记录
        var dataWriter = new DataRecordWriter(framer);
        var dataAddr = dataWriter.AppendDictVersion(0, new byte[] { 0x01, 0x02, 0x03 }.AsSpan());

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new MetaRecordReader(scanner);

        // 尝试用 MetaRecordReader 读取 Data 记录
        var result = reader.TryReadAt(dataAddr);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<MetaRecordFrameTagMismatchError>();
    }
}
