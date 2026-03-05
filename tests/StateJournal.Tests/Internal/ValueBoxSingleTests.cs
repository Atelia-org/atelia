using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Float.cs`

/// <summary>
/// <see cref="ValueBox.SingleFace.From"/> 和 <see cref="ValueBox.SingleFace.Get"/> 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - FromSingle：始终 inline（float→double 拓宽 LSB 恒为 0），无堆分配。
/// - Get(out float) 从 double 源窄化：可能无损 (None)、精度损失 (PrecisionLost) 或溢出 (OverflowedToInfinity)。
/// - Get(out float) 从整数源隐式转换：24-bit significand 边界判定精度。
/// - Get(out float) 从 Half 源：经 inline double 中转，无损。
/// - 同值同码 @[SAME-INLINE-SAME-VALUEBOX]。
/// - 特殊值：NaN, ±Infinity。
/// - TypeMismatch。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxSingleTests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static int PoolCount => ValuePools.Bits64.Count;

    private static void AssertFloatBitsEqual(float expected, float actual) =>
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected), BitConverter.SingleToUInt32Bits(actual));

    // ═══════════════════════ FromSingle → Get(out float) Roundtrip ═══════════════════════

    [Theory]
    [InlineData(0.0f)]
    [InlineData(1.0f)]
    [InlineData(-1.0f)]
    [InlineData(0.5f)]
    [InlineData(3.14f)]
    [InlineData(float.MaxValue)]
    [InlineData(float.MinValue)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.Epsilon)]
    public void FromSingle_Roundtrip(float value) {
        var box = ValueBox.SingleFace.From(value);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float actual);
        Assert.Equal(GetIssue.None, issue);
        AssertFloatBitsEqual(value, actual);
    }

    [Fact]
    public void FromSingle_NaN_Roundtrip() {
        var box = ValueBox.SingleFace.From(float.NaN);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.True(float.IsNaN(actual));
    }

    [Fact]
    public void FromSingle_NegativeZero_PreservesBits() {
        float neg0 = BitConverter.UInt32BitsToSingle(0x8000_0000); // -0.0f
        var box = ValueBox.SingleFace.From(neg0);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float actual);
        Assert.Equal(GetIssue.None, issue);
        AssertFloatBitsEqual(neg0, actual);
    }

    // ═══════════════════════ FromSingle 始终 inline ═══════════════════════

    [Fact]
    public void FromSingle_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.SingleFace.From(float.MaxValue);
        _ = ValueBox.SingleFace.From(float.MinValue);
        _ = ValueBox.SingleFace.From(float.NaN);
        _ = ValueBox.SingleFace.From(float.PositiveInfinity);
        Assert.Equal(before, PoolCount);
    }

    // ═══════════════════════ 同值同码 @[SAME-INLINE-SAME-VALUEBOX] ═══════════════════════

    [Fact]
    public void SameInlineSameValueBox_Float_EqualsDouble() =>
        Assert.Equal(ValueBox.SingleFace.From(0.5f).GetBits(), ValueBox.RoundedDoubleFace.From(0.5).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Float1_EqualsHalf1() =>
        Assert.Equal(ValueBox.SingleFace.From(1.0f).GetBits(), ValueBox.HalfFace.From((Half)1.0).GetBits());

    // ═══════════════════════ Get(out float) — 从 Half 源（经 inline double 中转）═══════════════════════

    [Fact]
    public void GetFloat_FromHalf_ReturnsWidenedFloat() {
        Half h = (Half)2.5;
        var box = ValueBox.HalfFace.From(h);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((float)(double)h, value);
    }

    // ═══════════════════════ Get(out float) — 从 inline double 源窄化 ═══════════════════════

    [Fact]
    public void GetFloat_FromDouble_ExactNarrowing() {
        // 1.5 能精确表示为 float
        var box = ValueBox.RoundedDoubleFace.From(1.5);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(1.5f, value);
    }

    [Fact]
    public void GetFloat_FromDouble_PrecisionLost() {
        // 0.1 (double) 不能精确表示为 float
        var box = ValueBox.RoundedDoubleFace.From(0.1);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((float)0.1, value);
    }

    [Fact]
    public void GetFloat_FromDouble_OverflowedToInfinity() {
        // 1e+300 → (float) → +Infinity
        var box = ValueBox.RoundedDoubleFace.From(1.0e+300);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.OverflowedToInfinity, issue);
        Assert.Equal(float.PositiveInfinity, value);
    }

    [Fact]
    public void GetFloat_FromNegativeDouble_OverflowedToInfinity() {
        var box = ValueBox.RoundedDoubleFace.From(-1.0e+300);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.OverflowedToInfinity, issue);
        Assert.Equal(float.NegativeInfinity, value);
    }

    [Fact]
    public void GetFloat_FromDoubleInfinity_None_NotOverflow() {
        // double ±Infinity → float ±Infinity 不算"溢出到 Infinity"
        var box = ValueBox.RoundedDoubleFace.From(double.PositiveInfinity);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(float.PositiveInfinity, value);
    }

    [Fact]
    public void GetFloat_FromDoubleNaN_None() {
        var box = ValueBox.RoundedDoubleFace.From(double.NaN);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.None, issue);
        Assert.True(float.IsNaN(value));
    }

    // ═══════════════════════ Get(out float) — 从堆 double 源窄化 ═══════════════════════

    [Fact]
    public void GetFloat_FromExactHeapDouble_PrecisionLost() {
        // 0x3FF0_0000_0000_0001 ≈ 1.0（LSB=1 → heap），窄化到 float 丢失精度
        double d = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = ValueBox.ExactDoubleFace.From(d);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal(1.0f, value);
    }

    [Fact]
    public void GetFloat_FromExactHeapDouble_OverflowedToInfinity() {
        // double.MaxValue（LSB=1 → heap），窄化到 float 溢出
        var box = ValueBox.ExactDoubleFace.From(double.MaxValue);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.OverflowedToInfinity, issue);
        Assert.Equal(float.PositiveInfinity, value);
    }

    // ═══════════════════════ Get(out float) — 从整数源：精确转换 ═══════════════════════

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(42L)]
    public void GetFloat_FromSmallInt_Exact(long intValue) {
        var box = ValueBox.Int64Face.From(intValue);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((float)intValue, value);
    }

    [Fact]
    public void GetFloat_FromInlineInt_24BitBoundary_Exact() {
        // 2^24 − 1 = 16777215：24 个有效位，恰好在 float 精度内
        long v = (1L << 24) - 1;
        var box = ValueBox.Int64Face.From(v);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((float)v, value);
    }

    // ═══════════════════════ Get(out float) — 从整数源：PrecisionLost ═══════════════════════

    [Fact]
    public void GetFloat_FromInlineInt_25Bit_PrecisionLost() {
        // 2^24 + 1 = 16777217：25 个有效位，超出 float 24-bit significand
        long v = (1L << 24) + 1;
        var box = ValueBox.Int64Face.From(v);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((float)v, value);
    }

    [Fact]
    public void GetFloat_FromInlineNegInt_PrecisionLost() {
        long v = -((1L << 24) + 1);
        var box = ValueBox.Int64Face.From(v);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((float)v, value);
    }

    // ═══════════════════════ Get(out float) — 从堆整数源 ═══════════════════════

    [Fact]
    public void GetFloat_FromHeapNonnegInt_PowerOf2_Exact() {
        // 2^62 是 2 的幂 → 精确表示为 float；超出 inline → heap
        var box = ValueBox.Int64Face.From((long)LzcConstants.NonnegIntInlineCap);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((float)(long)LzcConstants.NonnegIntInlineCap, value);
    }

    [Fact]
    public void GetFloat_FromHeapUInt64Max_PrecisionLost() {
        var box = ValueBox.UInt64Face.From(ulong.MaxValue);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((float)ulong.MaxValue, value);
    }

    [Fact]
    public void GetFloat_FromHeapNegInt_LongMinValue_Exact() {
        // long.MinValue = −2^63：2 的幂 → exact as float
        // 验证 (ulong)(-long.MinValue) 溢出但仍给出正确 magnitude
        var box = ValueBox.Int64Face.From(long.MinValue);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((float)long.MinValue, value);
    }

    [Fact]
    public void GetFloat_FromHeapNegInt_NotPowerOf2_PrecisionLost() {
        // long.MinValue + 1 = −(2^63 − 1)：63 个有效位 → PrecisionLost
        var box = ValueBox.Int64Face.From(long.MinValue + 1);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((float)(long.MinValue + 1), value);
    }

    // ═══════════════════════ Get(out float) — TypeMismatch ═══════════════════════

    [Fact]
    public void GetFloat_FromNull_TypeMismatch() {
        var box = new ValueBox(0);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetFloat_FromBooleanFalse_TypeMismatch() {
        var box = new ValueBox(2);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetFloat_FromUndefined_TypeMismatch() {
        var box = new ValueBox(1);
        GetIssue issue = ValueBox.SingleFace.Get(box, out float value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }
}
