using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Core;

/// <summary>
/// VarInt 编解码测试。
/// </summary>
/// <remarks>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[F-VARINT-CANONICAL-ENCODING]</c></item>
///   <item><c>[F-DECODE-ERROR-FAILFAST]</c></item>
/// </list>
/// </remarks>
public class VarIntTests {
    #region GetVarUIntLength 测试

    /// <summary>
    /// GetVarUIntLength 边界值测试
    /// </summary>
    [Theory]
    [InlineData(0UL, 1)]                    // 0 → 1 byte
    [InlineData(1UL, 1)]                    // 1 → 1 byte
    [InlineData(127UL, 1)]                  // 0x7F → 1 byte (7 bit max)
    [InlineData(128UL, 2)]                  // 0x80 → 2 bytes
    [InlineData(255UL, 2)]                  // 0xFF → 2 bytes
    [InlineData(16383UL, 2)]                // 0x3FFF → 2 bytes (14 bit max)
    [InlineData(16384UL, 3)]                // 0x4000 → 3 bytes
    [InlineData(2097151UL, 3)]              // 0x1FFFFF → 3 bytes (21 bit max)
    [InlineData(2097152UL, 4)]              // 0x200000 → 4 bytes
    [InlineData(268435455UL, 4)]            // 0xFFFFFFF → 4 bytes (28 bit max)
    [InlineData(268435456UL, 5)]            // 0x10000000 → 5 bytes
    [InlineData(uint.MaxValue, 5)]          // 0xFFFFFFFF → 5 bytes
    [InlineData(34359738367UL, 5)]          // 0x7FFFFFFFF → 5 bytes (35 bit max)
    [InlineData(34359738368UL, 6)]          // 0x800000000 → 6 bytes
    [InlineData(ulong.MaxValue, 10)]        // 0xFFFFFFFFFFFFFFFF → 10 bytes
    public void GetVarUIntLength_ReturnsCorrectLength(ulong value, int expectedLength) {
        VarInt.GetVarUIntLength(value).Should().Be(expectedLength);
    }

    #endregion

    #region WriteVarUInt / TryReadVarUInt 往返测试

    /// <summary>
    /// WriteVarUInt + TryReadVarUInt 往返测试
    /// </summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(255UL)]
    [InlineData(300UL)]                     // 规范示例：0x12C → 0xAC 0x02
    [InlineData(16383UL)]
    [InlineData(16384UL)]
    [InlineData(uint.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void WriteAndRead_VarUInt_Roundtrip(ulong value) {
        // Arrange
        Span<byte> buffer = stackalloc byte[VarInt.MaxVarUInt64Bytes];

        // Act
        int written = VarInt.WriteVarUInt(buffer, value);
        var result = VarInt.TryReadVarUInt(buffer[..written]);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(value);
        result.Value.BytesConsumed.Should().Be(written);
        written.Should().Be(VarInt.GetVarUIntLength(value));
    }

    /// <summary>
    /// 编码示例验证：300 = 0x12C → [0xAC, 0x02]
    /// </summary>
    [Fact]
    public void WriteVarUInt_300_ProducesExpectedBytes() {
        Span<byte> buffer = stackalloc byte[2];

        int written = VarInt.WriteVarUInt(buffer, 300);

        written.Should().Be(2);
        buffer[0].Should().Be(0xAC);
        buffer[1].Should().Be(0x02);
    }

    /// <summary>
    /// 编码示例验证：127 → [0x7F]
    /// </summary>
    [Fact]
    public void WriteVarUInt_127_ProducesOneByte() {
        Span<byte> buffer = stackalloc byte[1];

        int written = VarInt.WriteVarUInt(buffer, 127);

        written.Should().Be(1);
        buffer[0].Should().Be(0x7F);
    }

    /// <summary>
    /// 编码示例验证：128 → [0x80, 0x01]
    /// </summary>
    [Fact]
    public void WriteVarUInt_128_ProducesTwoBytes() {
        Span<byte> buffer = stackalloc byte[2];

        int written = VarInt.WriteVarUInt(buffer, 128);

        written.Should().Be(2);
        buffer[0].Should().Be(0x80);
        buffer[1].Should().Be(0x01);
    }

    /// <summary>
    /// 编码示例验证：0 → [0x00] (canonical)
    /// </summary>
    [Fact]
    public void WriteVarUInt_Zero_ProducesOneByte() {
        Span<byte> buffer = stackalloc byte[1];

        int written = VarInt.WriteVarUInt(buffer, 0);

        written.Should().Be(1);
        buffer[0].Should().Be(0x00);
    }

    /// <summary>
    /// WriteVarUInt 缓冲区太小时抛出 ArgumentException
    /// </summary>
    [Fact]
    public void WriteVarUInt_BufferTooSmall_ThrowsArgumentException() {
        // Note: 不能在 lambda 中使用 Span<byte>，改用 try-catch
        var buffer = new byte[1];

        try {
            VarInt.WriteVarUInt(buffer, 128); // 需要 2 字节
            Assert.Fail("Expected ArgumentException");
        }
        catch (ArgumentException ex) {
            ex.ParamName.Should().Be("destination");
        }
    }

    #endregion

    #region Non-canonical 检测测试

    /// <summary>
    /// [F-VARINT-CANONICAL-ENCODING] 非 canonical 编码拒绝：0x80 0x00 表示 0
    /// </summary>
    [Fact]
    public void TryReadVarUInt_NonCanonical_ZeroWithTwoBytes_ReturnsFailure() {
        // 0x80 0x00 = 0，但 canonical 编码应为 0x00（1 字节）
        ReadOnlySpan<byte> data = new byte[] { 0x80, 0x00 };

        var result = VarInt.TryReadVarUInt(data);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<VarIntNonCanonicalError>();

        var error = (VarIntNonCanonicalError)result.Error!;
        error.Value.Should().Be(0UL);
        error.ActualBytes.Should().Be(2);
        error.ExpectedBytes.Should().Be(1);
    }

    /// <summary>
    /// [F-VARINT-CANONICAL-ENCODING] 非 canonical 编码拒绝：0x81 0x80 0x00 表示 1
    /// </summary>
    [Fact]
    public void TryReadVarUInt_NonCanonical_OneWithThreeBytes_ReturnsFailure() {
        // 0x81 0x80 0x00 = 1，但 canonical 编码应为 0x01（1 字节）
        ReadOnlySpan<byte> data = new byte[] { 0x81, 0x80, 0x00 };

        var result = VarInt.TryReadVarUInt(data);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<VarIntNonCanonicalError>();

        var error = (VarIntNonCanonicalError)result.Error!;
        error.Value.Should().Be(1UL);
        error.ActualBytes.Should().Be(3);
        error.ExpectedBytes.Should().Be(1);
    }

    /// <summary>
    /// [F-VARINT-CANONICAL-ENCODING] 非 canonical 编码拒绝：127 用 2 字节
    /// </summary>
    [Fact]
    public void TryReadVarUInt_NonCanonical_127WithTwoBytes_ReturnsFailure() {
        // 0xFF 0x00 = 127，但 canonical 编码应为 0x7F（1 字节）
        ReadOnlySpan<byte> data = new byte[] { 0xFF, 0x00 };

        var result = VarInt.TryReadVarUInt(data);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<VarIntNonCanonicalError>();

        var error = (VarIntNonCanonicalError)result.Error!;
        error.Value.Should().Be(127UL);
        error.ActualBytes.Should().Be(2);
        error.ExpectedBytes.Should().Be(1);
    }

    #endregion

    #region EOF 检测测试

    /// <summary>
    /// [F-DECODE-ERROR-FAILFAST] EOF 检测：空缓冲区
    /// </summary>
    [Fact]
    public void TryReadVarUInt_EmptyBuffer_ReturnsFailure() {
        ReadOnlySpan<byte> data = ReadOnlySpan<byte>.Empty;

        var result = VarInt.TryReadVarUInt(data);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<VarIntDecodeError>();
        result.Error!.Message.Should().Contain("EOF");
    }

    /// <summary>
    /// [F-DECODE-ERROR-FAILFAST] EOF 检测：continuation 后无数据
    /// </summary>
    [Fact]
    public void TryReadVarUInt_TruncatedContinuation_ReturnsFailure() {
        // 0x80 有 continuation flag，但后面没有数据
        ReadOnlySpan<byte> data = new byte[] { 0x80 };

        var result = VarInt.TryReadVarUInt(data);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<VarIntDecodeError>();
        result.Error!.Message.Should().Contain("EOF");
    }

    /// <summary>
    /// [F-DECODE-ERROR-FAILFAST] EOF 检测：多字节截断
    /// </summary>
    [Fact]
    public void TryReadVarUInt_MultiByteEof_ReturnsFailure() {
        // 0x80 0x80 表示还需要更多字节
        ReadOnlySpan<byte> data = new byte[] { 0x80, 0x80 };

        var result = VarInt.TryReadVarUInt(data);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<VarIntDecodeError>();
    }

    #endregion

    #region 溢出检测测试

    /// <summary>
    /// [F-DECODE-ERROR-FAILFAST] 溢出检测：11 字节 varuint
    /// </summary>
    [Fact]
    public void TryReadVarUInt_ElevenBytes_ReturnsOverflowError() {
        // 11 个 0x80 后跟 0x00 → 超过 64 位
        ReadOnlySpan<byte> data = new byte[] {
            0x80, 0x80, 0x80, 0x80, 0x80,
            0x80, 0x80, 0x80, 0x80, 0x80,
            0x00
        };

        var result = VarInt.TryReadVarUInt(data);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<VarIntDecodeError>();
        result.Error!.Message.Should().Contain("overflow");
    }

    /// <summary>
    /// [F-DECODE-ERROR-FAILFAST] 溢出检测：第 10 字节值太大
    /// </summary>
    [Fact]
    public void TryReadVarUInt_TenthByteTooLarge_ReturnsOverflowError() {
        // 9 个 0xFF 后跟 0x02 → 第 10 字节超过 0x01
        ReadOnlySpan<byte> data = new byte[] {
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0x02
        };

        var result = VarInt.TryReadVarUInt(data);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<VarIntDecodeError>();
        result.Error!.Message.Should().Contain("overflow");
    }

    /// <summary>
    /// 最大有效值 ulong.MaxValue 可以正确编解码
    /// </summary>
    [Fact]
    public void TryReadVarUInt_MaxValue_Succeeds() {
        // ulong.MaxValue = 0xFFFFFFFFFFFFFFFF
        // 编码为 9 个 0xFF 后跟 0x01
        Span<byte> buffer = stackalloc byte[10];

        int written = VarInt.WriteVarUInt(buffer, ulong.MaxValue);
        var result = VarInt.TryReadVarUInt(buffer[..written]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(ulong.MaxValue);
        written.Should().Be(10);
    }

    #endregion

    #region ZigZag 编解码测试

    /// <summary>
    /// ZigZag 编码测试
    /// </summary>
    [Theory]
    [InlineData(0L, 0UL)]                   // 0 → 0
    [InlineData(-1L, 1UL)]                  // -1 → 1
    [InlineData(1L, 2UL)]                   // 1 → 2
    [InlineData(-2L, 3UL)]                  // -2 → 3
    [InlineData(2L, 4UL)]                   // 2 → 4
    [InlineData(-3L, 5UL)]                  // -3 → 5
    [InlineData(2147483647L, 4294967294UL)] // int.MaxValue → 2^32-2
    [InlineData(-2147483648L, 4294967295UL)] // int.MinValue → 2^32-1
    public void ZigZagEncode_ReturnsExpectedValue(long input, ulong expected) {
        VarInt.ZigZagEncode(input).Should().Be(expected);
    }

    /// <summary>
    /// ZigZag 编码边界值：long.MaxValue
    /// </summary>
    [Fact]
    public void ZigZagEncode_LongMaxValue_ReturnsExpected() {
        // long.MaxValue → ulong.MaxValue - 1
        VarInt.ZigZagEncode(long.MaxValue).Should().Be(ulong.MaxValue - 1);
    }

    /// <summary>
    /// ZigZag 编码边界值：long.MinValue
    /// </summary>
    [Fact]
    public void ZigZagEncode_LongMinValue_ReturnsExpected() {
        // long.MinValue → ulong.MaxValue
        VarInt.ZigZagEncode(long.MinValue).Should().Be(ulong.MaxValue);
    }

    /// <summary>
    /// ZigZag 解码测试
    /// </summary>
    [Theory]
    [InlineData(0UL, 0L)]                   // 0 → 0
    [InlineData(1UL, -1L)]                  // 1 → -1
    [InlineData(2UL, 1L)]                   // 2 → 1
    [InlineData(3UL, -2L)]                  // 3 → -2
    [InlineData(4UL, 2L)]                   // 4 → 2
    [InlineData(5UL, -3L)]                  // 5 → -3
    [InlineData(4294967294UL, 2147483647L)] // 2^32-2 → int.MaxValue
    [InlineData(4294967295UL, -2147483648L)] // 2^32-1 → int.MinValue
    public void ZigZagDecode_ReturnsExpectedValue(ulong input, long expected) {
        VarInt.ZigZagDecode(input).Should().Be(expected);
    }

    /// <summary>
    /// ZigZag 解码边界值：ulong.MaxValue - 1
    /// </summary>
    [Fact]
    public void ZigZagDecode_UlongMaxMinusOne_ReturnsLongMax() {
        // ulong.MaxValue - 1 → long.MaxValue
        VarInt.ZigZagDecode(ulong.MaxValue - 1).Should().Be(long.MaxValue);
    }

    /// <summary>
    /// ZigZag 解码边界值：ulong.MaxValue
    /// </summary>
    [Fact]
    public void ZigZagDecode_UlongMax_ReturnsLongMin() {
        // ulong.MaxValue → long.MinValue
        VarInt.ZigZagDecode(ulong.MaxValue).Should().Be(long.MinValue);
    }

    /// <summary>
    /// ZigZag 往返测试
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(100L)]
    [InlineData(-100L)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void ZigZag_Roundtrip(long value) {
        var encoded = VarInt.ZigZagEncode(value);
        var decoded = VarInt.ZigZagDecode(encoded);

        decoded.Should().Be(value);
    }

    #endregion

    #region Signed VarInt 测试

    /// <summary>
    /// WriteVarInt + TryReadVarInt 往返测试
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(127L)]
    [InlineData(-128L)]
    [InlineData(300L)]
    [InlineData(-300L)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void WriteAndRead_VarInt_Roundtrip(long value) {
        // Arrange
        Span<byte> buffer = stackalloc byte[VarInt.MaxVarUInt64Bytes];

        // Act
        int written = VarInt.WriteVarInt(buffer, value);
        var result = VarInt.TryReadVarInt(buffer[..written]);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(value);
        result.Value.BytesConsumed.Should().Be(written);
    }

    /// <summary>
    /// 有符号 0 编码为 1 字节
    /// </summary>
    [Fact]
    public void WriteVarInt_Zero_ProducesOneByte() {
        Span<byte> buffer = stackalloc byte[1];

        int written = VarInt.WriteVarInt(buffer, 0);

        written.Should().Be(1);
        buffer[0].Should().Be(0x00);
    }

    /// <summary>
    /// 有符号 -1 编码为 1 字节 (ZigZag → 1)
    /// </summary>
    [Fact]
    public void WriteVarInt_MinusOne_ProducesOneByte() {
        Span<byte> buffer = stackalloc byte[1];

        int written = VarInt.WriteVarInt(buffer, -1);

        written.Should().Be(1);
        buffer[0].Should().Be(0x01); // ZigZag(-1) = 1
    }

    /// <summary>
    /// 有符号 1 编码为 1 字节 (ZigZag → 2)
    /// </summary>
    [Fact]
    public void WriteVarInt_One_ProducesOneByte() {
        Span<byte> buffer = stackalloc byte[1];

        int written = VarInt.WriteVarInt(buffer, 1);

        written.Should().Be(1);
        buffer[0].Should().Be(0x02); // ZigZag(1) = 2
    }

    /// <summary>
    /// TryReadVarInt EOF 测试
    /// </summary>
    [Fact]
    public void TryReadVarInt_EmptyBuffer_ReturnsFailure() {
        var result = VarInt.TryReadVarInt(ReadOnlySpan<byte>.Empty);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<VarIntDecodeError>();
    }

    #endregion

    #region 错误类型测试

    /// <summary>
    /// VarIntDecodeError 包含正确的错误信息
    /// </summary>
    [Fact]
    public void VarIntDecodeError_HasCorrectErrorCode() {
        var error = new VarIntDecodeError("Test message");

        error.ErrorCode.Should().Be("StateJournal.VarInt.DecodeError");
        error.Message.Should().Be("Test message");
    }

    /// <summary>
    /// VarIntNonCanonicalError 包含正确的错误信息
    /// </summary>
    [Fact]
    public void VarIntNonCanonicalError_HasCorrectErrorCode() {
        var error = new VarIntNonCanonicalError(0, 2, 1);

        error.ErrorCode.Should().Be("StateJournal.VarInt.NonCanonical");
        error.Value.Should().Be(0UL);
        error.ActualBytes.Should().Be(2);
        error.ExpectedBytes.Should().Be(1);
        error.Message.Should().Contain("Non-canonical");
    }

    #endregion

    #region 部分读取测试（缓冲区有多余数据）

    /// <summary>
    /// 读取时缓冲区有多余数据，应只消费必要的字节
    /// </summary>
    [Fact]
    public void TryReadVarUInt_BufferWithExtraData_ConsumesCorrectBytes() {
        // 300 编码为 [0xAC, 0x02]，后面还有多余数据
        ReadOnlySpan<byte> data = new byte[] { 0xAC, 0x02, 0xFF, 0xFF, 0xFF };

        var result = VarInt.TryReadVarUInt(data);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(300UL);
        result.Value.BytesConsumed.Should().Be(2);
    }

    /// <summary>
    /// 有符号读取时缓冲区有多余数据
    /// </summary>
    [Fact]
    public void TryReadVarInt_BufferWithExtraData_ConsumesCorrectBytes() {
        // -1 编码为 [0x01]，后面还有多余数据
        ReadOnlySpan<byte> data = new byte[] { 0x01, 0xFF, 0xFF };

        var result = VarInt.TryReadVarInt(data);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(-1L);
        result.Value.BytesConsumed.Should().Be(1);
    }

    #endregion
}
