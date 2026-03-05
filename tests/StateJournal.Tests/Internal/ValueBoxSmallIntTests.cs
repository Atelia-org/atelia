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

    private static int PoolCount => ValuePools.OfBits64.Count;

    // ═══════════════════════ short (Int16) ═══════════════════════

    #region short

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)1)]
    [InlineData((short)-1)]
    [InlineData(short.MaxValue)]
    [InlineData(short.MinValue)]
    public void FromInt16_Roundtrip(short value) {
        var box = ValueBox.Int16Face.From(value);
        GetIssue issue = ValueBox.Int16Face.Get(box, out short actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromInt16_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.Int16Face.From(short.MaxValue);
        _ = ValueBox.Int16Face.From(short.MinValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void SameInlineSameValueBox_Int16_EqualsInt64() =>
        Assert.Equal(ValueBox.Int16Face.From(100).GetBits(), ValueBox.Int64Face.From(100).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int16Negative_EqualsInt64() =>
        Assert.Equal(ValueBox.Int16Face.From(-100).GetBits(), ValueBox.Int64Face.From(-100).GetBits());

    [Fact]
    public void GetShort_FromIntJustAboveShortMax_Saturated() {
        var box = ValueBox.Int32Face.From(short.MaxValue + 1);
        GetIssue issue = ValueBox.Int16Face.Get(box, out short value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(short.MaxValue, value);
    }

    [Fact]
    public void GetShort_FromIntJustBelowShortMin_Saturated() {
        var box = ValueBox.Int32Face.From(short.MinValue - 1);
        GetIssue issue = ValueBox.Int16Face.Get(box, out short value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(short.MinValue, value);
    }

    [Fact]
    public void GetShort_FromLongMax_Saturated() {
        var box = ValueBox.Int64Face.From(long.MaxValue);
        GetIssue issue = ValueBox.Int16Face.Get(box, out short value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(short.MaxValue, value);
    }

    [Fact]
    public void GetShort_FromLongMin_Saturated() {
        var box = ValueBox.Int64Face.From(long.MinValue);
        GetIssue issue = ValueBox.Int16Face.Get(box, out short value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(short.MinValue, value);
    }

    [Fact]
    public void GetShort_AtBoundary_None() {
        var box1 = ValueBox.Int64Face.From(short.MaxValue);
        Assert.Equal(GetIssue.None, ValueBox.Int16Face.Get(box1, out short v1));
        Assert.Equal(short.MaxValue, v1);

        var box2 = ValueBox.Int64Face.From(short.MinValue);
        Assert.Equal(GetIssue.None, ValueBox.Int16Face.Get(box2, out short v2));
        Assert.Equal(short.MinValue, v2);
    }

    [Fact]
    public void GetShort_FromDouble_TypeMismatch() {
        var box = ValueBox.RoundedDoubleFace.From(1.5);
        GetIssue issue = ValueBox.Int16Face.Get(box, out short value);
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
        var box = ValueBox.UInt16Face.From(value);
        GetIssue issue = ValueBox.UInt16Face.Get(box, out ushort actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromUInt16_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.UInt16Face.From(ushort.MaxValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void SameInlineSameValueBox_UInt16_EqualsUInt64() =>
        Assert.Equal(ValueBox.UInt16Face.From(60000).GetBits(), ValueBox.UInt64Face.From(60000).GetBits());

    [Fact]
    public void GetUShort_FromJustAboveUShortMax_Saturated() {
        var box = ValueBox.UInt32Face.From(ushort.MaxValue + 1U);
        GetIssue issue = ValueBox.UInt16Face.Get(box, out ushort value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(ushort.MaxValue, value);
    }

    [Fact]
    public void GetUShort_FromUInt64Max_Saturated() {
        var box = ValueBox.UInt64Face.From(ulong.MaxValue);
        GetIssue issue = ValueBox.UInt16Face.Get(box, out ushort value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(ushort.MaxValue, value);
    }

    [Fact]
    public void GetUShort_FromNegative_Saturated() {
        var box = ValueBox.Int64Face.From(-1);
        GetIssue issue = ValueBox.UInt16Face.Get(box, out ushort value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(ushort.MinValue, value); // 饱和到 0
    }

    [Fact]
    public void GetUShort_AtBoundary_None() {
        var box = ValueBox.UInt64Face.From(ushort.MaxValue);
        Assert.Equal(GetIssue.None, ValueBox.UInt16Face.Get(box, out ushort v));
        Assert.Equal(ushort.MaxValue, v);
    }

    [Fact]
    public void GetUShort_FromDouble_TypeMismatch() {
        var box = ValueBox.RoundedDoubleFace.From(1.5);
        GetIssue issue = ValueBox.UInt16Face.Get(box, out ushort value);
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
        var box = ValueBox.SByteFace.From(value);
        GetIssue issue = ValueBox.SByteFace.Get(box, out sbyte actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromSByte_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.SByteFace.From(sbyte.MaxValue);
        _ = ValueBox.SByteFace.From(sbyte.MinValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void SameInlineSameValueBox_SByte_EqualsInt64() =>
        Assert.Equal(ValueBox.SByteFace.From(-1).GetBits(), ValueBox.Int64Face.From(-1).GetBits());

    [Fact]
    public void SameInlineSameValueBox_SBytePositive_EqualsByte() =>
        Assert.Equal(ValueBox.SByteFace.From(42).GetBits(), ValueBox.ByteFace.From(42).GetBits());

    [Fact]
    public void GetSByte_FromJustAboveSByteMax_Saturated() {
        var box = ValueBox.Int32Face.From(sbyte.MaxValue + 1);
        GetIssue issue = ValueBox.SByteFace.Get(box, out sbyte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(sbyte.MaxValue, value);
    }

    [Fact]
    public void GetSByte_FromJustBelowSByteMin_Saturated() {
        var box = ValueBox.Int32Face.From(sbyte.MinValue - 1);
        GetIssue issue = ValueBox.SByteFace.Get(box, out sbyte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(sbyte.MinValue, value);
    }

    [Fact]
    public void GetSByte_FromLongMax_Saturated() {
        var box = ValueBox.Int64Face.From(long.MaxValue);
        GetIssue issue = ValueBox.SByteFace.Get(box, out sbyte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(sbyte.MaxValue, value);
    }

    [Fact]
    public void GetSByte_AtBoundary_None() {
        var box1 = ValueBox.Int64Face.From(sbyte.MaxValue);
        Assert.Equal(GetIssue.None, ValueBox.SByteFace.Get(box1, out sbyte v1));
        Assert.Equal(sbyte.MaxValue, v1);

        var box2 = ValueBox.Int64Face.From(sbyte.MinValue);
        Assert.Equal(GetIssue.None, ValueBox.SByteFace.Get(box2, out sbyte v2));
        Assert.Equal(sbyte.MinValue, v2);
    }

    [Fact]
    public void GetSByte_FromDouble_TypeMismatch() {
        var box = ValueBox.RoundedDoubleFace.From(1.5);
        GetIssue issue = ValueBox.SByteFace.Get(box, out sbyte value);
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
        var box = ValueBox.ByteFace.From(value);
        GetIssue issue = ValueBox.ByteFace.Get(box, out byte actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromByte_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.ByteFace.From(byte.MaxValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void SameInlineSameValueBox_Byte_EqualsUInt64() =>
        Assert.Equal(ValueBox.ByteFace.From(200).GetBits(), ValueBox.UInt64Face.From(200).GetBits());

    [Fact]
    public void GetByte_FromJustAboveByteMax_Saturated() {
        var box = ValueBox.UInt32Face.From(byte.MaxValue + 1U);
        GetIssue issue = ValueBox.ByteFace.Get(box, out byte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(byte.MaxValue, value);
    }

    [Fact]
    public void GetByte_FromUInt64Max_Saturated() {
        var box = ValueBox.UInt64Face.From(ulong.MaxValue);
        GetIssue issue = ValueBox.ByteFace.Get(box, out byte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(byte.MaxValue, value);
    }

    [Fact]
    public void GetByte_FromNegative_Saturated() {
        var box = ValueBox.Int64Face.From(-1);
        GetIssue issue = ValueBox.ByteFace.Get(box, out byte value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(byte.MinValue, value); // 饱和到 0
    }

    [Fact]
    public void GetByte_AtBoundary_None() {
        var box = ValueBox.UInt64Face.From(byte.MaxValue);
        Assert.Equal(GetIssue.None, ValueBox.ByteFace.Get(box, out byte v));
        Assert.Equal(byte.MaxValue, v);
    }

    [Fact]
    public void GetByte_FromDouble_TypeMismatch() {
        var box = ValueBox.RoundedDoubleFace.From(1.5);
        GetIssue issue = ValueBox.ByteFace.Get(box, out byte value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    #endregion
}
