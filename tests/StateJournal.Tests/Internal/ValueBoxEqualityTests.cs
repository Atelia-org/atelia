using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Equality.cs`

/// <summary>
/// <see cref="ValueBox.ValueEquals"/>、<see cref="ValueBox.ValueHashCode"/>
/// 和 <see cref="ValueBox.EqualityComparer"/> 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - 快速路径：bits 相同 → true（inline 值、布尔、null、undefined）。
/// - 慢路径：heap 数值同值同 Kind → true（GcPool 独占 slot，同值不同 handle）。
/// - 跨 Kind 不等：整数 ≠ 浮点（42 ≠ 42.0 的语义决策）。
/// - HashCode 一致性：ValueEquals 为 true → 同 HashCode。
/// - EqualityComparer 单例委托。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxEqualityTests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static ValueBox Null => new(0);
    private static ValueBox Undefined => new(1);
    private static ValueBox BoolFalse => new(2);
    private static ValueBox BoolTrue => new(3);

    // ═══════════════════════ ValueEquals 快速路径 — bits 相等 → true ═══════════════════════

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(42L)]
    [InlineData(-1L)]
    [InlineData(-42L)]
    public void ValueEquals_SameInlineInt_True(long value) {
        var a = ValueBox.FromInt64(value);
        var b = ValueBox.FromInt64(value);
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_FromInt64_42_And_FromInt32_42_SameBits_True() {
        // @[SAME-INLINE-SAME-VALUEBOX]: 同值 inline 整数跨类型产生相同 bits
        var a = ValueBox.FromInt64(42);
        var b = ValueBox.FromInt32(42);
        Assert.Equal(a.GetBits(), b.GetBits()); // 前提：bits 确实相同
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_Null_SelfEquals() {
        Assert.True(ValueBox.ValueEquals(Null, Null));
    }

    [Fact]
    public void ValueEquals_Undefined_SelfEquals() {
        Assert.True(ValueBox.ValueEquals(Undefined, Undefined));
    }

    [Fact]
    public void ValueEquals_BoolTrue_SelfEquals() {
        Assert.True(ValueBox.ValueEquals(BoolTrue, BoolTrue));
    }

    [Fact]
    public void ValueEquals_BoolFalse_SelfEquals() {
        Assert.True(ValueBox.ValueEquals(BoolFalse, BoolFalse));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(3.14)]
    [InlineData(double.PositiveInfinity)]
    public void ValueEquals_SameRoundedDouble_True(double value) {
        var a = ValueBox.FromRoundedDouble(value);
        var b = ValueBox.FromRoundedDouble(value);
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    // ═══════════════════════ ValueEquals 慢路径 — heap 数值同值同 Kind → true ═══════════════════════

    [Fact]
    public void ValueEquals_TwoHeapUInt64Max_True() {
        // ulong.MaxValue > 2^62-1 → 走 heap，两次独立分配 → 不同 handle → 慢路径
        var a = ValueBox.FromUInt64(ulong.MaxValue);
        var b = ValueBox.FromUInt64(ulong.MaxValue);
        Assert.NotEqual(a.GetBits(), b.GetBits()); // 不同 handle → 不同 bits
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_TwoHeapInt64Min_True() {
        // long.MinValue = -2^63，超出 inline 范围 [-2^61, 2^62-1] → 走 heap
        var a = ValueBox.FromInt64(long.MinValue);
        var b = ValueBox.FromInt64(long.MinValue);
        Assert.NotEqual(a.GetBits(), b.GetBits()); // 不同 handle
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_TwoHeapExactDoubleMax_True() {
        // double.MaxValue = 0x7FEF_FFFF_FFFF_FFFF，LSB=1 → FromExactDouble 走 heap
        var a = ValueBox.FromExactDouble(double.MaxValue);
        var b = ValueBox.FromExactDouble(double.MaxValue);
        Assert.NotEqual(a.GetBits(), b.GetBits()); // 不同 handle
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    // ═══════════════════════ ValueEquals — heap 数值不同 Kind → false ═══════════════════════

    [Fact]
    public void ValueEquals_InlineInt42_vs_InlineDouble42_False() {
        // 设计决策：42 != 42.0，整数与浮点不互等
        // 42 → inline int，42.0 → inline double → bits 不同 → 快速路径 false
        var intBox = ValueBox.FromInt64(42);
        var dblBox = ValueBox.FromRoundedDouble(42.0);
        Assert.False(ValueBox.ValueEquals(intBox, dblBox));
    }

    [Fact]
    public void ValueEquals_HeapNonnegInt_vs_HeapFloat_False() {
        // 两个都走 heap Bits64，但 Kind 分别是 NonnegativeInteger 和 FloatingPoint → false
        var intBox = ValueBox.FromUInt64(ulong.MaxValue); // NonnegativeInteger heap
        var dblBox = ValueBox.FromExactDouble(double.MaxValue); // FloatingPoint heap (LSB=1)
        Assert.False(ValueBox.ValueEquals(intBox, dblBox));
    }

    // ═══════════════════════ ValueEquals — heap 数值不同值同 Kind → false ═══════════════════════

    [Fact]
    public void ValueEquals_HeapUInt64Max_vs_HeapUInt64MaxMinus1_False() {
        var a = ValueBox.FromUInt64(ulong.MaxValue);
        var b = ValueBox.FromUInt64(ulong.MaxValue - 1);
        Assert.False(ValueBox.ValueEquals(a, b));
    }

    // ═══════════════════════ ValueEquals — 非 heap 值不等 → false ═══════════════════════

    [Fact]
    public void ValueEquals_InlineInt1_vs_InlineInt2_False() {
        Assert.False(ValueBox.ValueEquals(ValueBox.FromInt64(1), ValueBox.FromInt64(2)));
    }

    [Fact]
    public void ValueEquals_InlineDouble1_vs_InlineDouble2_False() {
        Assert.False(ValueBox.ValueEquals(ValueBox.FromRoundedDouble(1.0), ValueBox.FromRoundedDouble(2.0)));
    }

    [Fact]
    public void ValueEquals_InlineInt0_vs_Null_False() {
        // FromInt64(0) 编码为 inline 非负整数（LZC=1），new ValueBox(0) 是 Null（LZC=64）
        var intZero = ValueBox.FromInt64(0);
        Assert.False(ValueBox.ValueEquals(intZero, Null));
    }

    // ═══════════════════════ ValueEquals — 跨类型快速 false ═══════════════════════

    [Fact]
    public void ValueEquals_InlineInt_vs_InlineDouble_False() {
        Assert.False(ValueBox.ValueEquals(ValueBox.FromInt64(1), ValueBox.FromRoundedDouble(1.0)));
    }

    [Fact]
    public void ValueEquals_Null_vs_Boolean_False() {
        Assert.False(ValueBox.ValueEquals(Null, BoolFalse));
        Assert.False(ValueBox.ValueEquals(Null, BoolTrue));
    }

    [Fact]
    public void ValueEquals_InlineInt_vs_Undefined_False() {
        Assert.False(ValueBox.ValueEquals(ValueBox.FromInt64(0), Undefined));
    }

    // ═══════════════════════ ValueHashCode 一致性 ═══════════════════════

    [Theory]
    [InlineData(0L)]
    [InlineData(42L)]
    [InlineData(-1L)]
    public void ValueHashCode_SameInlineInt_SameHash(long value) {
        var a = ValueBox.FromInt64(value);
        var b = ValueBox.FromInt64(value);
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.14)]
    public void ValueHashCode_SameInlineDouble_SameHash(double value) {
        var a = ValueBox.FromRoundedDouble(value);
        var b = ValueBox.FromRoundedDouble(value);
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    [Fact]
    public void ValueHashCode_TwoHeapUInt64Max_SameHash() {
        var a = ValueBox.FromUInt64(ulong.MaxValue);
        var b = ValueBox.FromUInt64(ulong.MaxValue);
        Assert.True(ValueBox.ValueEquals(a, b)); // 前提：ValueEquals 为 true
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    [Fact]
    public void ValueHashCode_TwoHeapExactDoubleMax_SameHash() {
        var a = ValueBox.FromExactDouble(double.MaxValue);
        var b = ValueBox.FromExactDouble(double.MaxValue);
        Assert.True(ValueBox.ValueEquals(a, b)); // 前提
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    [Fact]
    public void ValueHashCode_TwoHeapInt64Min_SameHash() {
        var a = ValueBox.FromInt64(long.MinValue);
        var b = ValueBox.FromInt64(long.MinValue);
        Assert.True(ValueBox.ValueEquals(a, b));
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    // ═══════════════════════ EqualityComparer.Instance ═══════════════════════

    [Fact]
    public void EqualityComparer_Instance_IsSingleton() {
        var a = ValueBox.EqualityComparer.Instance;
        var b = ValueBox.EqualityComparer.Instance;
        Assert.Same(a, b);
    }

    [Fact]
    public void EqualityComparer_Equals_DelegatesToValueEquals() {
        var comparer = ValueBox.EqualityComparer.Instance;
        var x = ValueBox.FromInt64(42);
        var y = ValueBox.FromInt64(42);
        var z = ValueBox.FromInt64(99);
        Assert.True(comparer.Equals(x, y));
        Assert.False(comparer.Equals(x, z));
    }

    [Fact]
    public void EqualityComparer_GetHashCode_DelegatesToValueHashCode() {
        var comparer = ValueBox.EqualityComparer.Instance;
        var box = ValueBox.FromInt64(42);
        Assert.Equal(ValueBox.ValueHashCode(box), comparer.GetHashCode(box));
    }

    [Fact]
    public void EqualityComparer_Equals_HeapValues_Works() {
        var comparer = ValueBox.EqualityComparer.Instance;
        var a = ValueBox.FromUInt64(ulong.MaxValue);
        var b = ValueBox.FromUInt64(ulong.MaxValue);
        Assert.True(comparer.Equals(a, b));
        Assert.Equal(comparer.GetHashCode(a), comparer.GetHashCode(b));
    }
}
