using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Integer.cs`

/// <summary>
/// <see cref="ValueBox.FromUInt64"/> 和 <see cref="ValueBox.Get(out ulong)"/> 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - Roundtrip：From → Get 往返正确性。
/// - Inline/Heap 边界：验证 LZC 编码在 inline 容量边界处的正确分派。
/// - Pool 行为：inline 不分配 slot；heap 分配 slot。
/// - 同值同码 @[SAME-INLINE-SAME-VALUEBOX]。
/// - Saturated：负整数源读取 ulong 时饱和到 ulong.MinValue。
/// - TypeMismatch：非整数源返回 TypeMismatch。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxUInt64Tests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static int PoolCount => ValuePools.Bits64.Count;

    private static void AssertRoundtrip(ulong expected) {
        var box = ValueBox.FromUInt64(expected);
        GetIssue issue = box.Get(out ulong actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(expected, actual);
    }

    // ═══════════════════════ FromUInt64 → Get(out ulong) Roundtrip ═══════════════════════

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(ulong.MaxValue)]
    public void FromUInt64_Roundtrip(ulong value) => AssertRoundtrip(value);

    [Fact]
    public void FromUInt64_LongMaxValue_Roundtrip() => AssertRoundtrip((ulong)long.MaxValue);

    [Fact]
    public void FromUInt64_LongMaxValuePlus1_Roundtrip() => AssertRoundtrip((ulong)long.MaxValue + 1);

    // ═══════════════════════ Inline 边界 ═══════════════════════

    [Fact]
    public void FromUInt64_MaxInline_IsInline() {
        ulong value = LzcConstants.NonnegIntInlineCap - 1; // 2^62 − 1
        int before = PoolCount;
        var box = ValueBox.FromUInt64(value);
        Assert.Equal(before, PoolCount);
        GetIssue issue = box.Get(out ulong actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromUInt64_MinHeap_AllocatesPool() {
        ulong value = LzcConstants.NonnegIntInlineCap; // 2^62
        int before = PoolCount;
        var box = ValueBox.FromUInt64(value);
        Assert.Equal(before + 1, PoolCount);
        GetIssue issue = box.Get(out ulong actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    // ═══════════════════════ 同值同码 @[SAME-INLINE-SAME-VALUEBOX] ═══════════════════════

    [Fact]
    public void SameInlineSameValueBox_UInt64_EqualsInt64() =>
        Assert.Equal(ValueBox.FromUInt64(42).GetBits(), ValueBox.FromInt64(42).GetBits());

    [Fact]
    public void SameInlineSameValueBox_UInt64_EqualsUInt32() =>
        Assert.Equal(ValueBox.FromUInt64(1000).GetBits(), ValueBox.FromUInt32(1000).GetBits());

    [Fact]
    public void SameInlineSameValueBox_UInt64_EqualsUInt16() =>
        Assert.Equal(ValueBox.FromUInt64(60000).GetBits(), ValueBox.FromUInt16(60000).GetBits());

    [Fact]
    public void SameInlineSameValueBox_UInt64_EqualsByte() =>
        Assert.Equal(ValueBox.FromUInt64(200).GetBits(), ValueBox.FromByte(200).GetBits());

    // ═══════════════════════ Get(out ulong) — 从正整数长路径 ═══════════════════════

    [Fact]
    public void GetULong_FromInt64Positive_None() {
        var box = ValueBox.FromInt64(42);
        GetIssue issue = box.Get(out ulong value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(42UL, value);
    }

    [Fact]
    public void GetULong_FromInt64MaxInlinePositive_None() {
        long src = (long)LzcConstants.NonnegIntInlineCap - 1;
        var box = ValueBox.FromInt64(src);
        GetIssue issue = box.Get(out ulong value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((ulong)src, value);
    }

    [Fact]
    public void GetULong_FromInt64HeapPositive_None() {
        var box = ValueBox.FromInt64(long.MaxValue);
        GetIssue issue = box.Get(out ulong value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal((ulong)long.MaxValue, value);
    }

    // ═══════════════════════ Get(out ulong) — Saturated（负整数源）═══════════════════════

    [Fact]
    public void GetULong_FromInlineNegInt_Saturated() {
        var box = ValueBox.FromInt64(-1);
        GetIssue issue = box.Get(out ulong value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(ulong.MinValue, value);
    }

    [Fact]
    public void GetULong_FromInlineMinNeg_Saturated() {
        var box = ValueBox.FromInt64(LzcConstants.NegIntInlineMin); // −2^61
        GetIssue issue = box.Get(out ulong value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(ulong.MinValue, value);
    }

    [Fact]
    public void GetULong_FromHeapNegInt_Saturated() {
        var box = ValueBox.FromInt64(long.MinValue); // heap negative
        GetIssue issue = box.Get(out ulong value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(ulong.MinValue, value);
    }

    // ═══════════════════════ Get(out ulong) — TypeMismatch ═══════════════════════

    [Fact]
    public void GetULong_FromDouble_TypeMismatch() {
        var box = ValueBox.FromRoundedDouble(3.14);
        GetIssue issue = box.Get(out ulong value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetULong_FromNull_TypeMismatch() {
        var box = new ValueBox(0); // Null
        GetIssue issue = box.Get(out ulong value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetULong_FromBooleanFalse_TypeMismatch() {
        var box = new ValueBox(2); // Boolean false
        GetIssue issue = box.Get(out ulong value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }
}
