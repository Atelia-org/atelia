using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Integer.cs`

/// <summary>
/// <see cref="ValueBox"/> 的 short / ushort / sbyte / byte 四种小整数类型的单元测试。
/// </summary>
/// <remarks>
/// 这些类型的值域远小于 inline 容量，始终 inline，无堆分配。
/// Get(out T) 委托给 Get(out long/ulong) 后做范围检查。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxSmallIntTests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static int PoolCount => ValuePools.Bits64.Count;

    // ═══════════════════════ short (Int16) ═══════════════════════

    #region short

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)1)]
    [InlineData((short)-1)]
    [InlineData(short.MaxValue)]
    [InlineData(short.MinValue)]
    public void FromInt16_Roundtrip(short value) {
        var box = ValueBox.FromInt16(value);
        GetIssue issue = box.Get(out short actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromInt16_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.FromInt16(short.MaxValue);
        _ = ValueBox.FromInt16(short.MinValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void SameInlineSameValueBox_Int16_EqualsInt64() =>
        Assert.Equal(ValueBox.FromInt16(100).GetBits(), ValueBox.FromInt64(100).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int16Negative_EqualsInt64() =>
        Assert.Equal(ValueBox.FromInt16(-100).GetBits(), ValueBox.FromInt64(-100).GetBits());

    [Fact]
    public void GetShort_FromIntJustAboveShortMax_Saturated() {
        var box = ValueBox.FromInt32(short.MaxValue + 1);
        GetIssue issue = box.Get(out short value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(short.MaxValue, value);
    }

    [Fact]
    public void GetShort_FromIntJustBelowShortMin_Saturated() {
        var box = ValueBox.FromInt32(short.MinValue - 1);
        GetIssue issue = box.Get(out short value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(short.MinValue, value);
    }

    [Fact]
    public void GetShort_FromLongMax_Saturated() {
        var box = ValueBox.FromInt64(long.MaxValue);
        GetIssue issue = box.Get(out short value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(short.MaxValue, value);
    }

    [Fact]
    public void GetShort_FromLongMin_Saturated() {
        var box = ValueBox.FromInt64(long.MinValue);
        GetIssue issue = box.Get(out short value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(short.MinValue, value);
    }

    [Fact]
    public void GetShort_AtBoundary_None() {
        var box1 = ValueBox.FromInt64(short.MaxValue);
        Assert.Equal(GetIssue.None, box1.Get(out short v1));
        Assert.Equal(short.MaxValue, v1);

        var box2 = ValueBox.FromInt64(short.MinValue);
        Assert.Equal(GetIssue.None, box2.Get(out short v2));
        Assert.Equal(short.MinValue, v2);
    }

    [Fact]
    public void GetShort_FromDouble_TypeMismatch() {
        var box = ValueBox.FromRoundedDouble(1.5);
        GetIssue issue = box.Get(out short value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    #endregion

    // ═══════════════════════ ushort (UInt16) ═══════════════════════

    #region ushort

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData(ushort.MaxValue)]
    public void FromUInt16_Roundtrip(ushort value) {
        var box = ValueBox.FromUInt16(value);
        GetIssue issue = box.Get(out ushort actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromUInt16_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.FromUInt16(ushort.MaxValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void SameInlineSameValueBox_UInt16_EqualsUInt64() =>
        Assert.Equal(ValueBox.FromUInt16(60000).GetBits(), ValueBox.FromUInt64(60000).GetBits());

    [Fact]
    public void GetUShort_FromJustAboveUShortMax_Saturated() {
        var box = ValueBox.FromUInt32(ushort.MaxValue + 1U);
        GetIssue issue = box.Get(out ushort value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(ushort.MaxValue, value);
    }

    [Fact]
    public void GetUShort_FromUInt64Max_Saturated() {
        var box = ValueBox.FromUInt64(ulong.MaxValue);
        GetIssue issue = box.Get(out ushort value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(ushort.MaxValue, value);
    }

    [Fact]
    public void GetUShort_FromNegative_Saturated() {
        var box = ValueBox.FromInt64(-1);
        GetIssue issue = box.Get(out ushort value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(ushort.MinValue, value); // 饱和到 0
    }

    [Fact]
    public void GetUShort_AtBoundary_None() {
        var box = ValueBox.FromUInt64(ushort.MaxValue);
        Assert.Equal(GetIssue.None, box.Get(out ushort v));
        Assert.Equal(ushort.MaxValue, v);
    }

    [Fact]
    public void GetUShort_FromDouble_TypeMismatch() {
        var box = ValueBox.FromRoundedDouble(1.5);
        GetIssue issue = box.Get(out ushort value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    #endregion

    // ═══════════════════════ sbyte (SByte) ═══════════════════════

    #region sbyte

    [Theory]
    [InlineData((sbyte)0)]
    [InlineData((sbyte)1)]
    [InlineData((sbyte)-1)]
    [InlineData(sbyte.MaxValue)]
    [InlineData(sbyte.MinValue)]
    public void FromSByte_Roundtrip(sbyte value) {
        var box = ValueBox.FromSByte(value);
        GetIssue issue = box.Get(out sbyte actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromSByte_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.FromSByte(sbyte.MaxValue);
        _ = ValueBox.FromSByte(sbyte.MinValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void SameInlineSameValueBox_SByte_EqualsInt64() =>
        Assert.Equal(ValueBox.FromSByte(-1).GetBits(), ValueBox.FromInt64(-1).GetBits());

    [Fact]
    public void SameInlineSameValueBox_SBytePositive_EqualsByte() =>
        Assert.Equal(ValueBox.FromSByte(42).GetBits(), ValueBox.FromByte(42).GetBits());

    [Fact]
    public void GetSByte_FromJustAboveSByteMax_Saturated() {
        var box = ValueBox.FromInt32(sbyte.MaxValue + 1);
        GetIssue issue = box.Get(out sbyte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(sbyte.MaxValue, value);
    }

    [Fact]
    public void GetSByte_FromJustBelowSByteMin_Saturated() {
        var box = ValueBox.FromInt32(sbyte.MinValue - 1);
        GetIssue issue = box.Get(out sbyte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(sbyte.MinValue, value);
    }

    [Fact]
    public void GetSByte_FromLongMax_Saturated() {
        var box = ValueBox.FromInt64(long.MaxValue);
        GetIssue issue = box.Get(out sbyte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(sbyte.MaxValue, value);
    }

    [Fact]
    public void GetSByte_AtBoundary_None() {
        var box1 = ValueBox.FromInt64(sbyte.MaxValue);
        Assert.Equal(GetIssue.None, box1.Get(out sbyte v1));
        Assert.Equal(sbyte.MaxValue, v1);

        var box2 = ValueBox.FromInt64(sbyte.MinValue);
        Assert.Equal(GetIssue.None, box2.Get(out sbyte v2));
        Assert.Equal(sbyte.MinValue, v2);
    }

    [Fact]
    public void GetSByte_FromDouble_TypeMismatch() {
        var box = ValueBox.FromRoundedDouble(1.5);
        GetIssue issue = box.Get(out sbyte value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    #endregion

    // ═══════════════════════ byte (Byte) ═══════════════════════

    #region byte

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData(byte.MaxValue)]
    public void FromByte_Roundtrip(byte value) {
        var box = ValueBox.FromByte(value);
        GetIssue issue = box.Get(out byte actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromByte_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.FromByte(byte.MaxValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void SameInlineSameValueBox_Byte_EqualsUInt64() =>
        Assert.Equal(ValueBox.FromByte(200).GetBits(), ValueBox.FromUInt64(200).GetBits());

    [Fact]
    public void GetByte_FromJustAboveByteMax_Saturated() {
        var box = ValueBox.FromUInt32(byte.MaxValue + 1U);
        GetIssue issue = box.Get(out byte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(byte.MaxValue, value);
    }

    [Fact]
    public void GetByte_FromUInt64Max_Saturated() {
        var box = ValueBox.FromUInt64(ulong.MaxValue);
        GetIssue issue = box.Get(out byte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(byte.MaxValue, value);
    }

    [Fact]
    public void GetByte_FromNegative_Saturated() {
        var box = ValueBox.FromInt64(-1);
        GetIssue issue = box.Get(out byte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(byte.MinValue, value); // 饱和到 0
    }

    [Fact]
    public void GetByte_AtBoundary_None() {
        var box = ValueBox.FromUInt64(byte.MaxValue);
        Assert.Equal(GetIssue.None, box.Get(out byte v));
        Assert.Equal(byte.MaxValue, v);
    }

    [Fact]
    public void GetByte_FromDouble_TypeMismatch() {
        var box = ValueBox.FromRoundedDouble(1.5);
        GetIssue issue = box.Get(out byte value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    #endregion
}
