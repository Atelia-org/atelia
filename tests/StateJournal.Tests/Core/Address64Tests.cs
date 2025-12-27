using Atelia.Rbf;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

// Ptr64 别名：在 StateJournal 项目中通过 global using 定义，测试项目需本地定义
using Ptr64 = Atelia.Rbf.Address64;

namespace Atelia.StateJournal.Tests.Core;

/// <summary>
/// Address64 和 Ptr64 类型测试。
/// </summary>
/// <remarks>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[F-ADDRESS64-DEFINITION]</c></item>
///   <item><c>[F-ADDRESS64-ALIGNMENT]</c></item>
///   <item><c>[F-ADDRESS64-NULL]</c></item>
/// </list>
/// </remarks>
public class Address64Tests {
    #region Address64.Null 测试

    /// <summary>
    /// [F-ADDRESS64-NULL] Address64.Null.Value == 0
    /// </summary>
    [Fact]
    public void Null_HasValueZero() {
        Address64.Null.Value.Should().Be(0UL);
    }

    /// <summary>
    /// [F-ADDRESS64-NULL] Address64.Null.IsNull == true
    /// </summary>
    [Fact]
    public void Null_IsNullReturnsTrue() {
        Address64.Null.IsNull.Should().BeTrue();
    }

    /// <summary>
    /// 非零地址的 IsNull 应返回 false
    /// </summary>
    [Theory]
    [InlineData(4UL)]
    [InlineData(8UL)]
    [InlineData(1024UL)]
    public void NonZeroAddress_IsNullReturnsFalse(ulong value) {
        var addr = new Address64(value);
        addr.IsNull.Should().BeFalse();
    }

    #endregion

    #region IsValid 测试 (4 字节对齐)

    /// <summary>
    /// [F-ADDRESS64-ALIGNMENT] 4 字节对齐的非零地址是有效的
    /// </summary>
    [Theory]
    [InlineData(4UL)]
    [InlineData(8UL)]
    [InlineData(12UL)]
    [InlineData(1024UL)]
    [InlineData(4096UL)]
    public void AlignedNonZeroAddress_IsValidReturnsTrue(ulong value) {
        var addr = new Address64(value);
        addr.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// [F-ADDRESS64-ALIGNMENT] 非 4 字节对齐的地址是无效的
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(5UL)]
    [InlineData(7UL)]
    [InlineData(1001UL)]
    public void UnalignedAddress_IsValidReturnsFalse(ulong value) {
        var addr = new Address64(value);
        addr.IsValid.Should().BeFalse();
    }

    /// <summary>
    /// [F-ADDRESS64-NULL] Null 地址的 IsValid 返回 false
    /// </summary>
    [Fact]
    public void NullAddress_IsValidReturnsFalse() {
        Address64.Null.IsValid.Should().BeFalse();
    }

    #endregion

    #region TryFromOffset 测试

    /// <summary>
    /// [F-ADDRESS64-ALIGNMENT] TryFromOffset 对齐成功
    /// </summary>
    [Theory]
    [InlineData(4UL)]
    [InlineData(8UL)]
    [InlineData(1024UL)]
    public void TryFromOffset_AlignedValue_ReturnsSuccess(ulong offset) {
        var result = Address64Extensions.TryFromOffset(offset);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(offset);
    }

    /// <summary>
    /// [F-ADDRESS64-ALIGNMENT] TryFromOffset 非对齐返回 AddressAlignmentError
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(5UL)]
    [InlineData(7UL)]
    public void TryFromOffset_UnalignedValue_ReturnsFailure(ulong offset) {
        var result = Address64Extensions.TryFromOffset(offset);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<AddressAlignmentError>();

        var error = (AddressAlignmentError)result.Error!;
        error.Address.Should().Be(offset);
    }

    /// <summary>
    /// [F-ADDRESS64-NULL] TryFromOffset(0) 返回 Address64.Null
    /// </summary>
    [Fact]
    public void TryFromOffset_Zero_ReturnsNullAddress() {
        var result = Address64Extensions.TryFromOffset(0UL);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Address64.Null);
        result.Value.IsNull.Should().BeTrue();
    }

    /// <summary>
    /// TryFromOffset(long) 负数抛出 ArgumentOutOfRangeException
    /// </summary>
    [Fact]
    public void TryFromOffset_NegativeLong_ThrowsArgumentOutOfRangeException() {
        var act = () => Address64Extensions.TryFromOffset(-1L);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// TryFromOffset(long) 正数正常工作
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(4L)]
    [InlineData(8L)]
    public void TryFromOffset_PositiveLong_Works(long offset) {
        var result = Address64Extensions.TryFromOffset(offset);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be((ulong)offset);
    }

    #endregion

    #region 隐式/显式转换测试

    /// <summary>
    /// Address64 可以隐式转换为 ulong
    /// </summary>
    [Fact]
    public void Address64_ImplicitConversionToUlong_Works() {
        var addr = new Address64(1024);
        ulong value = addr;

        value.Should().Be(1024UL);
    }

    /// <summary>
    /// ulong 可以显式转换为 Address64
    /// </summary>
    [Fact]
    public void Ulong_ExplicitConversionToAddress64_Works() {
        ulong value = 2048;
        var addr = (Address64)value;

        addr.Value.Should().Be(2048UL);
    }

    #endregion

    #region Ptr64 别名测试

    /// <summary>
    /// Ptr64 是 Address64 的别名
    /// </summary>
    [Fact]
    public void Ptr64_IsAliasForAddress64() {
        Ptr64 ptr = new(4);
        Address64 addr = ptr;

        // 验证它们是同一类型
        addr.Should().Be(ptr);
        ptr.Value.Should().Be(4UL);
        ptr.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// Ptr64.Null 与 Address64.Null 相等
    /// </summary>
    [Fact]
    public void Ptr64Null_EqualsAddress64Null() {
        Ptr64 ptrNull = Ptr64.Null;
        Address64 addrNull = Address64.Null;

        ptrNull.Should().Be(addrNull);
        ptrNull.IsNull.Should().BeTrue();
    }

    #endregion

    #region Record struct 相等性测试

    /// <summary>
    /// 相同 Value 的 Address64 应相等
    /// </summary>
    [Fact]
    public void SameValue_AreEqual() {
        var addr1 = new Address64(100);
        var addr2 = new Address64(100);

        addr1.Should().Be(addr2);
        (addr1 == addr2).Should().BeTrue();
    }

    /// <summary>
    /// 不同 Value 的 Address64 不相等
    /// </summary>
    [Fact]
    public void DifferentValue_AreNotEqual() {
        var addr1 = new Address64(100);
        var addr2 = new Address64(200);

        addr1.Should().NotBe(addr2);
        (addr1 != addr2).Should().BeTrue();
    }

    #endregion

    #region AddressAlignmentError 测试

    /// <summary>
    /// AddressAlignmentError 包含正确的错误信息
    /// </summary>
    [Fact]
    public void AddressAlignmentError_HasCorrectMessage() {
        var error = new AddressAlignmentError(5);

        error.ErrorCode.Should().Be("StateJournal.Address.Alignment");
        error.Address.Should().Be(5UL);
        error.Message.Should().Contain("5");
        error.Message.Should().Contain("4-byte aligned");
    }

    #endregion
}
