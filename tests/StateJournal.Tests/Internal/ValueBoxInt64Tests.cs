using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Integer.cs`

/// <summary>
/// <see cref="ValueBox.Int64Face.From"/> 和 <see cref="ValueBox.Int64Face.Get"/> 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - Roundtrip：From → Get 往返正确性。
/// - Inline/Heap 边界：验证 LZC 编码在 inline 容量边界处的正确分派。
/// - Pool 行为：inline 不分配 slot；heap 分配 slot。
/// - 同值同码 @[SAME-INLINE-SAME-VALUEBOX]：跨整数类型 inline 编码应产生相同 bits。
/// - Saturated / TypeMismatch：Get(out long) 在源值溢出或类型不匹配时的行为。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxInt64Tests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static int PoolCount => ValuePools.OfBits64.Count;

    private static void AssertRoundtrip(long expected) {
        var box = ValueBox.Int64Face.From(expected);
        GetIssue issue = ValueBox.Int64Face.Get(box, out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(expected, actual);
    }

    // ═══════════════════════ FromInt64 → Get(out long) Roundtrip ═══════════════════════

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(42L)]
    [InlineData(-42L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void FromInt64_Roundtrip(long value) => AssertRoundtrip(value);

    // ═══════════════════════ Inline 边界 ═══════════════════════

    [Fact]
    public void FromInt64_MaxInlinePositive_IsInline() {
        long value = (long)LzcConstants.NonnegIntInlineCap - 1; // 2^62 − 1
        int before = PoolCount;
        var box = ValueBox.Int64Face.From(value);
        Assert.Equal(before, PoolCount); // 无 pool 分配
        GetIssue issue = ValueBox.Int64Face.Get(box, out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromInt64_MinHeapPositive_AllocatesPool() {
        long value = (long)LzcConstants.NonnegIntInlineCap; // 2^62，刚好溢出 inline
        int before = PoolCount;
        var box = ValueBox.Int64Face.From(value);
        Assert.Equal(before + 1, PoolCount); // 分配了 1 个 slot
        GetIssue issue = ValueBox.Int64Face.Get(box, out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromInt64_MinInlineNegative_IsInline() {
        long value = LzcConstants.NegIntInlineMin; // −2^61
        int before = PoolCount;
        var box = ValueBox.Int64Face.From(value);
        Assert.Equal(before, PoolCount);
        GetIssue issue = ValueBox.Int64Face.Get(box, out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromInt64_MaxHeapNegative_AllocatesPool() {
        long value = LzcConstants.NegIntInlineMin - 1; // −2^61 − 1，刚好溢出 inline
        int before = PoolCount;
        var box = ValueBox.Int64Face.From(value);
        Assert.Equal(before + 1, PoolCount);
        GetIssue issue = ValueBox.Int64Face.Get(box, out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    // ═══════════════════════ 同值同码 @[SAME-INLINE-SAME-VALUEBOX] ═══════════════════════

    [Fact]
    public void SameInlineSameValueBox_Int64_EqualsInt32() =>
        Assert.Equal(ValueBox.Int64Face.From(42).GetBits(), ValueBox.Int32Face.From(42).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int64_EqualsInt16() =>
        Assert.Equal(ValueBox.Int64Face.From(100).GetBits(), ValueBox.Int16Face.From(100).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int64_EqualsSByte() =>
        Assert.Equal(ValueBox.Int64Face.From(-1).GetBits(), ValueBox.SByteFace.From(-1).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int64_EqualsByte() =>
        Assert.Equal(ValueBox.Int64Face.From(255).GetBits(), ValueBox.ByteFace.From(255).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int64_EqualsUInt64() =>
        Assert.Equal(ValueBox.Int64Face.From(42).GetBits(), ValueBox.UInt64Face.From(42).GetBits());

    [Fact]
    public void SameInlineSameValueBox_NegativeInt64_EqualsNegativeInt32() =>
        Assert.Equal(ValueBox.Int64Face.From(-100).GetBits(), ValueBox.Int32Face.From(-100).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Zero() =>
        Assert.Equal(ValueBox.Int64Face.From(0).GetBits(), ValueBox.ByteFace.From(0).GetBits());

    // ═══════════════════════ Get(out long) — Saturated ═══════════════════════

    [Fact]
    public void GetLong_FromUInt64MaxValue_Saturated() {
        var box = ValueBox.UInt64Face.From(ulong.MaxValue);
        GetIssue issue = ValueBox.Int64Face.Get(box, out long value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(long.MaxValue, value);
    }

    [Fact]
    public void GetLong_FromUInt64JustAboveLongMax_Saturated() {
        var box = ValueBox.UInt64Face.From((ulong)long.MaxValue + 1);
        GetIssue issue = ValueBox.Int64Face.Get(box, out long value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(long.MaxValue, value);
    }

    [Fact]
    public void GetLong_FromUInt64AtLongMax_None() {
        var box = ValueBox.UInt64Face.From((ulong)long.MaxValue);
        GetIssue issue = ValueBox.Int64Face.Get(box, out long value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(long.MaxValue, value);
    }

    // ═══════════════════════ Get(out long) — TypeMismatch ═══════════════════════

    [Fact]
    public void GetLong_FromDouble_TypeMismatch() {
        var box = ValueBox.RoundedDoubleFace.From(3.14);
        GetIssue issue = ValueBox.Int64Face.Get(box, out long value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetLong_FromNull_TypeMismatch() {
        var box = ValueBox.Null;
        GetIssue issue = ValueBox.Int64Face.Get(box, out long value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetLong_FromBooleanTrue_TypeMismatch() {
        var box = new ValueBox(3); // Boolean true
        GetIssue issue = ValueBox.Int64Face.Get(box, out long value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }
}
