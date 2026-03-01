using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;
using Xunit;

namespace Atelia.StateJournal.Tests.Internal;

/// <summary>
/// <see cref="ValueBox.ExclusiveSetInt64"/>, <see cref="ValueBox.ExclusiveSetUInt64"/>,
/// <see cref="ValueBox.ExclusiveSetRoundedDouble"/>, <see cref="ValueBox.ExclusiveSetExactDouble"/>
/// 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - 值正确性：通过 <see cref="ValueBox.Get(out long)"/> 等 roundtrip 验证。
/// - inline 编码一致性：对 inline 范围内的值，ExclusiveSet 产生的 bits 应与 From* 一致。
/// - Pool 行为：通过 <see cref="ValuePools.Bits64"/> 的 Count delta 验证 inplace 复用、Free、及新分配。
///
/// 注意：ValuePools.Bits64 是进程级静态单例，测试间共享状态。
/// 验证基于 Count delta（操作前后的差值），不依赖绝对值。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxExclusiveSetTests {

    // ═══════════════════════ Helpers ═══════════════════════

    /// <summary>创建一个持有给定 long 值的 ValueBox（通过 FromInt64）。</summary>
    private static ValueBox BoxInt64(long value) => ValueBox.FromInt64(value);

    /// <summary>创建一个持有给定 ulong 值的 ValueBox（通过 FromUInt64）。</summary>
    private static ValueBox BoxUInt64(ulong value) => ValueBox.FromUInt64(value);

    /// <summary>创建一个需要堆分配的正整数 ValueBox。</summary>
    private static ValueBox HeapNonnegInt() => BoxInt64((long)LzcConstants.NonnegIntInlineCap); // 2^62，刚好溢出 inline

    /// <summary>创建一个需要堆分配的负整数 ValueBox。</summary>
    private static ValueBox HeapNegInt() => BoxInt64(LzcConstants.NegIntInlineMin - 1); // -2^61-1，刚好溢出 inline

    /// <summary>创建一个需要堆分配的 exact double ValueBox（LSB=1 的 double）。</summary>
    private static ValueBox HeapDouble() {
        // double 1.0000000000000002 的 IEEE 754 bits = 0x3FF0_0000_0000_0001，LSB=1 → 需要堆分配
        double v = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        return ValueBox.FromExactDouble(v);
    }

    /// <summary>获取当前 Bits64 池的活跃 slot 数量。</summary>
    private static int PoolCount => ValuePools.Bits64.Count;

    /// <summary>断言 ValueBox 能正确读出 long 值。</summary>
    private static void AssertGetLong(ValueBox box, long expected) {
        GetIssue issue = box.Get(out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(expected, actual);
    }

    /// <summary>断言 ValueBox 能正确读出 ulong 值。</summary>
    private static void AssertGetULong(ValueBox box, ulong expected) {
        GetIssue issue = box.Get(out ulong actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(expected, actual);
    }

    /// <summary>断言 ValueBox 能正确读出 double 值（bits 精确匹配）。</summary>
    private static void AssertGetDoubleBits(ValueBox box, double expected) {
        GetIssue issue = box.Get(out double actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(BitConverter.DoubleToUInt64Bits(expected), BitConverter.DoubleToUInt64Bits(actual));
    }

    // ═══════════════════════ ExclusiveSetInt64 — 值正确性 ═══════════════════════

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(42L)]
    [InlineData(-42L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void SetInt64_FromNull_RoundtripsCorrectly(long value) {
        var box = new ValueBox(0); // Null
        ValueBox.ExclusiveSetInt64(ref box, value);
        AssertGetLong(box, value);
    }

    [Fact]
    public void SetInt64_MaxInlinePositive() {
        long value = (long)LzcConstants.NonnegIntInlineCap - 1; // 2^62 - 1，最大 inline 正整数
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetInt64(ref box, value);
        AssertGetLong(box, value);
        // 应与 FromInt64 产生相同 bits（inline）
        Assert.Equal(ValueBox.FromInt64(value).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetInt64_MinHeapPositive() {
        long value = (long)LzcConstants.NonnegIntInlineCap; // 2^62，刚好需要堆
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetInt64(ref box, value);
        AssertGetLong(box, value);
    }

    [Fact]
    public void SetInt64_MinInlineNegative() {
        long value = LzcConstants.NegIntInlineMin; // -2^61，最小 inline 负整数
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetInt64(ref box, value);
        AssertGetLong(box, value);
        Assert.Equal(ValueBox.FromInt64(value).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetInt64_MaxHeapNegative() {
        long value = LzcConstants.NegIntInlineMin - 1; // -2^61 - 1，刚好需要堆
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetInt64(ref box, value);
        AssertGetLong(box, value);
    }

    // ═══════════════════════ ExclusiveSetInt64 — 转换路径 ═══════════════════════

    [Fact]
    public void SetInt64_InlineToInline_BitsMatchFrom() {
        var box = BoxInt64(10);
        ValueBox.ExclusiveSetInt64(ref box, 20);
        AssertGetLong(box, 20);
        Assert.Equal(ValueBox.FromInt64(20).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetInt64_InlineToInline_NegToPos() {
        var box = BoxInt64(-5);
        ValueBox.ExclusiveSetInt64(ref box, 5);
        AssertGetLong(box, 5);
        Assert.Equal(ValueBox.FromInt64(5).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetInt64_InlineToHeap_IncreasesPoolCount() {
        var box = BoxInt64(1);
        int before = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, long.MaxValue); // long.MaxValue > NonnegIntInlineCap → heap
        AssertGetLong(box, long.MaxValue);
        Assert.Equal(before + 1, PoolCount);
    }

    [Fact]
    public void SetInt64_HeapToInline_DecreasesPoolCount() {
        var box = HeapNonnegInt();
        int before = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, 42); // 42 是 inline
        AssertGetLong(box, 42);
        Assert.Equal(before - 1, PoolCount);
        Assert.Equal(ValueBox.FromInt64(42).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetInt64_HeapToHeap_SameSign_InplaceReuse_PoolCountUnchanged() {
        var box = HeapNonnegInt(); // 需要堆的正整数
        int before = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, long.MaxValue); // 另一个需要堆的正整数
        AssertGetLong(box, long.MaxValue);
        Assert.Equal(before, PoolCount); // inplace，Count 不变
    }

    [Fact]
    public void SetInt64_HeapToHeap_CrossSign_InplaceReuse_PoolCountUnchanged() {
        var box = HeapNonnegInt(); // 正整数堆 slot
        int before = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, long.MinValue); // 负整数堆 slot → 复用同一 slot
        AssertGetLong(box, long.MinValue);
        Assert.Equal(before, PoolCount); // inplace
    }

    [Fact]
    public void SetInt64_HeapNeg_ToHeapNeg_InplaceReuse() {
        var box = HeapNegInt();
        int before = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, long.MinValue);
        AssertGetLong(box, long.MinValue);
        Assert.Equal(before, PoolCount);
    }

    // ═══════════════════════ ExclusiveSetInt64 — 跨类型 Bits64 slot 复用 ═══════════════════════

    [Fact]
    public void SetInt64_FromHeapDouble_ToHeapInt_InplaceReuse() {
        var box = HeapDouble(); // FloatingPoint 类型的 Bits64 slot
        int before = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, long.MaxValue); // 需要堆的正整数
        AssertGetLong(box, long.MaxValue);
        Assert.Equal(before, PoolCount); // 跨类型复用，Count 不变
    }

    [Fact]
    public void SetInt64_FromHeapDouble_ToInlineInt_FreesSlot() {
        var box = HeapDouble();
        int before = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, 7);
        AssertGetLong(box, 7);
        Assert.Equal(before - 1, PoolCount); // 旧 slot 已释放
    }

    [Fact]
    public void SetInt64_FromInlineDouble_ToInlineInt_NoPoolChange() {
        var box = ValueBox.FromRoundedDouble(3.14); // inline double
        int before = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, 99);
        AssertGetLong(box, 99);
        Assert.Equal(before, PoolCount); // 两边都无 pool slot
    }

    // ═══════════════════════ ExclusiveSetInt64 — 多次连续调用 ═══════════════════════

    [Fact]
    public void SetInt64_MultipleUpdates_HeapToHeapToInline() {
        var box = new ValueBox(0);
        // null → heap
        int c0 = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, long.MaxValue);
        Assert.Equal(c0 + 1, PoolCount);
        AssertGetLong(box, long.MaxValue);

        // heap → heap (inplace)
        int c1 = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, long.MinValue);
        Assert.Equal(c1, PoolCount);
        AssertGetLong(box, long.MinValue);

        // heap → inline (free)
        int c2 = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, 0);
        Assert.Equal(c2 - 1, PoolCount);
        AssertGetLong(box, 0);

        // inline → inline
        int c3 = PoolCount;
        ValueBox.ExclusiveSetInt64(ref box, -1);
        Assert.Equal(c3, PoolCount);
        AssertGetLong(box, -1);
    }

    // ═══════════════════════ ExclusiveSetUInt64 — 值正确性 ═══════════════════════

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(ulong.MaxValue)]
    public void SetUInt64_FromNull_RoundtripsCorrectly(ulong value) {
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetUInt64(ref box, value);
        AssertGetULong(box, value);
    }

    [Fact]
    public void SetUInt64_MaxInline() {
        ulong value = LzcConstants.NonnegIntInlineCap - 1;
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetUInt64(ref box, value);
        AssertGetULong(box, value);
        Assert.Equal(ValueBox.FromUInt64(value).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetUInt64_MinHeap() {
        ulong value = LzcConstants.NonnegIntInlineCap;
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetUInt64(ref box, value);
        AssertGetULong(box, value);
    }

    // ═══════════════════════ ExclusiveSetUInt64 — 转换路径 ═══════════════════════

    [Fact]
    public void SetUInt64_InlineToHeap_IncreasesPoolCount() {
        var box = BoxUInt64(1);
        int before = PoolCount;
        ValueBox.ExclusiveSetUInt64(ref box, ulong.MaxValue);
        AssertGetULong(box, ulong.MaxValue);
        Assert.Equal(before + 1, PoolCount);
    }

    [Fact]
    public void SetUInt64_HeapToInline_DecreasesPoolCount() {
        var box = BoxUInt64(ulong.MaxValue);
        int before = PoolCount;
        ValueBox.ExclusiveSetUInt64(ref box, 0);
        AssertGetULong(box, 0);
        Assert.Equal(before - 1, PoolCount);
    }

    [Fact]
    public void SetUInt64_HeapToHeap_InplaceReuse() {
        var box = BoxUInt64(LzcConstants.NonnegIntInlineCap);
        int before = PoolCount;
        ValueBox.ExclusiveSetUInt64(ref box, ulong.MaxValue);
        AssertGetULong(box, ulong.MaxValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void SetUInt64_FromHeapDouble_CrossTypeReuse() {
        var box = HeapDouble();
        int before = PoolCount;
        ValueBox.ExclusiveSetUInt64(ref box, ulong.MaxValue);
        AssertGetULong(box, ulong.MaxValue);
        Assert.Equal(before, PoolCount);
    }

    // ═══════════════════════ ExclusiveSetRoundedDouble — 值正确性 ═══════════════════════

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(3.14)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SetRoundedDouble_FromNull_MatchesFromRoundedDouble(double value) {
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetRoundedDouble(ref box, value);
        // ExclusiveSetRoundedDouble 始终 inline，bits 应与 FromRoundedDouble 一致
        Assert.Equal(ValueBox.FromRoundedDouble(value).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetRoundedDouble_NaN_MatchesFromRoundedDouble() {
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetRoundedDouble(ref box, double.NaN);
        Assert.Equal(ValueBox.FromRoundedDouble(double.NaN).GetBits(), box.GetBits());
    }

    // ═══════════════════════ ExclusiveSetRoundedDouble — 旧 slot 清理 ═══════════════════════

    [Fact]
    public void SetRoundedDouble_FromHeapInt_FreesSlot() {
        var box = HeapNonnegInt();
        int before = PoolCount;
        ValueBox.ExclusiveSetRoundedDouble(ref box, 2.718);
        Assert.Equal(before - 1, PoolCount); // lossy double 始终 inline，旧 slot 被释放
        Assert.Equal(ValueBox.FromRoundedDouble(2.718).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetRoundedDouble_FromHeapDouble_FreesSlot() {
        var box = HeapDouble();
        int before = PoolCount;
        ValueBox.ExclusiveSetRoundedDouble(ref box, 1.5);
        Assert.Equal(before - 1, PoolCount);
    }

    [Fact]
    public void SetRoundedDouble_FromInline_NoPoolChange() {
        var box = BoxInt64(42);
        int before = PoolCount;
        ValueBox.ExclusiveSetRoundedDouble(ref box, 0.5);
        Assert.Equal(before, PoolCount); // 两边都无 pool
    }

    // ═══════════════════════ ExclusiveSetExactDouble — 值正确性 ═══════════════════════

    [Theory]
    [InlineData(0.0)]    // LSB=0, inline
    [InlineData(1.0)]    // LSB=0, inline
    [InlineData(-1.0)]   // LSB=0, inline
    [InlineData(0.5)]    // LSB=0, inline
    public void SetExactDouble_InlineValues_MatchesFromExactDouble(double value) {
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetExactDouble(ref box, value);
        Assert.Equal(ValueBox.FromExactDouble(value).GetBits(), box.GetBits());
        AssertGetDoubleBits(box, value);
    }

    [Fact]
    public void SetExactDouble_HeapValue_RoundtripsCorrectly() {
        // 0x3FF0_0000_0000_0001 的 LSB=1 → 需要堆分配
        double value = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetExactDouble(ref box, value);
        AssertGetDoubleBits(box, value);
    }

    // ═══════════════════════ ExclusiveSetExactDouble — 转换路径 ═══════════════════════

    [Fact]
    public void SetExactDouble_InlineFromHeapInt_FreesSlot() {
        var box = HeapNonnegInt();
        int before = PoolCount;
        ValueBox.ExclusiveSetExactDouble(ref box, 1.0); // LSB=0 → inline
        Assert.Equal(before - 1, PoolCount);
        AssertGetDoubleBits(box, 1.0);
    }

    [Fact]
    public void SetExactDouble_HeapFromHeapInt_CrossTypeReuse() {
        var box = HeapNonnegInt();
        double value = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        int before = PoolCount;
        ValueBox.ExclusiveSetExactDouble(ref box, value); // LSB=1 → heap，复用旧 int slot
        Assert.Equal(before, PoolCount);
        AssertGetDoubleBits(box, value);
    }

    [Fact]
    public void SetExactDouble_HeapFromNull_IncreasesPoolCount() {
        double value = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = new ValueBox(0);
        int before = PoolCount;
        ValueBox.ExclusiveSetExactDouble(ref box, value);
        Assert.Equal(before + 1, PoolCount);
        AssertGetDoubleBits(box, value);
    }

    [Fact]
    public void SetExactDouble_HeapToHeap_InplaceReuse() {
        var box = HeapDouble();
        double value2 = BitConverter.UInt64BitsToDouble(0x4000_0000_0000_0001); // 另一个 LSB=1 的 double
        int before = PoolCount;
        ValueBox.ExclusiveSetExactDouble(ref box, value2);
        Assert.Equal(before, PoolCount);
        AssertGetDoubleBits(box, value2);
    }

    // ═══════════════════════ 跨方法联调 ═══════════════════════

    [Fact]
    public void CrossMethod_Int64ThenDouble_SlotReuse() {
        // int(heap) → double(heap)：跨方法复用同一 Bits64 slot
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetInt64(ref box, long.MaxValue); // heap int
        int c1 = PoolCount;

        double d = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        ValueBox.ExclusiveSetExactDouble(ref box, d); // heap double，复用
        Assert.Equal(c1, PoolCount);
        AssertGetDoubleBits(box, d);
    }

    [Fact]
    public void CrossMethod_DoubleThenInt64_SlotReuse() {
        // double(heap) → int(heap)
        double d = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetExactDouble(ref box, d);
        int c1 = PoolCount;

        ValueBox.ExclusiveSetInt64(ref box, long.MinValue); // heap neg int，复用
        Assert.Equal(c1, PoolCount);
        AssertGetLong(box, long.MinValue);
    }

    [Fact]
    public void CrossMethod_RoundedDoubleThenUInt64_HeapAlloc() {
        // rounded double (inline) → uint64 (heap)
        var box = new ValueBox(0);
        ValueBox.ExclusiveSetRoundedDouble(ref box, 1.23);
        int c1 = PoolCount;

        ValueBox.ExclusiveSetUInt64(ref box, ulong.MaxValue); // heap，无旧 slot 可复用
        Assert.Equal(c1 + 1, PoolCount);
        AssertGetULong(box, ulong.MaxValue);
    }
}
