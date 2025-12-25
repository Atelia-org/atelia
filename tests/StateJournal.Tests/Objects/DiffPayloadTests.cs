using System.Buffers;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// DiffPayload 编解码测试。
/// </summary>
/// <remarks>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[F-KVPAIR-HIGHBITS-RESERVED]</c></item>
///   <item><c>[F-UNKNOWN-VALUETYPE-REJECT]</c></item>
///   <item><c>[S-DIFF-KEY-SORTED-UNIQUE]</c></item>
///   <item><c>[S-PAIRCOUNT-ZERO-LEGALITY]</c></item>
/// </list>
/// </remarks>
public class DiffPayloadTests {
    #region 空 payload 测试

    /// <summary>
    /// 空 payload（PairCount=0）往返测试。
    /// </summary>
    [Fact]
    public void EmptyPayload_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.Complete();

        // Assert - 写入结果
        buffer.WrittenCount.Should().Be(1); // PairCount=0 只需 1 字节
        buffer.WrittenSpan[0].Should().Be(0x00);

        // Assert - 读取结果
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(0);
        reader.HasError.Should().BeFalse();

        var result = reader.TryReadNext(out _, out _, out _);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse(); // 没有更多数据
    }

    #endregion

    #region 单对测试

    /// <summary>
    /// 单对 Null 往返测试。
    /// </summary>
    [Fact]
    public void SinglePair_Null_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WriteNull(42);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out var key, out var valueType, out var payload);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        key.Should().Be(42UL);
        valueType.Should().Be(ValueType.Null);
        payload.Length.Should().Be(0);
    }

    /// <summary>
    /// 单对 Tombstone 往返测试。
    /// </summary>
    [Fact]
    public void SinglePair_Tombstone_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WriteTombstone(100);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out var key, out var valueType, out var payload);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        key.Should().Be(100UL);
        valueType.Should().Be(ValueType.Tombstone);
        payload.Length.Should().Be(0);
    }

    /// <summary>
    /// 单对 ObjRef 往返测试。
    /// </summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(300UL)]
    [InlineData(ulong.MaxValue)]
    public void SinglePair_ObjRef_Roundtrip(ulong objectId) {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WriteObjRef(10, objectId);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out var key, out var valueType, out var payload);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        key.Should().Be(10UL);
        valueType.Should().Be(ValueType.ObjRef);

        var objRefResult = DiffPayloadReader.ReadObjRef(payload);
        objRefResult.IsSuccess.Should().BeTrue();
        objRefResult.Value.Should().Be(objectId);
    }

    /// <summary>
    /// 单对 VarInt 往返测试。
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(127L)]
    [InlineData(-128L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void SinglePair_VarInt_Roundtrip(long value) {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WriteVarInt(20, value);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out var key, out var valueType, out var payload);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        key.Should().Be(20UL);
        valueType.Should().Be(ValueType.VarInt);

        var varIntResult = DiffPayloadReader.ReadVarInt(payload);
        varIntResult.IsSuccess.Should().BeTrue();
        varIntResult.Value.Should().Be(value);
    }

    /// <summary>
    /// 单对 Ptr64 往返测试。
    /// </summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(0x1234567890ABCDEFUL)]
    [InlineData(ulong.MaxValue)]
    public void SinglePair_Ptr64_Roundtrip(ulong ptr) {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WritePtr64(30, ptr);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out var key, out var valueType, out var payload);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        key.Should().Be(30UL);
        valueType.Should().Be(ValueType.Ptr64);
        payload.Length.Should().Be(8);

        var ptr64Result = DiffPayloadReader.ReadPtr64(payload);
        ptr64Result.IsSuccess.Should().BeTrue();
        ptr64Result.Value.Should().Be(ptr);
    }

    #endregion

    #region 多对测试（Key Delta 编码验证）

    /// <summary>
    /// 多对往返测试，验证 key delta 编码。
    /// </summary>
    [Fact]
    public void MultiplePairs_KeyDelta_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act - 按升序写入 keys
        writer.WriteNull(10);           // FirstKey = 10
        writer.WriteTombstone(20);      // Delta = 10
        writer.WriteObjRef(30, 100);    // Delta = 10
        writer.WriteVarInt(50, -42);    // Delta = 20
        writer.WritePtr64(100, 0xABCD); // Delta = 50
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(5);

        // Pair 1: key=10, Null
        var r1 = reader.TryReadNext(out var k1, out var t1, out var p1);
        r1.IsSuccess.Should().BeTrue();
        r1.Value.Should().BeTrue();
        k1.Should().Be(10UL);
        t1.Should().Be(ValueType.Null);

        // Pair 2: key=20, Tombstone
        var r2 = reader.TryReadNext(out var k2, out var t2, out var p2);
        r2.IsSuccess.Should().BeTrue();
        r2.Value.Should().BeTrue();
        k2.Should().Be(20UL);
        t2.Should().Be(ValueType.Tombstone);

        // Pair 3: key=30, ObjRef=100
        var r3 = reader.TryReadNext(out var k3, out var t3, out var p3);
        r3.IsSuccess.Should().BeTrue();
        r3.Value.Should().BeTrue();
        k3.Should().Be(30UL);
        t3.Should().Be(ValueType.ObjRef);
        DiffPayloadReader.ReadObjRef(p3).Value.Should().Be(100UL);

        // Pair 4: key=50, VarInt=-42
        var r4 = reader.TryReadNext(out var k4, out var t4, out var p4);
        r4.IsSuccess.Should().BeTrue();
        r4.Value.Should().BeTrue();
        k4.Should().Be(50UL);
        t4.Should().Be(ValueType.VarInt);
        DiffPayloadReader.ReadVarInt(p4).Value.Should().Be(-42L);

        // Pair 5: key=100, Ptr64=0xABCD
        var r5 = reader.TryReadNext(out var k5, out var t5, out var p5);
        r5.IsSuccess.Should().BeTrue();
        r5.Value.Should().BeTrue();
        k5.Should().Be(100UL);
        t5.Should().Be(ValueType.Ptr64);
        DiffPayloadReader.ReadPtr64(p5).Value.Should().Be(0xABCDUL);

        // No more pairs
        var r6 = reader.TryReadNext(out _, out _, out _);
        r6.IsSuccess.Should().BeTrue();
        r6.Value.Should().BeFalse();
    }

    /// <summary>
    /// 连续 key（delta=1）往返测试。
    /// </summary>
    [Fact]
    public void MultiplePairs_ConsecutiveKeys_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WriteNull(0);
        writer.WriteNull(1);
        writer.WriteNull(2);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(3);

        var r1 = reader.TryReadNext(out var k1, out _, out _);
        r1.Value.Should().BeTrue();
        k1.Should().Be(0UL);

        var r2 = reader.TryReadNext(out var k2, out _, out _);
        r2.Value.Should().BeTrue();
        k2.Should().Be(1UL);

        var r3 = reader.TryReadNext(out var k3, out _, out _);
        r3.Value.Should().BeTrue();
        k3.Should().Be(2UL);
    }

    #endregion

    #region 升序验证测试

    /// <summary>
    /// [S-DIFF-KEY-SORTED-UNIQUE] 乱序写入时抛异常。
    /// </summary>
    [Fact]
    public void Writer_NonAscendingKey_ThrowsArgumentException() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act & Assert
        writer.WriteNull(10);
        try {
            writer.WriteNull(5); // 5 < 10, 违反升序
            Assert.Fail("Expected ArgumentException");
        }
        catch (ArgumentException ex) {
            ex.Message.Should().Contain("strictly ascending");
        }
    }

    /// <summary>
    /// [S-DIFF-KEY-SORTED-UNIQUE] 重复 key 抛异常。
    /// </summary>
    [Fact]
    public void Writer_DuplicateKey_ThrowsArgumentException() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act & Assert
        writer.WriteNull(10);
        try {
            writer.WriteNull(10); // 重复 key
            Assert.Fail("Expected ArgumentException");
        }
        catch (ArgumentException ex) {
            ex.Message.Should().Contain("strictly ascending");
        }
    }

    /// <summary>
    /// [S-DIFF-KEY-SORTED-UNIQUE] Reader 检测到重复 key（delta=0）返回错误。
    /// </summary>
    [Fact]
    public void Reader_DuplicateKey_ReturnsError() {
        // 手工构造：PairCount=2, FirstKey=10, Type=Null, Type=Null, KeyDelta=0
        // 这会导致第二个 key 也是 10（重复）
        var payload = new byte[]
        {
            0x02,       // PairCount = 2
            0x0A,       // FirstKey = 10
            0x00,       // KeyValuePairType = Null (0x0)
            0x00,       // KeyValuePairType = Null (0x0)
            0x00,       // KeyDeltaFromPrev = 0 (重复 key!)
        };

        var reader = new DiffPayloadReader(payload);
        reader.PairCount.Should().Be(2);

        // 第一对正常读取
        var r1 = reader.TryReadNext(out var k1, out _, out _);
        r1.IsSuccess.Should().BeTrue();
        r1.Value.Should().BeTrue();
        k1.Should().Be(10UL);

        // 第二对应该失败（delta=0 导致重复 key）
        var r2 = reader.TryReadNext(out _, out _, out _);
        r2.IsFailure.Should().BeTrue();
        r2.Error.Should().BeOfType<DiffKeySortingError>();
    }

    #endregion

    #region 高 4 bit 非零测试

    /// <summary>
    /// [F-KVPAIR-HIGHBITS-RESERVED] Reader 拒绝高 4 bit 非零的 KeyValuePairType。
    /// </summary>
    [Fact]
    public void Reader_HighBitsNonZero_ReturnsError() {
        // 手工构造：PairCount=1, FirstKey=0, KeyValuePairType=0x10 (高 bit 非零)
        var payload = new byte[]
        {
            0x01,       // PairCount = 1
            0x00,       // FirstKey = 0
            0x10,       // KeyValuePairType = 0x10 (高 4 bit = 1, 低 4 bit = 0)
        };

        var reader = new DiffPayloadReader(payload);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out _, out _, out _);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DiffPayloadFormatError>();
        result.Error!.Message.Should().Contain("high 4 bits");
    }

    /// <summary>
    /// [F-UNKNOWN-VALUETYPE-REJECT] Reader 拒绝未知 ValueType（低 4 bit &gt; 4）。
    /// </summary>
    [Theory]
    [InlineData(0x05)]
    [InlineData(0x0A)]
    [InlineData(0x0F)]
    public void Reader_UnknownValueType_ReturnsError(byte kvpType) {
        // 手工构造：PairCount=1, FirstKey=0, KeyValuePairType=未知
        var payload = new byte[]
        {
            0x01,       // PairCount = 1
            0x00,       // FirstKey = 0
            kvpType,    // KeyValuePairType = 未知
        };

        var reader = new DiffPayloadReader(payload);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out _, out _, out _);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<UnknownValueTypeError>();
    }

    #endregion

    #region 边界值测试

    /// <summary>
    /// 边界值：ulong.MaxValue 作为 key。
    /// </summary>
    [Fact]
    public void BoundaryValue_MaxKey_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WriteNull(ulong.MaxValue);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        var result = reader.TryReadNext(out var key, out _, out _);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        key.Should().Be(ulong.MaxValue);
    }

    /// <summary>
    /// 边界值：long.MinValue 作为 VarInt 值。
    /// </summary>
    [Fact]
    public void BoundaryValue_MinVarInt_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WriteVarInt(0, long.MinValue);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        var result = reader.TryReadNext(out _, out var valueType, out var payload);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        valueType.Should().Be(ValueType.VarInt);

        var varIntResult = DiffPayloadReader.ReadVarInt(payload);
        varIntResult.IsSuccess.Should().BeTrue();
        varIntResult.Value.Should().Be(long.MinValue);
    }

    /// <summary>
    /// 边界值：long.MaxValue 作为 VarInt 值。
    /// </summary>
    [Fact]
    public void BoundaryValue_MaxVarInt_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WriteVarInt(0, long.MaxValue);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        var result = reader.TryReadNext(out _, out var valueType, out var payload);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        valueType.Should().Be(ValueType.VarInt);

        var varIntResult = DiffPayloadReader.ReadVarInt(payload);
        varIntResult.IsSuccess.Should().BeTrue();
        varIntResult.Value.Should().Be(long.MaxValue);
    }

    /// <summary>
    /// 边界值：ulong.MaxValue 作为 ObjRef。
    /// </summary>
    [Fact]
    public void BoundaryValue_MaxObjRef_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WriteObjRef(0, ulong.MaxValue);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        var result = reader.TryReadNext(out _, out var valueType, out var payload);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        valueType.Should().Be(ValueType.ObjRef);

        var objRefResult = DiffPayloadReader.ReadObjRef(payload);
        objRefResult.IsSuccess.Should().BeTrue();
        objRefResult.Value.Should().Be(ulong.MaxValue);
    }

    /// <summary>
    /// 边界值：ulong.MaxValue 作为 Ptr64。
    /// </summary>
    [Fact]
    public void BoundaryValue_MaxPtr64_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act
        writer.WritePtr64(0, ulong.MaxValue);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        var result = reader.TryReadNext(out _, out var valueType, out var payload);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        valueType.Should().Be(ValueType.Ptr64);

        var ptr64Result = DiffPayloadReader.ReadPtr64(payload);
        ptr64Result.IsSuccess.Should().BeTrue();
        ptr64Result.Value.Should().Be(ulong.MaxValue);
    }

    /// <summary>
    /// 大 key delta 测试。
    /// </summary>
    [Fact]
    public void BoundaryValue_LargeKeyDelta_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act - 两个相隔很远的 key
        writer.WriteNull(0);
        writer.WriteNull(ulong.MaxValue - 1);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(2);

        var r1 = reader.TryReadNext(out var k1, out _, out _);
        r1.Value.Should().BeTrue();
        k1.Should().Be(0UL);

        var r2 = reader.TryReadNext(out var k2, out _, out _);
        r2.Value.Should().BeTrue();
        k2.Should().Be(ulong.MaxValue - 1);
    }

    #endregion

    #region EOF 错误测试

    /// <summary>
    /// 空数据返回错误。
    /// </summary>
    [Fact]
    public void Reader_EmptyData_ReturnsError() {
        var reader = new DiffPayloadReader(ReadOnlySpan<byte>.Empty);
        reader.HasError.Should().BeTrue();
        reader.Error.Should().BeOfType<DiffPayloadEofError>();
    }

    /// <summary>
    /// 截断的 payload（缺少 FirstKey）返回错误。
    /// </summary>
    [Fact]
    public void Reader_TruncatedFirstKey_ReturnsError() {
        // PairCount=1 但没有 FirstKey
        var payload = new byte[] { 0x01 };

        var reader = new DiffPayloadReader(payload);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out _, out _, out _);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DiffPayloadEofError>();
    }

    /// <summary>
    /// 截断的 Ptr64 payload 返回错误。
    /// </summary>
    [Fact]
    public void Reader_TruncatedPtr64_ReturnsError() {
        // PairCount=1, FirstKey=0, Type=Ptr64 (0x04)，但只有 4 字节（需要 8）
        var payload = new byte[]
        {
            0x01,       // PairCount = 1
            0x00,       // FirstKey = 0
            0x04,       // KeyValuePairType = Ptr64
            0x01, 0x02, 0x03, 0x04  // 只有 4 字节，需要 8
        };

        var reader = new DiffPayloadReader(payload);
        var result = reader.TryReadNext(out _, out _, out _);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DiffPayloadEofError>();
    }

    #endregion

    #region Writer 状态测试

    /// <summary>
    /// PairCount 属性正确追踪。
    /// </summary>
    [Fact]
    public void Writer_PairCount_IsTracked() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        writer.PairCount.Should().Be(0);

        writer.WriteNull(0);
        writer.PairCount.Should().Be(1);

        writer.WriteNull(1);
        writer.PairCount.Should().Be(2);

        writer.WriteNull(2);
        writer.PairCount.Should().Be(3);
    }

    /// <summary>
    /// Complete 后不能再次调用。
    /// </summary>
    [Fact]
    public void Writer_CompleteCalledTwice_ThrowsException() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);
        writer.Complete();

        try {
            writer.Complete();
            Assert.Fail("Expected InvalidOperationException");
        }
        catch (InvalidOperationException) {
            // Expected
        }
    }

    /// <summary>
    /// Writer 构造函数对 null 参数抛异常。
    /// </summary>
    [Fact]
    public void Writer_NullBufferWriter_ThrowsArgumentNullException() {
        Action act = () => new DiffPayloadWriter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Reader 便利方法测试

    /// <summary>
    /// ReadPtr64 对短 payload 返回错误。
    /// </summary>
    [Fact]
    public void ReadPtr64_ShortPayload_ReturnsError() {
        var result = DiffPayloadReader.ReadPtr64(new byte[] { 0x01, 0x02 });
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DiffPayloadEofError>();
    }

    /// <summary>
    /// PairsRead 属性正确追踪。
    /// </summary>
    [Fact]
    public void Reader_PairsRead_IsTracked() {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);
        writer.WriteNull(0);
        writer.WriteNull(1);
        writer.WriteNull(2);
        writer.Complete();

        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairsRead.Should().Be(0);

        reader.TryReadNext(out _, out _, out _);
        reader.PairsRead.Should().Be(1);

        reader.TryReadNext(out _, out _, out _);
        reader.PairsRead.Should().Be(2);

        reader.TryReadNext(out _, out _, out _);
        reader.PairsRead.Should().Be(3);
    }

    #endregion

    #region 混合类型测试

    /// <summary>
    /// 混合所有 ValueType 的往返测试。
    /// </summary>
    [Fact]
    public void MixedValueTypes_Roundtrip() {
        // Arrange
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new DiffPayloadWriter(buffer);

        // Act - 写入所有类型
        writer.WriteNull(1);
        writer.WriteTombstone(2);
        writer.WriteObjRef(3, 12345);
        writer.WriteVarInt(4, -99999);
        writer.WritePtr64(5, 0xDEADBEEFCAFEBABE);
        writer.Complete();

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(5);

        // Null
        reader.TryReadNext(out var k1, out var t1, out _);
        k1.Should().Be(1UL);
        t1.Should().Be(ValueType.Null);

        // Tombstone
        reader.TryReadNext(out var k2, out var t2, out _);
        k2.Should().Be(2UL);
        t2.Should().Be(ValueType.Tombstone);

        // ObjRef
        reader.TryReadNext(out var k3, out var t3, out var p3);
        k3.Should().Be(3UL);
        t3.Should().Be(ValueType.ObjRef);
        DiffPayloadReader.ReadObjRef(p3).Value.Should().Be(12345UL);

        // VarInt
        reader.TryReadNext(out var k4, out var t4, out var p4);
        k4.Should().Be(4UL);
        t4.Should().Be(ValueType.VarInt);
        DiffPayloadReader.ReadVarInt(p4).Value.Should().Be(-99999L);

        // Ptr64
        reader.TryReadNext(out var k5, out var t5, out var p5);
        k5.Should().Be(5UL);
        t5.Should().Be(ValueType.Ptr64);
        DiffPayloadReader.ReadPtr64(p5).Value.Should().Be(0xDEADBEEFCAFEBABE);
    }

    #endregion
}
