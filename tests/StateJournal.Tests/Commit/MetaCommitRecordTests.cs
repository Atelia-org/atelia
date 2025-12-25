using System.Buffers;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Commit;

/// <summary>
/// MetaCommitRecord 序列化/反序列化测试。
/// </summary>
/// <remarks>
/// 对应条款：<c>[F-META-COMMIT-RECORD]</c>
/// </remarks>
public class MetaCommitRecordTests {
    #region 往返测试 (Round-trip Tests)

    /// <summary>
    /// 基本往返测试。
    /// </summary>
    [Fact]
    public void RoundTrip_BasicRecord() {
        // Arrange
        var record = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17,
        };

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        MetaCommitRecordSerializer.Write(buffer, record);
        var result = MetaCommitRecordSerializer.TryRead(buffer.WrittenSpan);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(record);
    }

    /// <summary>
    /// 边界值测试：最大值。
    /// </summary>
    [Fact]
    public void RoundTrip_MaxValues() {
        // Arrange
        var record = new MetaCommitRecord {
            EpochSeq = ulong.MaxValue,
            RootObjectId = ulong.MaxValue,
            VersionIndexPtr = ulong.MaxValue,
            DataTail = ulong.MaxValue,
            NextObjectId = ulong.MaxValue,
        };

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        MetaCommitRecordSerializer.Write(buffer, record);
        var result = MetaCommitRecordSerializer.TryRead(buffer.WrittenSpan);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(record);
    }

    /// <summary>
    /// 边界值测试：零值。
    /// </summary>
    [Fact]
    public void RoundTrip_ZeroValues() {
        // Arrange
        var record = new MetaCommitRecord {
            EpochSeq = 0,
            RootObjectId = 0,
            VersionIndexPtr = 0,
            DataTail = 0,
            NextObjectId = 16,  // 最小合法值（保留区之后）
        };

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        MetaCommitRecordSerializer.Write(buffer, record);
        var result = MetaCommitRecordSerializer.TryRead(buffer.WrittenSpan);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(record);
    }

    /// <summary>
    /// 往返测试：各种代表性值。
    /// </summary>
    [Theory]
    [InlineData(0UL, 16UL, 0x1000UL, 0x2000UL, 17UL)]
    [InlineData(1UL, 16UL, 0UL, 0UL, 16UL)]
    [InlineData(100UL, 100UL, 0x10000UL, 0x20000UL, 200UL)]
    [InlineData(127UL, 127UL, 127UL, 127UL, 127UL)]  // 1 byte varuint 边界
    [InlineData(128UL, 128UL, 128UL, 128UL, 128UL)]  // 2 byte varuint 开始
    [InlineData(16383UL, 16383UL, 16383UL, 16383UL, 16383UL)]  // 2 byte varuint 边界
    [InlineData(16384UL, 16384UL, 16384UL, 16384UL, 16384UL)]  // 3 byte varuint 开始
    public void RoundTrip_VariousValues(ulong epochSeq, ulong rootObjId, ulong versionIndexPtr, ulong dataTail, ulong nextObjId) {
        // Arrange
        var record = new MetaCommitRecord {
            EpochSeq = epochSeq,
            RootObjectId = rootObjId,
            VersionIndexPtr = versionIndexPtr,
            DataTail = dataTail,
            NextObjectId = nextObjId,
        };

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        MetaCommitRecordSerializer.Write(buffer, record);
        var result = MetaCommitRecordSerializer.TryRead(buffer.WrittenSpan);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(record);
    }

    #endregion

    #region 截断错误测试 (Truncation Error Tests)

    /// <summary>
    /// 截断 payload 返回错误。
    /// </summary>
    [Fact]
    public void TryRead_TruncatedPayload_ReturnsError() {
        // Arrange
        var record = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17
        };
        var buffer = new ArrayBufferWriter<byte>();
        MetaCommitRecordSerializer.Write(buffer, record);

        // 截断到一半（只保留 10 字节）
        var truncated = buffer.WrittenSpan[..10];

        // Act
        var result = MetaCommitRecordSerializer.TryRead(truncated);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<MetaCommitRecordTruncatedError>();
    }

    /// <summary>
    /// 空 payload 返回错误。
    /// </summary>
    [Fact]
    public void TryRead_EmptyPayload_ReturnsError() {
        // Act
        var result = MetaCommitRecordSerializer.TryRead(ReadOnlySpan<byte>.Empty);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<MetaCommitRecordTruncatedError>();
        var error = (MetaCommitRecordTruncatedError)result.Error!;
        error.FieldName.Should().Be("EpochSeq");
    }

    /// <summary>
    /// 截断在 EpochSeq 后返回 RootObjectId 错误。
    /// </summary>
    [Fact]
    public void TryRead_TruncatedAfterEpochSeq_ReturnsRootObjectIdError() {
        // Arrange：只写入 EpochSeq (值 1 = 1 字节)
        var buffer = new byte[] { 0x01 };

        // Act
        var result = MetaCommitRecordSerializer.TryRead(buffer);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<MetaCommitRecordTruncatedError>();
        var error = (MetaCommitRecordTruncatedError)result.Error!;
        error.FieldName.Should().Be("RootObjectId");
    }

    /// <summary>
    /// 截断在 VersionIndexPtr 返回错误。
    /// </summary>
    [Fact]
    public void TryRead_TruncatedAtVersionIndexPtr_ReturnsError() {
        // Arrange：写入 EpochSeq(1 byte) + RootObjectId(1 byte) + 部分 VersionIndexPtr(4 bytes)
        var buffer = new byte[] { 0x01, 0x10, 0x00, 0x10, 0x00, 0x00 };

        // Act
        var result = MetaCommitRecordSerializer.TryRead(buffer);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<MetaCommitRecordTruncatedError>();
        var error = (MetaCommitRecordTruncatedError)result.Error!;
        error.FieldName.Should().Be("VersionIndexPtr");
    }

    /// <summary>
    /// 截断在 DataTail 返回错误。
    /// </summary>
    [Fact]
    public void TryRead_TruncatedAtDataTail_ReturnsError() {
        // Arrange：写入 EpochSeq + RootObjectId + VersionIndexPtr + 部分 DataTail
        // EpochSeq=1 (1 byte), RootObjectId=16 (1 byte), VersionIndexPtr=0x1000 (8 bytes)
        // 总共 10 字节，然后加 4 字节的 DataTail（不够 8 字节）
        var fullBuffer = new ArrayBufferWriter<byte>();
        var record = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17
        };
        MetaCommitRecordSerializer.Write(fullBuffer, record);

        // 截断：保留 EpochSeq + RootObjectId + VersionIndexPtr + 4 bytes
        // EpochSeq=1 byte, RootObjectId=1 byte, VersionIndexPtr=8 bytes = 10 bytes
        // 再加 4 bytes = 14 bytes
        var truncated = fullBuffer.WrittenSpan[..14];

        // Act
        var result = MetaCommitRecordSerializer.TryRead(truncated);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<MetaCommitRecordTruncatedError>();
        var error = (MetaCommitRecordTruncatedError)result.Error!;
        error.FieldName.Should().Be("DataTail");
    }

    /// <summary>
    /// 截断在 NextObjectId 返回错误。
    /// </summary>
    [Fact]
    public void TryRead_TruncatedAtNextObjectId_ReturnsError() {
        // Arrange：完整写入但删除最后的 NextObjectId
        var fullBuffer = new ArrayBufferWriter<byte>();
        var record = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17
        };
        MetaCommitRecordSerializer.Write(fullBuffer, record);

        // 截断：保留 EpochSeq + RootObjectId + VersionIndexPtr + DataTail
        // EpochSeq=1 byte, RootObjectId=1 byte, VersionIndexPtr=8 bytes, DataTail=8 bytes = 18 bytes
        var truncated = fullBuffer.WrittenSpan[..18];

        // Act
        var result = MetaCommitRecordSerializer.TryRead(truncated);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<MetaCommitRecordTruncatedError>();
        var error = (MetaCommitRecordTruncatedError)result.Error!;
        error.FieldName.Should().Be("NextObjectId");
    }

    #endregion

    #region GetSerializedSize 测试

    /// <summary>
    /// GetSerializedSize 与实际大小一致。
    /// </summary>
    [Fact]
    public void GetSerializedSize_MatchesActualSize() {
        // Arrange
        var record = new MetaCommitRecord {
            EpochSeq = 12345,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 100,
        };

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        MetaCommitRecordSerializer.Write(buffer, record);
        var expectedSize = MetaCommitRecordSerializer.GetSerializedSize(record);

        // Assert
        buffer.WrittenCount.Should().Be(expectedSize);
    }

    /// <summary>
    /// GetSerializedSize：最小值测试。
    /// </summary>
    [Fact]
    public void GetSerializedSize_MinimalValues_MatchesActualSize() {
        // Arrange
        var record = new MetaCommitRecord {
            EpochSeq = 0,
            RootObjectId = 0,
            VersionIndexPtr = 0,
            DataTail = 0,
            NextObjectId = 0,
        };

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        MetaCommitRecordSerializer.Write(buffer, record);
        var expectedSize = MetaCommitRecordSerializer.GetSerializedSize(record);

        // Assert
        buffer.WrittenCount.Should().Be(expectedSize);
        // 最小大小：3 个 varuint(1 byte each) + 2 个 u64(8 bytes each) = 3 + 16 = 19 bytes
        buffer.WrittenCount.Should().Be(19);
    }

    /// <summary>
    /// GetSerializedSize：最大值测试。
    /// </summary>
    [Fact]
    public void GetSerializedSize_MaxValues_MatchesActualSize() {
        // Arrange
        var record = new MetaCommitRecord {
            EpochSeq = ulong.MaxValue,
            RootObjectId = ulong.MaxValue,
            VersionIndexPtr = ulong.MaxValue,
            DataTail = ulong.MaxValue,
            NextObjectId = ulong.MaxValue,
        };

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        MetaCommitRecordSerializer.Write(buffer, record);
        var expectedSize = MetaCommitRecordSerializer.GetSerializedSize(record);

        // Assert
        buffer.WrittenCount.Should().Be(expectedSize);
        // 最大大小：3 个 varuint(10 bytes each) + 2 个 u64(8 bytes each) = 30 + 16 = 46 bytes
        buffer.WrittenCount.Should().Be(46);
    }

    /// <summary>
    /// GetSerializedSize：各种边界值测试。
    /// </summary>
    [Theory]
    [InlineData(0UL, 0UL, 0UL, 0UL, 0UL)]
    [InlineData(127UL, 127UL, 127UL, 127UL, 127UL)]
    [InlineData(128UL, 128UL, 128UL, 128UL, 128UL)]
    [InlineData(16383UL, 16383UL, 16383UL, 16383UL, 16383UL)]
    [InlineData(16384UL, 16384UL, 16384UL, 16384UL, 16384UL)]
    [InlineData(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)]
    public void GetSerializedSize_VariousValues_MatchesActualSize(
        ulong epochSeq, ulong rootObjId, ulong versionIndexPtr, ulong dataTail, ulong nextObjId
    ) {
        // Arrange
        var record = new MetaCommitRecord {
            EpochSeq = epochSeq,
            RootObjectId = rootObjId,
            VersionIndexPtr = versionIndexPtr,
            DataTail = dataTail,
            NextObjectId = nextObjId,
        };

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        MetaCommitRecordSerializer.Write(buffer, record);
        var expectedSize = MetaCommitRecordSerializer.GetSerializedSize(record);

        // Assert
        buffer.WrittenCount.Should().Be(expectedSize);
    }

    #endregion

    #region IEquatable 测试

    /// <summary>
    /// 相等测试：相同值。
    /// </summary>
    [Fact]
    public void Equals_SameValues_ReturnsTrue() {
        var a = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17,
        };
        var b = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17,
        };

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    /// <summary>
    /// 相等测试：不同值。
    /// </summary>
    [Fact]
    public void Equals_DifferentValues_ReturnsFalse() {
        var a = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17,
        };
        var b = new MetaCommitRecord {
            EpochSeq = 2,  // Different
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17,
        };

        a.Equals(b).Should().BeFalse();
        (a == b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    /// <summary>
    /// 相等测试：与 object 比较。
    /// </summary>
    [Fact]
    public void Equals_ObjectComparison() {
        var a = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17,
        };
        object b = new MetaCommitRecord {
            EpochSeq = 1,
            RootObjectId = 16,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
            NextObjectId = 17,
        };
        object c = "not a MetaCommitRecord";

        a.Equals(b).Should().BeTrue();
        a.Equals(c).Should().BeFalse();
        a.Equals(null).Should().BeFalse();
    }

    /// <summary>
    /// 默认值测试。
    /// </summary>
    [Fact]
    public void Default_AllZero() {
        var record = default(MetaCommitRecord);

        record.EpochSeq.Should().Be(0);
        record.RootObjectId.Should().Be(0);
        record.VersionIndexPtr.Should().Be(0);
        record.DataTail.Should().Be(0);
        record.NextObjectId.Should().Be(0);
    }

    #endregion

    #region 错误类型测试

    /// <summary>
    /// MetaCommitRecordTruncatedError 包含正确的错误码。
    /// </summary>
    [Fact]
    public void MetaCommitRecordTruncatedError_HasCorrectErrorCode() {
        var error = new MetaCommitRecordTruncatedError("TestField");

        error.ErrorCode.Should().Be("StateJournal.MetaCommitRecord.Truncated");
        error.FieldName.Should().Be("TestField");
        error.Message.Should().Contain("TestField");
    }

    /// <summary>
    /// MetaCommitRecordTruncatedError 带有底层错误。
    /// </summary>
    [Fact]
    public void MetaCommitRecordTruncatedError_WithCause_HasCorrectProperties() {
        var cause = new VarIntDecodeError("Test cause");
        var error = new MetaCommitRecordTruncatedError("TestField", cause);

        error.ErrorCode.Should().Be("StateJournal.MetaCommitRecord.Truncated");
        error.FieldName.Should().Be("TestField");
        error.Cause.Should().Be(cause);
    }

    #endregion

    #region 序列化格式验证

    /// <summary>
    /// 验证序列化格式：VersionIndexPtr 和 DataTail 是小端序。
    /// </summary>
    [Fact]
    public void Write_FixedFields_AreLittleEndian() {
        // Arrange
        var record = new MetaCommitRecord {
            EpochSeq = 1,           // 1 byte varuint: 0x01
            RootObjectId = 16,      // 1 byte varuint: 0x10
            VersionIndexPtr = 0x0807060504030201,
            DataTail = 0x100F0E0D0C0B0A09,
            NextObjectId = 17,      // 1 byte varuint: 0x11
        };

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        MetaCommitRecordSerializer.Write(buffer, record);
        var span = buffer.WrittenSpan;

        // Assert
        // EpochSeq: 1 byte
        span[0].Should().Be(0x01);
        // RootObjectId: 1 byte
        span[1].Should().Be(0x10);
        // VersionIndexPtr: 8 bytes, little endian
        span[2].Should().Be(0x01);
        span[3].Should().Be(0x02);
        span[4].Should().Be(0x03);
        span[5].Should().Be(0x04);
        span[6].Should().Be(0x05);
        span[7].Should().Be(0x06);
        span[8].Should().Be(0x07);
        span[9].Should().Be(0x08);
        // DataTail: 8 bytes, little endian
        span[10].Should().Be(0x09);
        span[11].Should().Be(0x0A);
        span[12].Should().Be(0x0B);
        span[13].Should().Be(0x0C);
        span[14].Should().Be(0x0D);
        span[15].Should().Be(0x0E);
        span[16].Should().Be(0x0F);
        span[17].Should().Be(0x10);
        // NextObjectId: 1 byte
        span[18].Should().Be(0x11);
    }

    #endregion
}
