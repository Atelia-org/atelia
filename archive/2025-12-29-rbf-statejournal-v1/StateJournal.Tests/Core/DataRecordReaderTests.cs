// Tests for DataRecordReader: roundtrip with DataRecordWriter

using System.Buffers;
using Atelia.Rbf;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Core;

/// <summary>
/// DataRecordReader 测试：与 DataRecordWriter 配合的 roundtrip 验证。
/// </summary>
public class DataRecordReaderTests {
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
        var writer = new DataRecordWriter(framer);

        byte[] diffPayload = [0xAB, 0xCD, 0xEF, 0x12, 0x34];
        var expectedAddr = writer.AppendDictVersion(0, diffPayload.AsSpan());

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new DataRecordReader(scanner);
        var results = reader.ScanReverse().ToList();

        // Assert
        results.Should().HaveCount(1);

        var parsed = results[0];
        parsed.Address.Should().Be(expectedAddr);
        parsed.Kind.Should().Be(ObjectKind.Dict);
        parsed.PrevVersionPtr.Should().Be(0UL);
        parsed.DiffPayload.ToArray().Should().BeEquivalentTo(diffPayload);
        parsed.Status.IsValid.Should().BeTrue();
        parsed.IsBaseVersion.Should().BeTrue();
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
        var writer = new DataRecordWriter(framer);

        byte[] diff1 = [0x01, 0x02, 0x03];
        byte[] diff2 = [0x11, 0x12];
        byte[] diff3 = [0x21];

        var addr1 = writer.AppendDictVersion(0, diff1.AsSpan());
        var addr2 = writer.AppendDictVersion(addr1.Value, diff2.AsSpan());
        var addr3 = writer.AppendDictVersion(addr2.Value, diff3.AsSpan());

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new DataRecordReader(scanner);
        var results = reader.ScanReverse().ToList();

        // Assert - 逆序：record3, record2, record1
        results.Should().HaveCount(3);

        // record3
        results[0].Address.Should().Be(addr3);
        results[0].PrevVersionPtr.Should().Be(addr2.Value);
        results[0].DiffPayload.ToArray().Should().BeEquivalentTo(diff3);

        // record2
        results[1].Address.Should().Be(addr2);
        results[1].PrevVersionPtr.Should().Be(addr1.Value);
        results[1].DiffPayload.ToArray().Should().BeEquivalentTo(diff2);

        // record1
        results[2].Address.Should().Be(addr1);
        results[2].PrevVersionPtr.Should().Be(0UL);
        results[2].DiffPayload.ToArray().Should().BeEquivalentTo(diff1);
    }

    // =========================================================================
    // ScanReverse - Version Chain Traversal
    // =========================================================================

    /// <summary>
    /// 版本链回溯：从最新记录通过 PrevVersionPtr 遍历到 Base Version。
    /// </summary>
    [Fact]
    public void ScanReverse_VersionChain_CanTraverse() {
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
        var reader = new DataRecordReader(scanner);

        var chain = new List<(Address64 Addr, byte[] Diff)>();
        var currentPtr = addr5.Value;

        while (currentPtr != 0) {
            var result = reader.TryReadAt(Address64.FromOffset((long)currentPtr));
            result.IsSuccess.Should().BeTrue($"Should be able to read at 0x{currentPtr:X}");

            var parsed = result.Value;
            chain.Add((parsed.Address, parsed.DiffPayload.ToArray()));
            currentPtr = parsed.PrevVersionPtr;
        }

        // Assert - 链长度和内容
        chain.Should().HaveCount(5);

        chain[0].Diff.Should().BeEquivalentTo(new byte[] { 0x05 });
        chain[1].Diff.Should().BeEquivalentTo(new byte[] { 0x04 });
        chain[2].Diff.Should().BeEquivalentTo(new byte[] { 0x03 });
        chain[3].Diff.Should().BeEquivalentTo(new byte[] { 0x02 });
        chain[4].Diff.Should().BeEquivalentTo(new byte[] { 0x01 });

        // 验证指针关系
        chain[0].Addr.Should().Be(addr5);
        chain[1].Addr.Should().Be(addr4);
        chain[2].Addr.Should().Be(addr3);
        chain[3].Addr.Should().Be(addr2);
        chain[4].Addr.Should().Be(addr1);
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
        var writer = new DataRecordWriter(framer);

        byte[] diff1 = [0x01, 0x02, 0x03];
        byte[] diff2 = [0x11, 0x12, 0x13, 0x14];

        var addr1 = writer.AppendDictVersion(0, diff1.AsSpan());
        var addr2 = writer.AppendDictVersion(addr1.Value, diff2.AsSpan());

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new DataRecordReader(scanner);

        var result1 = reader.TryReadAt(addr1);
        var result2 = reader.TryReadAt(addr2);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result1.Value.Address.Should().Be(addr1);
        result1.Value.Kind.Should().Be(ObjectKind.Dict);
        result1.Value.PrevVersionPtr.Should().Be(0UL);
        result1.Value.DiffPayload.ToArray().Should().BeEquivalentTo(diff1);

        result2.IsSuccess.Should().BeTrue();
        result2.Value.Address.Should().Be(addr2);
        result2.Value.Kind.Should().Be(ObjectKind.Dict);
        result2.Value.PrevVersionPtr.Should().Be(addr1.Value);
        result2.Value.DiffPayload.ToArray().Should().BeEquivalentTo(diff2);
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
        var writer = new DataRecordWriter(framer);

        writer.AppendDictVersion(0, new byte[] { 0x01, 0x02, 0x03 }.AsSpan());

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new DataRecordReader(scanner);

        // 尝试读取一个无效地址
        var invalidAddr = Address64.FromOffset(0x9999);
        var result = reader.TryReadAt(invalidAddr);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DataRecordReadError>();
    }

    // =========================================================================
    // ScanReverse - Skips Unknown FrameTag
    // =========================================================================

    /// <summary>
    /// 过滤未知 FrameTag：混入 Meta 记录后，仅返回 Data 记录。
    /// </summary>
    [Fact]
    public void ScanReverse_SkipsUnknownFrameTag() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);

        // 写入 1 条 Data 记录
        var dataWriter = new DataRecordWriter(framer);
        byte[] diff1 = [0x01, 0x02, 0x03];
        var dataAddr1 = dataWriter.AppendDictVersion(0, diff1.AsSpan());

        // 混入 1 条 Meta 记录
        var metaWriter = new MetaRecordWriter(framer);
        metaWriter.AppendCommit(
            new MetaCommitRecord {
                EpochSeq = 1,
                RootObjectId = 16,
                VersionIndexPtr = 0x1000,
                DataTail = 0x2000,
                NextObjectId = 17,
            }
        );

        // 写入另 1 条 Data 记录
        byte[] diff2 = [0x11, 0x12];
        var dataAddr2 = dataWriter.AppendDictVersion(dataAddr1.Value, diff2.AsSpan());

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new DataRecordReader(scanner);
        var results = reader.ScanReverse().ToList();

        // Assert - 只返回 2 条 Data 记录，Meta 记录被过滤
        results.Should().HaveCount(2);

        results[0].Address.Should().Be(dataAddr2);
        results[0].DiffPayload.ToArray().Should().BeEquivalentTo(diff2);

        results[1].Address.Should().Be(dataAddr1);
        results[1].DiffPayload.ToArray().Should().BeEquivalentTo(diff1);
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

        // 写入 1 条 Meta 记录
        var metaWriter = new MetaRecordWriter(framer);
        var metaAddr = metaWriter.AppendCommit(
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
        var reader = new DataRecordReader(scanner);

        // 尝试用 DataRecordReader 读取 Meta 记录
        var result = reader.TryReadAt(metaAddr);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DataRecordFrameTagMismatchError>();
    }

    // =========================================================================
    // ObjectKind Extraction
    // =========================================================================

    /// <summary>
    /// 验证 ObjectKind 从 FrameTag 正确提取。
    /// </summary>
    [Fact]
    public void ScanReverse_ExtractsObjectKindCorrectly() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var framer = new RbfFramer(buffer);
        var writer = new DataRecordWriter(framer);

        // 只有 Dict 类型的记录
        writer.AppendDictVersion(0, new byte[] { 0x01 }.AsSpan());

        // Act
        var scanner = new RbfScanner(buffer.WrittenMemory);
        var reader = new DataRecordReader(scanner);
        var results = reader.ScanReverse().ToList();

        // Assert
        results.Should().HaveCount(1);
        results[0].Kind.Should().Be(ObjectKind.Dict);
    }
}
