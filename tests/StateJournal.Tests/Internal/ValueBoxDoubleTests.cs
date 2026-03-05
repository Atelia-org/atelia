using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Float.cs`

/// <summary>
/// <see cref="ValueBox.RoundedDoubleFace.From"/>、<see cref="ValueBox.ExactDoubleFace.From"/>
/// 和 <see cref="ValueBox.GetDouble(out double)"/> 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - FromRoundedDouble：LSB=0 值精确往返；LSB=1 值有损（±1 ULP），验证 round-to-odd 舍入行为。始终 inline。
/// - FromExactDouble：LSB=0 → inline；LSB=1 → heap；全部精确往返。
/// - 同值同码 @[SAME-INLINE-SAME-VALUEBOX]：float/Half 拓宽为 double 后应与对应 double 产生相同 bits。
/// - Get(out double) 从整数源隐式转换：精确转换（≤53-bit significand）或 PrecisionLost。
/// - 特殊值：NaN, ±Infinity, -0.0。
/// - TypeMismatch：来自 Null/Boolean/Undefined 的 ValueBox。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxDoubleTests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static int PoolCount => ValuePools.OfBits64.Count;

    /// <summary>断言两个 double 在 IEEE 754 位级别精确相等（区分 -0.0 和 NaN payload）。</summary>
    private static void AssertDoubleBitsEqual(double expected, double actual) =>
        Assert.Equal(BitConverter.DoubleToUInt64Bits(expected), BitConverter.DoubleToUInt64Bits(actual));

    // ═══════════════════════ FromRoundedDouble — LSB=0 精确往返 ═══════════════════════

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(0.5)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FromRoundedDouble_LSB0_ExactRoundtrip(double value) {
        var box = ValueBox.RoundedDoubleFace.From(value);
        GetIssue issue = box.GetDouble(out double actual);
        Assert.Equal(GetIssue.None, issue);
        AssertDoubleBitsEqual(value, actual);
    }

    [Fact]
    public void FromRoundedDouble_NaN_Roundtrip() {
        // canonical quiet NaN: 0x7FF8_0000_0000_0000，LSB=0
        var box = ValueBox.RoundedDoubleFace.From(double.NaN);
        GetIssue issue = box.GetDouble(out double actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.True(double.IsNaN(actual));
        AssertDoubleBitsEqual(double.NaN, actual);
    }

    [Fact]
    public void FromRoundedDouble_NegativeZero_PreservesBits() {
        double neg0 = BitConverter.UInt64BitsToDouble(0x8000_0000_0000_0000);
        var box = ValueBox.RoundedDoubleFace.From(neg0);
        GetIssue issue = box.GetDouble(out double actual);
        Assert.Equal(GetIssue.None, issue);
        AssertDoubleBitsEqual(neg0, actual);
    }

    // ═══════════════════════ FromRoundedDouble — LSB=1 有损编码 (Round-to-Odd) ═══════════════════════

    [Fact]
    public void FromRoundedDouble_LSB1_bits01_RoundUp1ULP() {
        // bits[1:0]=01 → sticky=1 → decoded bits[1:0]=10 → error +1 ULP
        double original = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = ValueBox.RoundedDoubleFace.From(original);
        GetIssue issue = box.GetDouble(out double decoded);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(0x3FF0_0000_0000_0002UL, BitConverter.DoubleToUInt64Bits(decoded));
    }

    [Fact]
    public void FromRoundedDouble_LSB1_bits11_RoundDown1ULP() {
        // bits[1:0]=11 → sticky already 1 → decoded bits[1:0]=10 → error -1 ULP
        double original = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0003);
        var box = ValueBox.RoundedDoubleFace.From(original);
        GetIssue issue = box.GetDouble(out double decoded);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(0x3FF0_0000_0000_0002UL, BitConverter.DoubleToUInt64Bits(decoded));
    }

    [Fact]
    public void FromRoundedDouble_LSB0_bits10_Exact() {
        // bits[1:0]=10 → LSB=0 → exact roundtrip
        double original = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0002);
        var box = ValueBox.RoundedDoubleFace.From(original);
        GetIssue issue = box.GetDouble(out double decoded);
        Assert.Equal(GetIssue.None, issue);
        AssertDoubleBitsEqual(original, decoded);
    }

    [Fact]
    public void FromRoundedDouble_DoubleMaxValue_LossyButFinite() {
        // double.MaxValue 的 IEEE bits LSB=1，有损编码后仍为有限数
        var box = ValueBox.RoundedDoubleFace.From(double.MaxValue);
        GetIssue issue = box.GetDouble(out double decoded);
        Assert.Equal(GetIssue.None, issue);
        Assert.NotEqual(
            BitConverter.DoubleToUInt64Bits(double.MaxValue),
            BitConverter.DoubleToUInt64Bits(decoded)
        );
        Assert.True(double.IsFinite(decoded));
    }

    [Fact]
    public void FromRoundedDouble_AlwaysInline_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.RoundedDoubleFace.From(double.MaxValue);
        _ = ValueBox.RoundedDoubleFace.From(double.MinValue);
        _ = ValueBox.RoundedDoubleFace.From(double.NaN);
        _ = ValueBox.RoundedDoubleFace.From(double.PositiveInfinity);
        Assert.Equal(before, PoolCount);
    }

    // ═══════════════════════ FromExactDouble — 精确往返 ═══════════════════════

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(0.5)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FromExactDouble_ExactRoundtrip(double value) {
        var box = ValueBox.ExactDoubleFace.From(value);
        GetIssue issue = box.GetDouble(out double actual);
        Assert.Equal(GetIssue.None, issue);
        AssertDoubleBitsEqual(value, actual);
    }

    [Fact]
    public void FromExactDouble_LSB0_Inline_NoPoolAllocation() {
        int before = PoolCount;
        var box = ValueBox.ExactDoubleFace.From(1.0); // LSB=0 → inline
        Assert.Equal(before, PoolCount);
        GetIssue issue = box.GetDouble(out double decoded);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(1.0, decoded);
    }

    [Fact]
    public void FromExactDouble_LSB1_HeapAllocation_ExactRoundtrip() {
        double original = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        int before = PoolCount;
        var box = ValueBox.ExactDoubleFace.From(original);
        Assert.Equal(before + 1, PoolCount);
        GetIssue issue = box.GetDouble(out double decoded);
        Assert.Equal(GetIssue.None, issue);
        AssertDoubleBitsEqual(original, decoded);
    }

    [Fact]
    public void FromExactDouble_DoubleMaxValue_HeapAllocation_ExactRoundtrip() {
        // double.MaxValue 的 LSB=1 → heap
        int before = PoolCount;
        var box = ValueBox.ExactDoubleFace.From(double.MaxValue);
        Assert.Equal(before + 1, PoolCount);
        GetIssue issue = box.GetDouble(out double decoded);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(double.MaxValue, decoded);
    }

    // ═══════════════════════ 同值同码 @[SAME-INLINE-SAME-VALUEBOX] ═══════════════════════

    [Fact]
    public void SameInlineSameValueBox_Float05_EqualsDouble05() =>
        Assert.Equal(ValueBox.SingleFace.From(0.5f).GetBits(), ValueBox.RoundedDoubleFace.From(0.5).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Half1_EqualsDouble1() =>
        Assert.Equal(ValueBox.HalfFace.From((Half)1.0).GetBits(), ValueBox.RoundedDoubleFace.From(1.0).GetBits());

    [Fact]
    public void SameInlineSameValueBox_FloatPosInf_EqualsDoublePosInf() =>
        Assert.Equal(
            ValueBox.SingleFace.From(float.PositiveInfinity).GetBits(),
            ValueBox.RoundedDoubleFace.From(double.PositiveInfinity).GetBits()
        );

    [Fact]
    public void SameInlineSameValueBox_HalfNegInf_EqualsDoubleNegInf() =>
        Assert.Equal(
            ValueBox.HalfFace.From(Half.NegativeInfinity).GetBits(),
            ValueBox.RoundedDoubleFace.From(double.NegativeInfinity).GetBits()
        );

    // ═══════════════════════ Get(out double) — 从 float/Half 源（内部均为 inline double）═══════════════════════

    [Fact]
    public void GetDouble_FromSingle_ReturnsWidenedDouble() {
        float f = 3.14f;
        var box = ValueBox.SingleFace.From(f);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((double)f, value);
    }

    [Fact]
    public void GetDouble_FromHalf_ReturnsWidenedDouble() {
        Half h = (Half)2.5;
        var box = ValueBox.HalfFace.From(h);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((double)h, value);
    }

    // ═══════════════════════ Get(out double) — 从整数源：精确转换 ═══════════════════════

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(42L)]
    [InlineData(-42L)]
    public void GetDouble_FromInlineInt_Exact(long intValue) {
        var box = ValueBox.Int64Face.From(intValue);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((double)intValue, value);
    }

    [Fact]
    public void GetDouble_FromInlineNonnegInt_53BitBoundary_Exact() {
        // 2^53 − 1 = 9007199254740991：53 个有效位，恰好精确
        long v = (1L << 53) - 1;
        var box = ValueBox.Int64Face.From(v);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((double)v, value);
    }

    // ═══════════════════════ Get(out double) — 从整数源：PrecisionLost ═══════════════════════

    [Fact]
    public void GetDouble_FromInlineNonnegInt_54Bit_PrecisionLost() {
        // 2^53 + 1：54 个有效位，超出 double 53-bit significand
        long v = (1L << 53) + 1;
        var box = ValueBox.Int64Face.From(v);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((double)v, value);
    }

    [Fact]
    public void GetDouble_FromInlineNegInt_PrecisionLost() {
        // −(2^53 + 1)：magnitude 有 54 个有效位
        long v = -((1L << 53) + 1);
        var box = ValueBox.Int64Face.From(v);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((double)v, value);
    }

    // ═══════════════════════ Get(out double) — 从堆整数源 ═══════════════════════

    [Fact]
    public void GetDouble_FromHeapNonnegInt_PowerOf2_Exact() {
        // 2^62 是 2 的幂 → 精确；但超出 inline 容量 → heap
        long v = (long)LzcConstants.NonnegIntInlineCap;
        var box = ValueBox.Int64Face.From(v);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((double)v, value);
    }

    [Fact]
    public void GetDouble_FromHeapNonnegInt_NotPowerOf2_PrecisionLost() {
        // 2^62 + 1：63 个有效位 → PrecisionLost
        long v = (long)LzcConstants.NonnegIntInlineCap + 1;
        var box = ValueBox.Int64Face.From(v);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((double)v, value);
    }

    [Fact]
    public void GetDouble_FromHeapNegInt_PowerOf2_Exact() {
        // long.MinValue = −2^63，2 的幂 → exact
        var box = ValueBox.Int64Face.From(long.MinValue);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((double)long.MinValue, value);
    }

    [Fact]
    public void GetDouble_FromHeapNegInt_NotPowerOf2_PrecisionLost() {
        // long.MinValue + 1 = −(2^63 − 1)：63 个有效位 → PrecisionLost
        var box = ValueBox.Int64Face.From(long.MinValue + 1);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((double)(long.MinValue + 1), value);
    }

    [Fact]
    public void GetDouble_FromHeapUInt64Max_PrecisionLost() {
        var box = ValueBox.UInt64Face.From(ulong.MaxValue);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((double)ulong.MaxValue, value);
    }

    // ═══════════════════════ Get(out double) — TypeMismatch ═══════════════════════

    [Fact]
    public void GetDouble_FromNull_TypeMismatch() {
        var box = new ValueBox(0);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetDouble_FromBooleanTrue_TypeMismatch() {
        var box = new ValueBox(3);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetDouble_FromUndefined_TypeMismatch() {
        var box = new ValueBox(1);
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }
}
