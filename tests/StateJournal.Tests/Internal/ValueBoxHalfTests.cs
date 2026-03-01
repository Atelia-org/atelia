using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Float.cs`

/// <summary>
/// <see cref="ValueBox.FromHalf"/> 和 <see cref="ValueBox.Get(out Half)"/> 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - FromHalf：始终 inline（Half→double 拓宽 LSB 恒为 0），无堆分配。
/// - Get(out Half) 从 double 源窄化：可能无损、精度损失或溢出到 Infinity。
/// - Get(out Half) 从整数源隐式转换：
///   - inline 整数 ≤ 65504(Half.MaxValue) → exact 或 PrecisionLost（11-bit significand 边界）；
///   - inline 整数 &gt; 65504 → OverflowedToInfinity；
///   - heap 整数 ≥ 2^62 → 始终 OverflowedToInfinity（不读堆，直接判定）。
/// - 同值同码 @[SAME-INLINE-SAME-VALUEBOX]。
/// - TypeMismatch。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxHalfTests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static int PoolCount => ValuePools.Bits64.Count;

    private static void AssertHalfBitsEqual(Half expected, Half actual) =>
        Assert.Equal(BitConverter.HalfToUInt16Bits(expected), BitConverter.HalfToUInt16Bits(actual));

    private static void AssertHalfRoundtrip(Half expected) {
        var box = ValueBox.FromHalf(expected);
        GetIssue issue = box.Get(out Half actual);
        Assert.Equal(GetIssue.None, issue);
        AssertHalfBitsEqual(expected, actual);
    }

    // ═══════════════════════ FromHalf → Get(out Half) Roundtrip ═══════════════════════
    // Half 不能用 [InlineData]，使用 [Fact] 逐项测试。

    [Fact] public void FromHalf_Zero_Roundtrip() => AssertHalfRoundtrip((Half)0.0);
    [Fact] public void FromHalf_One_Roundtrip() => AssertHalfRoundtrip((Half)1.0);
    [Fact] public void FromHalf_NegOne_Roundtrip() => AssertHalfRoundtrip((Half)(-1.0));
    [Fact] public void FromHalf_Half05_Roundtrip() => AssertHalfRoundtrip((Half)0.5);
    [Fact] public void FromHalf_MaxValue_Roundtrip() => AssertHalfRoundtrip(Half.MaxValue);
    [Fact] public void FromHalf_MinValue_Roundtrip() => AssertHalfRoundtrip(Half.MinValue);
    [Fact] public void FromHalf_PosInf_Roundtrip() => AssertHalfRoundtrip(Half.PositiveInfinity);
    [Fact] public void FromHalf_NegInf_Roundtrip() => AssertHalfRoundtrip(Half.NegativeInfinity);
    [Fact] public void FromHalf_Epsilon_Roundtrip() => AssertHalfRoundtrip(Half.Epsilon);

    [Fact]
    public void FromHalf_NaN_Roundtrip() {
        var box = ValueBox.FromHalf(Half.NaN);
        GetIssue issue = box.Get(out Half actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.True(Half.IsNaN(actual));
    }

    // ═══════════════════════ FromHalf 始终 inline ═══════════════════════

    [Fact]
    public void FromHalf_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.FromHalf(Half.MaxValue);
        _ = ValueBox.FromHalf(Half.MinValue);
        _ = ValueBox.FromHalf(Half.NaN);
        _ = ValueBox.FromHalf(Half.PositiveInfinity);
        Assert.Equal(before, PoolCount);
    }

    // ═══════════════════════ 同值同码 @[SAME-INLINE-SAME-VALUEBOX] ═══════════════════════

    [Fact]
    public void SameInlineSameValueBox_Half_EqualsDouble() =>
        Assert.Equal(ValueBox.FromHalf((Half)0.5).GetBits(), ValueBox.FromRoundedDouble(0.5).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Half_EqualsFloat() =>
        Assert.Equal(ValueBox.FromHalf((Half)1.0).GetBits(), ValueBox.FromSingle(1.0f).GetBits());

    // ═══════════════════════ Get(out Half) — 从 inline double 源窄化 ═══════════════════════

    [Fact]
    public void GetHalf_FromDouble_ExactNarrowing() {
        // 1.5 能精确表示为 Half
        var box = ValueBox.FromRoundedDouble(1.5);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((Half)1.5, value);
    }

    [Fact]
    public void GetHalf_FromDouble_PrecisionLost() {
        // 0.1 不能精确表示为 Half
        var box = ValueBox.FromRoundedDouble(0.1);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((Half)0.1, value);
    }

    [Fact]
    public void GetHalf_FromDouble_OverflowedToInfinity() {
        // 100000.0 > Half.MaxValue (65504) → Half → PositiveInfinity
        var box = ValueBox.FromRoundedDouble(100000.0);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.OverflowedToInfinity, issue);
        Assert.Equal(Half.PositiveInfinity, value);
    }

    [Fact]
    public void GetHalf_FromNegativeDouble_OverflowedToInfinity() {
        var box = ValueBox.FromRoundedDouble(-100000.0);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.OverflowedToInfinity, issue);
        Assert.Equal(Half.NegativeInfinity, value);
    }

    [Fact]
    public void GetHalf_FromDoubleInfinity_None_NotOverflow() {
        // double ±Infinity → Half ±Infinity 不算"溢出到 Infinity"
        var box = ValueBox.FromRoundedDouble(double.PositiveInfinity);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(Half.PositiveInfinity, value);
    }

    [Fact]
    public void GetHalf_FromDoubleNaN_None() {
        var box = ValueBox.FromRoundedDouble(double.NaN);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.None, issue);
        Assert.True(Half.IsNaN(value));
    }

    // ═══════════════════════ Get(out Half) — 从堆 double 源窄化 ═══════════════════════

    [Fact]
    public void GetHalf_FromExactHeapDouble_PrecisionLost() {
        // 0x3FF0_0000_0000_0001 ≈ 1.0（LSB=1 → heap），窄化到 Half 丢精度
        double d = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = ValueBox.FromExactDouble(d);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((Half)1.0, value);
    }

    [Fact]
    public void GetHalf_FromExactHeapDouble_OverflowedToInfinity() {
        // double.MaxValue（LSB=1 → heap），窄化到 Half 溢出
        var box = ValueBox.FromExactDouble(double.MaxValue);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.OverflowedToInfinity, issue);
        Assert.Equal(Half.PositiveInfinity, value);
    }

    // ═══════════════════════ Get(out Half) — 从 inline 整数源：精确转换 ═══════════════════════

    [Fact]
    public void GetHalf_FromSmallInt_Exact() {
        var box = ValueBox.FromInt32(42);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((Half)42, value);
    }

    [Fact]
    public void GetHalf_FromInlineInt_11BitBoundary_Exact() {
        // 2^11 − 1 = 2047：11 个有效位，恰好在 Half 精度内
        var box = ValueBox.FromInt32(2047);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((Half)2047, value);
    }

    [Fact]
    public void GetHalf_FromInlineInt_PowerOf2_Exact() {
        // 1024 = 2^10：1 个有效位 → exact
        var box = ValueBox.FromInt32(1024);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((Half)1024, value);
    }

    // ═══════════════════════ Get(out Half) — 从 inline 整数源：PrecisionLost ═══════════════════════

    [Fact]
    public void GetHalf_FromInlineInt_12Bit_PrecisionLost() {
        // 2^11 + 1 = 2049：12 个有效位，超出 Half 11-bit significand
        var box = ValueBox.FromInt32(2049);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((Half)2049, value);
    }

    [Fact]
    public void GetHalf_FromInlineNegInt_PrecisionLost() {
        var box = ValueBox.FromInt32(-2049);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.PrecisionLost, issue);
        Assert.Equal((Half)(-2049), value);
    }

    // ═══════════════════════ Get(out Half) — 从 inline 整数源：OverflowedToInfinity ═══════════════════════

    [Fact]
    public void GetHalf_FromInlineInt_BeyondHalfMax_OverflowedToInfinity() {
        // 100000 > 65504 (Half.MaxValue) → (Half)100000 = +Infinity
        var box = ValueBox.FromInt32(100000);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.OverflowedToInfinity, issue);
        Assert.Equal(Half.PositiveInfinity, value);
    }

    [Fact]
    public void GetHalf_FromInlineNegInt_BeyondHalfMin_OverflowedToInfinity() {
        var box = ValueBox.FromInt32(-100000);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.OverflowedToInfinity, issue);
        Assert.Equal(Half.NegativeInfinity, value);
    }

    // ═══════════════════════ Get(out Half) — 从堆整数源（始终 OverflowedToInfinity）═══════════════════════

    [Fact]
    public void GetHalf_FromHeapNonnegInt_AlwaysOverflow() {
        // heap 正整数 ≥ 2^62，远超 Half.MaxValue (65504)，代码直接返回不读堆
        var box = ValueBox.FromInt64((long)LzcConstants.NonnegIntInlineCap);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.OverflowedToInfinity, issue);
        Assert.Equal(Half.PositiveInfinity, value);
    }

    [Fact]
    public void GetHalf_FromHeapNegInt_AlwaysOverflow() {
        var box = ValueBox.FromInt64(long.MinValue);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.OverflowedToInfinity, issue);
        Assert.Equal(Half.NegativeInfinity, value);
    }

    // ═══════════════════════ Get(out Half) — TypeMismatch ═══════════════════════

    [Fact]
    public void GetHalf_FromNull_TypeMismatch() {
        var box = new ValueBox(0);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetHalf_FromBooleanTrue_TypeMismatch() {
        var box = new ValueBox(3);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetHalf_FromUndefined_TypeMismatch() {
        var box = new ValueBox(1);
        GetIssue issue = box.Get(out Half value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }
}
