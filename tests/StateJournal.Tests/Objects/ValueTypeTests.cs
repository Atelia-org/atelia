using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// ValueType 枚举和扩展方法测试。
/// </summary>
/// <remarks>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[F-KVPAIR-HIGHBITS-RESERVED]</c></item>
///   <item><c>[F-UNKNOWN-VALUETYPE-REJECT]</c></item>
/// </list>
/// </remarks>
public class ValueTypeTests {
    #region 枚举值测试

    /// <summary>
    /// 验证 ValueType 枚举值正确。
    /// </summary>
    [Theory]
    [InlineData(ValueType.Null, 0x0)]
    [InlineData(ValueType.Tombstone, 0x1)]
    [InlineData(ValueType.ObjRef, 0x2)]
    [InlineData(ValueType.VarInt, 0x3)]
    [InlineData(ValueType.Ptr64, 0x4)]
    public void ValueType_HasCorrectValue(ValueType valueType, byte expectedValue) {
        ((byte)valueType).Should().Be(expectedValue);
    }

    #endregion

    #region IsKnown 测试

    /// <summary>
    /// 已知 ValueType 返回 true。
    /// </summary>
    [Theory]
    [InlineData(ValueType.Null)]
    [InlineData(ValueType.Tombstone)]
    [InlineData(ValueType.ObjRef)]
    [InlineData(ValueType.VarInt)]
    [InlineData(ValueType.Ptr64)]
    public void IsKnown_KnownValueType_ReturnsTrue(ValueType valueType) {
        valueType.IsKnown().Should().BeTrue();
    }

    /// <summary>
    /// 未知 ValueType（0x5~0xF）返回 false。
    /// </summary>
    [Theory]
    [InlineData(0x5)]
    [InlineData(0x6)]
    [InlineData(0x7)]
    [InlineData(0x8)]
    [InlineData(0x9)]
    [InlineData(0xA)]
    [InlineData(0xB)]
    [InlineData(0xC)]
    [InlineData(0xD)]
    [InlineData(0xE)]
    [InlineData(0xF)]
    public void IsKnown_UnknownValueType_ReturnsFalse(byte value) {
        var valueType = (ValueType)value;
        valueType.IsKnown().Should().BeFalse();
    }

    #endregion

    #region HasPayload 测试

    /// <summary>
    /// Null 和 Tombstone 没有 payload。
    /// </summary>
    [Theory]
    [InlineData(ValueType.Null)]
    [InlineData(ValueType.Tombstone)]
    public void HasPayload_NullAndTombstone_ReturnsFalse(ValueType valueType) {
        valueType.HasPayload().Should().BeFalse();
    }

    /// <summary>
    /// ObjRef、VarInt、Ptr64 有 payload。
    /// </summary>
    [Theory]
    [InlineData(ValueType.ObjRef)]
    [InlineData(ValueType.VarInt)]
    [InlineData(ValueType.Ptr64)]
    public void HasPayload_OtherTypes_ReturnsTrue(ValueType valueType) {
        valueType.HasPayload().Should().BeTrue();
    }

    #endregion

    #region ExtractValueType 测试

    /// <summary>
    /// 从 KeyValuePairType 提取低 4 bit。
    /// </summary>
    [Theory]
    [InlineData(0x00, ValueType.Null)]
    [InlineData(0x01, ValueType.Tombstone)]
    [InlineData(0x02, ValueType.ObjRef)]
    [InlineData(0x03, ValueType.VarInt)]
    [InlineData(0x04, ValueType.Ptr64)]
    public void ExtractValueType_LowBits_ReturnsCorrectValueType(byte kvpType, ValueType expected) {
        ValueTypeExtensions.ExtractValueType(kvpType).Should().Be(expected);
    }

    /// <summary>
    /// 高 4 bit 被忽略，只提取低 4 bit。
    /// </summary>
    [Theory]
    [InlineData(0xF0, ValueType.Null)]      // 高 4 bit 全 1，低 4 bit = 0
    [InlineData(0xF1, ValueType.Tombstone)] // 高 4 bit 全 1，低 4 bit = 1
    [InlineData(0x52, ValueType.ObjRef)]    // 高 4 bit = 5，低 4 bit = 2
    [InlineData(0xA3, ValueType.VarInt)]    // 高 4 bit = A，低 4 bit = 3
    [InlineData(0x84, ValueType.Ptr64)]     // 高 4 bit = 8，低 4 bit = 4
    public void ExtractValueType_IgnoresHighBits(byte kvpType, ValueType expected) {
        ValueTypeExtensions.ExtractValueType(kvpType).Should().Be(expected);
    }

    #endregion

    #region AreHighBitsZero 测试

    /// <summary>
    /// 高 4 bit 为 0 时返回 true。
    /// </summary>
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x03)]
    [InlineData(0x04)]
    [InlineData(0x0F)]
    public void AreHighBitsZero_HighBitsAreZero_ReturnsTrue(byte kvpType) {
        ValueTypeExtensions.AreHighBitsZero(kvpType).Should().BeTrue();
    }

    /// <summary>
    /// [F-KVPAIR-HIGHBITS-RESERVED] 高 4 bit 非零时返回 false。
    /// </summary>
    [Theory]
    [InlineData(0x10)]
    [InlineData(0x20)]
    [InlineData(0x40)]
    [InlineData(0x80)]
    [InlineData(0xF0)]
    [InlineData(0xFF)]
    [InlineData(0x11)]
    [InlineData(0x52)]
    public void AreHighBitsZero_HighBitsNonZero_ReturnsFalse(byte kvpType) {
        ValueTypeExtensions.AreHighBitsZero(kvpType).Should().BeFalse();
    }

    #endregion

    #region ValidateKeyValuePairType 测试

    /// <summary>
    /// 合法的 KeyValuePairType 返回成功。
    /// </summary>
    [Theory]
    [InlineData(0x00, ValueType.Null)]
    [InlineData(0x01, ValueType.Tombstone)]
    [InlineData(0x02, ValueType.ObjRef)]
    [InlineData(0x03, ValueType.VarInt)]
    [InlineData(0x04, ValueType.Ptr64)]
    public void ValidateKeyValuePairType_Valid_ReturnsSuccess(byte kvpType, ValueType expected) {
        var result = ValueTypeExtensions.ValidateKeyValuePairType(kvpType);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    /// <summary>
    /// [F-KVPAIR-HIGHBITS-RESERVED] 高 4 bit 非零时返回失败。
    /// </summary>
    [Theory]
    [InlineData(0x10)] // 高 bit 非零，低 bit = 0 (Null)
    [InlineData(0x21)] // 高 bit 非零，低 bit = 1 (Tombstone)
    [InlineData(0x42)] // 高 bit 非零，低 bit = 2 (ObjRef)
    [InlineData(0x83)] // 高 bit 非零，低 bit = 3 (VarInt)
    [InlineData(0xF4)] // 高 bit 非零，低 bit = 4 (Ptr64)
    public void ValidateKeyValuePairType_HighBitsNonZero_ReturnsFailure(byte kvpType) {
        var result = ValueTypeExtensions.ValidateKeyValuePairType(kvpType);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<DiffPayloadFormatError>();
        result.Error!.Message.Should().Contain("high 4 bits");
    }

    /// <summary>
    /// [F-UNKNOWN-VALUETYPE-REJECT] 未知 ValueType 返回失败。
    /// </summary>
    [Theory]
    [InlineData(0x05)]
    [InlineData(0x06)]
    [InlineData(0x07)]
    [InlineData(0x08)]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0B)]
    [InlineData(0x0C)]
    [InlineData(0x0D)]
    [InlineData(0x0E)]
    [InlineData(0x0F)]
    public void ValidateKeyValuePairType_UnknownValueType_ReturnsFailure(byte kvpType) {
        var result = ValueTypeExtensions.ValidateKeyValuePairType(kvpType);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<UnknownValueTypeError>();
    }

    #endregion

    #region 常量测试

    /// <summary>
    /// 验证常量值正确。
    /// </summary>
    [Fact]
    public void Constants_HaveCorrectValues() {
        ValueTypeExtensions.MaxKnownValueType.Should().Be(0x4);
        ValueTypeExtensions.HighBitsMask.Should().Be(0xF0);
        ValueTypeExtensions.LowBitsMask.Should().Be(0x0F);
    }

    #endregion
}
