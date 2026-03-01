using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Integer.cs`

/// <summary>
/// <see cref="ValueBox.FromInt64"/> 和 <see cref="ValueBox.Get(out long)"/> 的单元测试。
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

    private static int PoolCount => ValuePools.Bits64.Count;

    private static void AssertRoundtrip(long expected) {
        var box = ValueBox.FromInt64(expected);
        GetIssue issue = box.Get(out long actual);
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
        var box = ValueBox.FromInt64(value);
        Assert.Equal(before, PoolCount); // 无 pool 分配
        GetIssue issue = box.Get(out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromInt64_MinHeapPositive_AllocatesPool() {
        long value = (long)LzcConstants.NonnegIntInlineCap; // 2^62，刚好溢出 inline
        int before = PoolCount;
        var box = ValueBox.FromInt64(value);
        Assert.Equal(before + 1, PoolCount); // 分配了 1 个 slot
        GetIssue issue = box.Get(out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromInt64_MinInlineNegative_IsInline() {
        long value = LzcConstants.NegIntInlineMin; // −2^61
        int before = PoolCount;
        var box = ValueBox.FromInt64(value);
        Assert.Equal(before, PoolCount);
        GetIssue issue = box.Get(out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    [Fact]
    public void FromInt64_MaxHeapNegative_AllocatesPool() {
        long value = LzcConstants.NegIntInlineMin - 1; // −2^61 − 1，刚好溢出 inline
        int before = PoolCount;
        var box = ValueBox.FromInt64(value);
        Assert.Equal(before + 1, PoolCount);
        GetIssue issue = box.Get(out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(value, actual);
    }

    // ═══════════════════════ 同值同码 @[SAME-INLINE-SAME-VALUEBOX] ═══════════════════════

    [Fact]
    public void SameInlineSameValueBox_Int64_EqualsInt32() =>
        Assert.Equal(ValueBox.FromInt64(42).GetBits(), ValueBox.FromInt32(42).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int64_EqualsInt16() =>
        Assert.Equal(ValueBox.FromInt64(100).GetBits(), ValueBox.FromInt16(100).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int64_EqualsSByte() =>
        Assert.Equal(ValueBox.FromInt64(-1).GetBits(), ValueBox.FromSByte(-1).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int64_EqualsByte() =>
        Assert.Equal(ValueBox.FromInt64(255).GetBits(), ValueBox.FromByte(255).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int64_EqualsUInt64() =>
        Assert.Equal(ValueBox.FromInt64(42).GetBits(), ValueBox.FromUInt64(42).GetBits());

    [Fact]
    public void SameInlineSameValueBox_NegativeInt64_EqualsNegativeInt32() =>
        Assert.Equal(ValueBox.FromInt64(-100).GetBits(), ValueBox.FromInt32(-100).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Zero() =>
        Assert.Equal(ValueBox.FromInt64(0).GetBits(), ValueBox.FromByte(0).GetBits());

    // ═══════════════════════ Get(out long) — Saturated ═══════════════════════

    [Fact]
    public void GetLong_FromUInt64MaxValue_Saturated() {
        var box = ValueBox.FromUInt64(ulong.MaxValue);
        GetIssue issue = box.Get(out long value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(long.MaxValue, value);
    }

    [Fact]
    public void GetLong_FromUInt64JustAboveLongMax_Saturated() {
        var box = ValueBox.FromUInt64((ulong)long.MaxValue + 1);
        GetIssue issue = box.Get(out long value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(long.MaxValue, value);
    }

    [Fact]
    public void GetLong_FromUInt64AtLongMax_None() {
        var box = ValueBox.FromUInt64((ulong)long.MaxValue);
        GetIssue issue = box.Get(out long value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(long.MaxValue, value);
    }

    // ═══════════════════════ Get(out long) — TypeMismatch ═══════════════════════

    [Fact]
    public void GetLong_FromDouble_TypeMismatch() {
        var box = ValueBox.FromRoundedDouble(3.14);
        GetIssue issue = box.Get(out long value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetLong_FromNull_TypeMismatch() {
        var box = new ValueBox(0); // Null
        GetIssue issue = box.Get(out long value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetLong_FromUndefined_TypeMismatch() {
        var box = new ValueBox(1); // Undefined
        GetIssue issue = box.Get(out long value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetLong_FromBooleanTrue_TypeMismatch() {
        var box = new ValueBox(3); // Boolean true
        GetIssue issue = box.Get(out long value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }
}
