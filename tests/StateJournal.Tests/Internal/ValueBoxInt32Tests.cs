using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Integer.cs`

/// <summary>
/// <see cref="ValueBox.Int32Face.From"/> 和 <see cref="ValueBox.Int32Face.Get"/> 的单元测试。
/// </summary>
/// <remarks>
/// int 值域 [−2^31, 2^31−1] 完全在 inline 容量 [−2^61, 2^62−1] 以内，始终 inline，无堆分配。
/// Get(out int) 委托给 Get(out long) 后做范围检查。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxInt32Tests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static int PoolCount => ValuePools.OfBits64.Count;

    private static void AssertRoundtrip(int expected) {
        var box = ValueBox.Int32Face.From(expected);
        GetIssue issue = ValueBox.Int32Face.Get(box, out int actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(expected, actual);
    }

    // ═══════════════════════ FromInt32 → Get(out int) Roundtrip ═══════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(42)]
    [InlineData(-42)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void FromInt32_Roundtrip(int value) => AssertRoundtrip(value);

    // ═══════════════════════ 始终 inline，无 Pool 分配 ═══════════════════════

    [Fact]
    public void FromInt32_MaxValue_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.Int32Face.From(int.MaxValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void FromInt32_MinValue_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.Int32Face.From(int.MinValue);
        Assert.Equal(before, PoolCount);
    }

    // ═══════════════════════ 同值同码 @[SAME-INLINE-SAME-VALUEBOX] ═══════════════════════

    [Fact]
    public void SameInlineSameValueBox_Int32_EqualsInt64() =>
        Assert.Equal(ValueBox.Int32Face.From(42).GetBits(), ValueBox.Int64Face.From(42).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int32Negative_EqualsInt64() =>
        Assert.Equal(ValueBox.Int32Face.From(-100).GetBits(), ValueBox.Int64Face.From(-100).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int32Max_EqualsInt64() =>
        Assert.Equal(ValueBox.Int32Face.From(int.MaxValue).GetBits(), ValueBox.Int64Face.From(int.MaxValue).GetBits());

    [Fact]
    public void SameInlineSameValueBox_Int32Min_EqualsInt64() =>
        Assert.Equal(ValueBox.Int32Face.From(int.MinValue).GetBits(), ValueBox.Int64Face.From(int.MinValue).GetBits());

    // ═══════════════════════ Get(out int) — Saturated（源值超出 int 范围）═══════════════════════

    [Fact]
    public void GetInt_FromLongMaxValue_Saturated() {
        var box = ValueBox.Int64Face.From(long.MaxValue);
        GetIssue issue = ValueBox.Int32Face.Get(box, out int value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(int.MaxValue, value);
    }

    [Fact]
    public void GetInt_FromLongMinValue_Saturated() {
        var box = ValueBox.Int64Face.From(long.MinValue);
        GetIssue issue = ValueBox.Int32Face.Get(box, out int value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(int.MinValue, value);
    }

    [Fact]
    public void GetInt_FromInlineJustAboveIntMax_Saturated() {
        var box = ValueBox.Int64Face.From((long)int.MaxValue + 1);
        GetIssue issue = ValueBox.Int32Face.Get(box, out int value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(int.MaxValue, value);
    }

    [Fact]
    public void GetInt_FromInlineJustBelowIntMin_Saturated() {
        var box = ValueBox.Int64Face.From((long)int.MinValue - 1);
        GetIssue issue = ValueBox.Int32Face.Get(box, out int value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(int.MinValue, value);
    }

    [Fact]
    public void GetInt_FromIntMaxValue_None() {
        var box = ValueBox.Int64Face.From(int.MaxValue);
        GetIssue issue = ValueBox.Int32Face.Get(box, out int value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(int.MaxValue, value);
    }

    [Fact]
    public void GetInt_FromIntMinValue_None() {
        var box = ValueBox.Int64Face.From(int.MinValue);
        GetIssue issue = ValueBox.Int32Face.Get(box, out int value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(int.MinValue, value);
    }

    // ═══════════════════════ Get(out int) — Saturated（ulong 源经 long 溢出级联）═══════════════════════

    [Fact]
    public void GetInt_FromUInt64Max_Saturated() {
        // ulong.MaxValue → Get(out long) 返回 Saturated/long.MaxValue → 再窄化到 int → Saturated/int.MaxValue
        var box = ValueBox.UInt64Face.From(ulong.MaxValue);
        GetIssue issue = ValueBox.Int32Face.Get(box, out int value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(int.MaxValue, value);
    }

    // ═══════════════════════ Get(out int) — TypeMismatch ═══════════════════════

    [Fact]
    public void GetInt_FromDouble_TypeMismatch() {
        var box = ValueBox.RoundedDoubleFace.From(3.14);
        GetIssue issue = ValueBox.Int32Face.Get(box, out int value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetInt_FromNull_TypeMismatch() {
        var box = new ValueBox(0);
        GetIssue issue = ValueBox.Int32Face.Get(box, out int value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }
}
