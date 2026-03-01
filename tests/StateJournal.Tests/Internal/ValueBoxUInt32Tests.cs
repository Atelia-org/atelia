using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Integer.cs`

/// <summary>
/// <see cref="ValueBox.FromUInt32"/> 和 <see cref="ValueBox.Get(out uint)"/> 的单元测试。
/// </summary>
/// <remarks>
/// uint 值域 [0, 2^32−1] 完全在 inline 容量 [0, 2^62−1] 以内，始终 inline，无堆分配。
/// Get(out uint) 委托给 Get(out ulong) 后做范围检查。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxUInt32Tests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static int PoolCount => ValuePools.Bits64.Count;

    private static void AssertRoundtrip(uint expected) {
        var box = ValueBox.FromUInt32(expected);
        GetIssue issue = box.Get(out uint actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(expected, actual);
    }

    // ═══════════════════════ FromUInt32 → Get(out uint) Roundtrip ═══════════════════════

    [Theory]
    [InlineData(0U)]
    [InlineData(1U)]
    [InlineData(42U)]
    [InlineData(uint.MaxValue)]
    public void FromUInt32_Roundtrip(uint value) => AssertRoundtrip(value);

    // ═══════════════════════ 始终 inline，无 Pool 分配 ═══════════════════════

    [Fact]
    public void FromUInt32_MaxValue_NoPoolAllocation() {
        int before = PoolCount;
        _ = ValueBox.FromUInt32(uint.MaxValue);
        Assert.Equal(before, PoolCount);
    }

    // ═══════════════════════ 同值同码 @[SAME-INLINE-SAME-VALUEBOX] ═══════════════════════

    [Fact]
    public void SameInlineSameValueBox_UInt32_EqualsUInt64() =>
        Assert.Equal(ValueBox.FromUInt32(1000U).GetBits(), ValueBox.FromUInt64(1000UL).GetBits());

    [Fact]
    public void SameInlineSameValueBox_UInt32_EqualsInt64() =>
        Assert.Equal(ValueBox.FromUInt32(42U).GetBits(), ValueBox.FromInt64(42L).GetBits());

    [Fact]
    public void SameInlineSameValueBox_UInt32Max_EqualsUInt64() =>
        Assert.Equal(ValueBox.FromUInt32(uint.MaxValue).GetBits(), ValueBox.FromUInt64(uint.MaxValue).GetBits());

    // ═══════════════════════ Get(out uint) — Saturated（源值超出 uint 范围）═══════════════════════

    [Fact]
    public void GetUInt_FromUInt64Max_Saturated() {
        var box = ValueBox.FromUInt64(ulong.MaxValue);
        GetIssue issue = box.Get(out uint value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(uint.MaxValue, value);
    }

    [Fact]
    public void GetUInt_FromJustAboveUIntMax_Saturated() {
        var box = ValueBox.FromUInt64((ulong)uint.MaxValue + 1);
        GetIssue issue = box.Get(out uint value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(uint.MaxValue, value);
    }

    [Fact]
    public void GetUInt_FromUIntMaxValue_None() {
        var box = ValueBox.FromUInt64(uint.MaxValue);
        GetIssue issue = box.Get(out uint value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(uint.MaxValue, value);
    }

    [Fact]
    public void GetUInt_FromNegativeInt_Saturated() {
        // 负整数 → Get(out ulong) 返回 Saturated/0 → Get(out uint) 返回 Saturated/0
        var box = ValueBox.FromInt64(-1);
        GetIssue issue = box.Get(out uint value);
        Assert.Equal(GetIssue.Saturated, issue);
        Assert.Equal(uint.MinValue, value); // 饱和到 0
    }

    // ═══════════════════════ Get(out uint) — TypeMismatch ═══════════════════════

    [Fact]
    public void GetUInt_FromDouble_TypeMismatch() {
        var box = ValueBox.FromRoundedDouble(3.14);
        GetIssue issue = box.Get(out uint value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetUInt_FromNull_TypeMismatch() {
        var box = new ValueBox(0);
        GetIssue issue = box.Get(out uint value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }
}
