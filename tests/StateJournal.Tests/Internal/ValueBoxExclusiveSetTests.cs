using Atelia.StateJournal;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

/// <summary>
/// <see cref="ValueBox.Int64Face.Update"/>, <see cref="ValueBox.UInt64Face.Update"/>,
/// <see cref="ValueBox.RoundedDoubleFace.Update"/>, <see cref="ValueBox.ExactDoubleFace.Update"/>
/// 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - 值正确性：通过 <see cref="ValueBox.Int64Face.Get"/> 等 roundtrip 验证。
/// - inline 编码一致性：对 inline 范围内的值，ExclusiveSet 产生的 bits 应与 From* 一致。
/// - Pool 行为：通过 <see cref="ValuePools.OfBits64"/> 的 Count delta 验证 inplace 复用、Free、及新分配。
///
/// 注意：ValuePools.Bits64 是进程级静态单例，测试间共享状态。
/// 验证基于 Count delta（操作前后的差值），不依赖绝对值。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxExclusiveSetTests {

    // ═══════════════════════ Helpers ═══════════════════════

    /// <summary>创建一个持有给定 long 值的 ValueBox（通过 FromInt64）。</summary>
    private static ValueBox BoxInt64(long value) => ValueBox.Int64Face.From(value);

    /// <summary>创建一个持有给定 ulong 值的 ValueBox（通过 FromUInt64）。</summary>
    private static ValueBox BoxUInt64(ulong value) => ValueBox.UInt64Face.From(value);

    /// <summary>创建一个需要堆分配的正整数 ValueBox。</summary>
    private static ValueBox HeapNonnegInt() => BoxInt64((long)LzcConstants.NonnegIntInlineCap); // 2^62，刚好溢出 inline

    /// <summary>创建一个需要堆分配的负整数 ValueBox。</summary>
    private static ValueBox HeapNegInt() => BoxInt64(LzcConstants.NegIntInlineMin - 1); // -2^61-1，刚好溢出 inline

    /// <summary>创建一个需要堆分配的 exact double ValueBox（LSB=1 的 double）。</summary>
    private static ValueBox HeapDouble() {
        // double 1.0000000000000002 的 IEEE 754 bits = 0x3FF0_0000_0000_0001，LSB=1 → 需要堆分配
        double v = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        return ValueBox.ExactDoubleFace.From(v);
    }

    /// <summary>获取当前 Bits64 池的活跃 slot 数量。</summary>
    private static int PoolCount => ValuePools.OfBits64.Count;

    /// <summary>断言 ValueBox 能正确读出 long 值。</summary>
    private static void AssertGetLong(ValueBox box, long expected) {
        GetIssue issue = ValueBox.Int64Face.Get(box, out long actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(expected, actual);
    }

    /// <summary>断言 ValueBox 能正确读出 ulong 值。</summary>
    private static void AssertGetULong(ValueBox box, ulong expected) {
        GetIssue issue = ValueBox.UInt64Face.Get(box, out ulong actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(expected, actual);
    }

    /// <summary>断言 ValueBox 能正确读出 double 值（bits 精确匹配）。</summary>
    private static void AssertGetDoubleBits(ValueBox box, double expected) {
        GetIssue issue = box.GetDouble(out double actual);
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
    public void SetInt64_FromUninitialized_RoundtripsCorrectly(long value) {
        var box = default(ValueBox); // Uninitialized
        ValueBox.Int64Face.Update(ref box, value);
        AssertGetLong(box, value);
    }

    [Fact]
    public void SetInt64_MaxInlinePositive() {
        long value = (long)LzcConstants.NonnegIntInlineCap - 1; // 2^62 - 1，最大 inline 正整数
        var box = default(ValueBox);
        ValueBox.Int64Face.Update(ref box, value);
        AssertGetLong(box, value);
        // 应与 FromInt64 产生相同 bits（inline）
        Assert.Equal(ValueBox.Int64Face.From(value).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetInt64_MinHeapPositive() {
        long value = (long)LzcConstants.NonnegIntInlineCap; // 2^62，刚好需要堆
        var box = default(ValueBox);
        ValueBox.Int64Face.Update(ref box, value);
        AssertGetLong(box, value);
    }

    [Fact]
    public void SetInt64_MinInlineNegative() {
        long value = LzcConstants.NegIntInlineMin; // -2^61，最小 inline 负整数
        var box = default(ValueBox);
        ValueBox.Int64Face.Update(ref box, value);
        AssertGetLong(box, value);
        Assert.Equal(ValueBox.Int64Face.From(value).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetInt64_MaxHeapNegative() {
        long value = LzcConstants.NegIntInlineMin - 1; // -2^61 - 1，刚好需要堆
        var box = default(ValueBox);
        ValueBox.Int64Face.Update(ref box, value);
        AssertGetLong(box, value);
    }

    // ═══════════════════════ ExclusiveSetInt64 — 转换路径 ═══════════════════════

    [Fact]
    public void SetInt64_InlineToInline_BitsMatchFrom() {
        var box = BoxInt64(10);
        ValueBox.Int64Face.Update(ref box, 20);
        AssertGetLong(box, 20);
        Assert.Equal(ValueBox.Int64Face.From(20).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetInt64_InlineToInline_NegToPos() {
        var box = BoxInt64(-5);
        ValueBox.Int64Face.Update(ref box, 5);
        AssertGetLong(box, 5);
        Assert.Equal(ValueBox.Int64Face.From(5).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetInt64_InlineToHeap_IncreasesPoolCount() {
        var box = BoxInt64(1);
        int before = PoolCount;
        ValueBox.Int64Face.Update(ref box, long.MaxValue); // long.MaxValue > NonnegIntInlineCap → heap
        AssertGetLong(box, long.MaxValue);
        Assert.Equal(before + 1, PoolCount);
    }

    [Fact]
    public void SetInt64_HeapToInline_DecreasesPoolCount() {
        var box = HeapNonnegInt();
        int before = PoolCount;
        ValueBox.Int64Face.Update(ref box, 42); // 42 是 inline
        AssertGetLong(box, 42);
        Assert.Equal(before - 1, PoolCount);
        Assert.Equal(ValueBox.Int64Face.From(42).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetInt64_HeapToHeap_SameSign_InplaceReuse_PoolCountUnchanged() {
        var box = HeapNonnegInt(); // 需要堆的正整数
        int before = PoolCount;
        ValueBox.Int64Face.Update(ref box, long.MaxValue); // 另一个需要堆的正整数
        AssertGetLong(box, long.MaxValue);
        Assert.Equal(before, PoolCount); // inplace，Count 不变
    }

    [Fact]
    public void SetInt64_HeapToHeap_CrossSign_InplaceReuse_PoolCountUnchanged() {
        var box = HeapNonnegInt(); // 正整数堆 slot
        int before = PoolCount;
        ValueBox.Int64Face.Update(ref box, long.MinValue); // 负整数堆 slot → 复用同一 slot
        AssertGetLong(box, long.MinValue);
        Assert.Equal(before, PoolCount); // inplace
    }

    [Fact]
    public void SetInt64_HeapNeg_ToHeapNeg_InplaceReuse() {
        var box = HeapNegInt();
        int before = PoolCount;
        ValueBox.Int64Face.Update(ref box, long.MinValue);
        AssertGetLong(box, long.MinValue);
        Assert.Equal(before, PoolCount);
    }

    // ═══════════════════════ ExclusiveSetInt64 — 跨类型 Bits64 slot 复用 ═══════════════════════

    [Fact]
    public void SetInt64_FromHeapDouble_ToHeapInt_InplaceReuse() {
        var box = HeapDouble(); // FloatingPoint 类型的 Bits64 slot
        int before = PoolCount;
        ValueBox.Int64Face.Update(ref box, long.MaxValue); // 需要堆的正整数
        AssertGetLong(box, long.MaxValue);
        Assert.Equal(before, PoolCount); // 跨类型复用，Count 不变
    }

    [Fact]
    public void SetInt64_FromHeapDouble_ToInlineInt_FreesSlot() {
        var box = HeapDouble();
        int before = PoolCount;
        ValueBox.Int64Face.Update(ref box, 7);
        AssertGetLong(box, 7);
        Assert.Equal(before - 1, PoolCount); // 旧 slot 已释放
    }

    [Fact]
    public void SetInt64_FromInlineDouble_ToInlineInt_NoPoolChange() {
        var box = ValueBox.RoundedDoubleFace.From(3.14); // inline double
        int before = PoolCount;
        ValueBox.Int64Face.Update(ref box, 99);
        AssertGetLong(box, 99);
        Assert.Equal(before, PoolCount); // 两边都无 pool slot
    }

    // ═══════════════════════ ExclusiveSetInt64 — 多次连续调用 ═══════════════════════

    [Fact]
    public void SetInt64_MultipleUpdates_HeapToHeapToInline() {
        var box = default(ValueBox);
        // null → heap
        int c0 = PoolCount;
        ValueBox.Int64Face.Update(ref box, long.MaxValue);
        Assert.Equal(c0 + 1, PoolCount);
        AssertGetLong(box, long.MaxValue);

        // heap → heap (inplace)
        int c1 = PoolCount;
        ValueBox.Int64Face.Update(ref box, long.MinValue);
        Assert.Equal(c1, PoolCount);
        AssertGetLong(box, long.MinValue);

        // heap → inline (free)
        int c2 = PoolCount;
        ValueBox.Int64Face.Update(ref box, 0);
        Assert.Equal(c2 - 1, PoolCount);
        AssertGetLong(box, 0);

        // inline → inline
        int c3 = PoolCount;
        ValueBox.Int64Face.Update(ref box, -1);
        Assert.Equal(c3, PoolCount);
        AssertGetLong(box, -1);
    }

    // ═══════════════════════ ExclusiveSetUInt64 — 值正确性 ═══════════════════════

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(ulong.MaxValue)]
    public void SetUInt64_FromUninitialized_RoundtripsCorrectly(ulong value) {
        var box = default(ValueBox);
        ValueBox.UInt64Face.Update(ref box, value);
        AssertGetULong(box, value);
    }

    [Fact]
    public void SetUInt64_MaxInline() {
        ulong value = LzcConstants.NonnegIntInlineCap - 1;
        var box = default(ValueBox);
        ValueBox.UInt64Face.Update(ref box, value);
        AssertGetULong(box, value);
        Assert.Equal(ValueBox.UInt64Face.From(value).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetUInt64_MinHeap() {
        ulong value = LzcConstants.NonnegIntInlineCap;
        var box = default(ValueBox);
        ValueBox.UInt64Face.Update(ref box, value);
        AssertGetULong(box, value);
    }

    // ═══════════════════════ ExclusiveSetUInt64 — 转换路径 ═══════════════════════

    [Fact]
    public void SetUInt64_InlineToHeap_IncreasesPoolCount() {
        var box = BoxUInt64(1);
        int before = PoolCount;
        ValueBox.UInt64Face.Update(ref box, ulong.MaxValue);
        AssertGetULong(box, ulong.MaxValue);
        Assert.Equal(before + 1, PoolCount);
    }

    [Fact]
    public void SetUInt64_HeapToInline_DecreasesPoolCount() {
        var box = BoxUInt64(ulong.MaxValue);
        int before = PoolCount;
        ValueBox.UInt64Face.Update(ref box, 0);
        AssertGetULong(box, 0);
        Assert.Equal(before - 1, PoolCount);
    }

    [Fact]
    public void SetUInt64_HeapToHeap_InplaceReuse() {
        var box = BoxUInt64(LzcConstants.NonnegIntInlineCap);
        int before = PoolCount;
        ValueBox.UInt64Face.Update(ref box, ulong.MaxValue);
        AssertGetULong(box, ulong.MaxValue);
        Assert.Equal(before, PoolCount);
    }

    [Fact]
    public void SetUInt64_FromHeapDouble_CrossTypeReuse() {
        var box = HeapDouble();
        int before = PoolCount;
        ValueBox.UInt64Face.Update(ref box, ulong.MaxValue);
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
    public void SetRoundedDouble_FromUninitialized_MatchesFromRoundedDouble(double value) {
        var box = default(ValueBox);
        ValueBox.RoundedDoubleFace.Update(ref box, value);
        // ExclusiveSetRoundedDouble 始终 inline，bits 应与 FromRoundedDouble 一致
        Assert.Equal(ValueBox.RoundedDoubleFace.From(value).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetRoundedDouble_NaN_MatchesFromRoundedDouble() {
        var box = default(ValueBox);
        ValueBox.RoundedDoubleFace.Update(ref box, double.NaN);
        Assert.Equal(ValueBox.RoundedDoubleFace.From(double.NaN).GetBits(), box.GetBits());
    }

    // ═══════════════════════ ExclusiveSetRoundedDouble — 旧 slot 清理 ═══════════════════════

    [Fact]
    public void SetRoundedDouble_FromHeapInt_FreesSlot() {
        var box = HeapNonnegInt();
        int before = PoolCount;
        ValueBox.RoundedDoubleFace.Update(ref box, 2.718);
        Assert.Equal(before - 1, PoolCount); // lossy double 始终 inline，旧 slot 被释放
        Assert.Equal(ValueBox.RoundedDoubleFace.From(2.718).GetBits(), box.GetBits());
    }

    [Fact]
    public void SetRoundedDouble_FromHeapDouble_FreesSlot() {
        var box = HeapDouble();
        int before = PoolCount;
        ValueBox.RoundedDoubleFace.Update(ref box, 1.5);
        Assert.Equal(before - 1, PoolCount);
    }

    [Fact]
    public void SetRoundedDouble_FromInline_NoPoolChange() {
        var box = BoxInt64(42);
        int before = PoolCount;
        ValueBox.RoundedDoubleFace.Update(ref box, 0.5);
        Assert.Equal(before, PoolCount); // 两边都无 pool
    }

    // ═══════════════════════ ExclusiveSetExactDouble — 值正确性 ═══════════════════════

    [Theory]
    [InlineData(0.0)]    // LSB=0, inline
    [InlineData(1.0)]    // LSB=0, inline
    [InlineData(-1.0)]   // LSB=0, inline
    [InlineData(0.5)]    // LSB=0, inline
    public void SetExactDouble_InlineValues_MatchesFromExactDouble(double value) {
        var box = default(ValueBox);
        ValueBox.ExactDoubleFace.Update(ref box, value);
        Assert.Equal(ValueBox.ExactDoubleFace.From(value).GetBits(), box.GetBits());
        AssertGetDoubleBits(box, value);
    }

    [Fact]
    public void SetExactDouble_HeapValue_RoundtripsCorrectly() {
        // 0x3FF0_0000_0000_0001 的 LSB=1 → 需要堆分配
        double value = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = default(ValueBox);
        ValueBox.ExactDoubleFace.Update(ref box, value);
        AssertGetDoubleBits(box, value);
    }

    // ═══════════════════════ ExclusiveSetExactDouble — 转换路径 ═══════════════════════

    [Fact]
    public void SetExactDouble_InlineFromHeapInt_FreesSlot() {
        var box = HeapNonnegInt();
        int before = PoolCount;
        ValueBox.ExactDoubleFace.Update(ref box, 1.0); // LSB=0 → inline
        Assert.Equal(before - 1, PoolCount);
        AssertGetDoubleBits(box, 1.0);
    }

    [Fact]
    public void SetExactDouble_HeapFromHeapInt_CrossTypeReuse() {
        var box = HeapNonnegInt();
        double value = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        int before = PoolCount;
        ValueBox.ExactDoubleFace.Update(ref box, value); // LSB=1 → heap，复用旧 int slot
        Assert.Equal(before, PoolCount);
        AssertGetDoubleBits(box, value);
    }

    [Fact]
    public void SetExactDouble_HeapFromNull_IncreasesPoolCount() {
        double value = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = default(ValueBox);
        int before = PoolCount;
        ValueBox.ExactDoubleFace.Update(ref box, value);
        Assert.Equal(before + 1, PoolCount);
        AssertGetDoubleBits(box, value);
    }

    [Fact]
    public void SetExactDouble_HeapToHeap_InplaceReuse() {
        var box = HeapDouble();
        double value2 = BitConverter.UInt64BitsToDouble(0x4000_0000_0000_0001); // 另一个 LSB=1 的 double
        int before = PoolCount;
        ValueBox.ExactDoubleFace.Update(ref box, value2);
        Assert.Equal(before, PoolCount);
        AssertGetDoubleBits(box, value2);
    }

    // ═══════════════════════ 跨方法联调 ═══════════════════════

    [Fact]
    public void CrossMethod_Int64ThenDouble_SlotReuse() {
        // int(heap) → double(heap)：跨方法复用同一 Bits64 slot
        var box = default(ValueBox);
        ValueBox.Int64Face.Update(ref box, long.MaxValue); // heap int
        int c1 = PoolCount;

        double d = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        ValueBox.ExactDoubleFace.Update(ref box, d); // heap double，复用
        Assert.Equal(c1, PoolCount);
        AssertGetDoubleBits(box, d);
    }

    [Fact]
    public void CrossMethod_DoubleThenInt64_SlotReuse() {
        // double(heap) → int(heap)
        double d = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = default(ValueBox);
        ValueBox.ExactDoubleFace.Update(ref box, d);
        int c1 = PoolCount;

        ValueBox.Int64Face.Update(ref box, long.MinValue); // heap neg int，复用
        Assert.Equal(c1, PoolCount);
        AssertGetLong(box, long.MinValue);
    }

    [Fact]
    public void CrossMethod_RoundedDoubleThenUInt64_HeapAlloc() {
        // rounded double (inline) → uint64 (heap)
        var box = default(ValueBox);
        ValueBox.RoundedDoubleFace.Update(ref box, 1.23);
        int c1 = PoolCount;

        ValueBox.UInt64Face.Update(ref box, ulong.MaxValue); // heap，无旧 slot 可复用
        Assert.Equal(c1 + 1, PoolCount);
        AssertGetULong(box, ulong.MaxValue);
    }

    // ═══════════════════════ Update 返回值：值相等时返回 false ═══════════════════════

    [Theory]
    [InlineData(0L)]
    [InlineData(42L)]
    [InlineData(-1L)]
    [InlineData(-42L)]
    public void SetInt64_SameInlineValue_ReturnsFalse(long value) {
        var box = ValueBox.Int64Face.From(value);
        ulong bitsBefore = box.GetBits();
        Assert.False(ValueBox.Int64Face.Update(ref box, value));
        Assert.Equal(bitsBefore, box.GetBits()); // bits 未变
    }

    [Fact]
    public void SetInt64_SameHeapNonneg_ReturnsFalse() {
        long value = (long)LzcConstants.NonnegIntInlineCap; // 堆分配正整数
        var box = ValueBox.Int64Face.From(value);
        ulong bitsBefore = box.GetBits();
        int poolBefore = PoolCount;
        Assert.False(ValueBox.Int64Face.Update(ref box, value));
        Assert.Equal(bitsBefore, box.GetBits());
        Assert.Equal(poolBefore, PoolCount); // 未分配新 slot
    }

    [Fact]
    public void SetInt64_SameHeapNeg_ReturnsFalse() {
        long value = LzcConstants.NegIntInlineMin - 1; // 堆分配负整数
        var box = ValueBox.Int64Face.From(value);
        ulong bitsBefore = box.GetBits();
        int poolBefore = PoolCount;
        Assert.False(ValueBox.Int64Face.Update(ref box, value));
        Assert.Equal(bitsBefore, box.GetBits());
        Assert.Equal(poolBefore, PoolCount);
    }

    [Theory]
    [InlineData(0L, 1L)]
    [InlineData(42L, -42L)]
    [InlineData(0L, -1L)]
    public void SetInt64_DifferentInlineValue_ReturnsTrue(long first, long second) {
        var box = ValueBox.Int64Face.From(first);
        Assert.True(ValueBox.Int64Face.Update(ref box, second));
        AssertGetLong(box, second);
    }

    [Fact]
    public void SetInt64_FromUninitialized_ReturnsTrue() {
        var box = default(ValueBox);
        Assert.True(ValueBox.Int64Face.Update(ref box, 42));
        AssertGetLong(box, 42);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(42u)]
    [InlineData(uint.MaxValue)]
    public void SetUInt32_SameValue_ReturnsFalse(uint value) {
        var box = ValueBox.UInt32Face.From(value);
        ulong bitsBefore = box.GetBits();
        Assert.False(ValueBox.UInt32Face.Update(ref box, value));
        Assert.Equal(bitsBefore, box.GetBits());
    }

    [Theory]
    [InlineData(0, 0)]        // int 0 == uint 0 → 同编码
    [InlineData(42, 42)]
    public void SetInt32_SameValueAsUInt32_ReturnsFalse(int intVal, uint uintVal) {
        // 验证跨 Face 写入同值也能检测
        var box = ValueBox.UInt32Face.From(uintVal);
        Assert.False(ValueBox.Int32Face.Update(ref box, intVal));
    }

    [Theory]
    [InlineData((ulong)0)]
    [InlineData((ulong)42)]
    public void SetUInt64_SameInlineValue_ReturnsFalse(ulong value) {
        var box = ValueBox.UInt64Face.From(value);
        ulong bitsBefore = box.GetBits();
        Assert.False(ValueBox.UInt64Face.Update(ref box, value));
        Assert.Equal(bitsBefore, box.GetBits());
    }

    [Fact]
    public void SetUInt64_SameHeapValue_ReturnsFalse() {
        ulong value = ulong.MaxValue;
        var box = ValueBox.UInt64Face.From(value);
        ulong bitsBefore = box.GetBits();
        int poolBefore = PoolCount;
        Assert.False(ValueBox.UInt64Face.Update(ref box, value));
        Assert.Equal(bitsBefore, box.GetBits());
        Assert.Equal(poolBefore, PoolCount);
    }

    // ═══════════ Boolean Update 返回值 ═══════════

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetBool_SameValue_ReturnsFalse(bool value) {
        var box = ValueBox.BooleanFace.From(value);
        Assert.False(ValueBox.BooleanFace.Update(ref box, value));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SetBool_DifferentValue_ReturnsTrue(bool first, bool second) {
        var box = ValueBox.BooleanFace.From(first);
        Assert.True(ValueBox.BooleanFace.Update(ref box, second));
    }

    // ═══════════ RoundedDouble Update 返回值 ═══════════

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(-1.5)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SetRoundedDouble_SameValue_ReturnsFalse(double value) {
        var box = ValueBox.RoundedDoubleFace.From(value);
        Assert.False(ValueBox.RoundedDoubleFace.Update(ref box, value));
    }

    [Fact]
    public void SetRoundedDouble_DifferentValue_ReturnsTrue() {
        var box = ValueBox.RoundedDoubleFace.From(1.0);
        Assert.True(ValueBox.RoundedDoubleFace.Update(ref box, 2.0));
    }

    // ═══════════ RoundedDouble NumericEquiv: -0.0 ≠ +0.0, all NaN equal ═══════════

    [Fact]
    public void SetRoundedDouble_NegZeroToPosZero_ReturnsTrue() {
        var box = ValueBox.RoundedDoubleFace.From(-0.0);
        Assert.True(ValueBox.RoundedDoubleFace.Update(ref box, +0.0));
    }

    [Fact]
    public void SetRoundedDouble_PosZeroToNegZero_ReturnsTrue() {
        var box = ValueBox.RoundedDoubleFace.From(+0.0);
        Assert.True(ValueBox.RoundedDoubleFace.Update(ref box, -0.0));
    }

    [Fact]
    public void SetRoundedDouble_SameNaN_ReturnsFalse() {
        var box = ValueBox.RoundedDoubleFace.From(double.NaN);
        Assert.False(ValueBox.RoundedDoubleFace.Update(ref box, double.NaN));
    }

    [Fact]
    public void SetRoundedDouble_DifferentNaNPayload_ReturnsFalse() {
        // 两个不同 payload 的 NaN，NumericEquiv 下都视为相等
        double nan1 = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0000); // canonical qNaN
        double nan2 = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001); // 不同 payload
        var box = ValueBox.RoundedDoubleFace.From(nan1);
        Assert.False(ValueBox.RoundedDoubleFace.Update(ref box, nan2));
    }

    [Fact]
    public void SetRoundedDouble_SignalingNaN_EqualsQuietNaN() {
        double snan = BitConverter.UInt64BitsToDouble(0x7FF0_0000_0000_0001); // sNaN
        double qnan = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0000); // qNaN
        var box = ValueBox.RoundedDoubleFace.From(snan);
        Assert.False(ValueBox.RoundedDoubleFace.Update(ref box, qnan));
    }

    [Fact]
    public void SetRoundedDouble_SameValue_FromHeapDouble_ReturnsFalse() {
        // old 是通过 ExactDouble 存入的 heap double，RoundedDouble Update 应检测到值相同
        double value = 1.5; // LSB=0, inline
        var box = ValueBox.ExactDoubleFace.From(value);
        Assert.False(ValueBox.RoundedDoubleFace.Update(ref box, value));
    }

    [Fact]
    public void SetRoundedDouble_NaN_FromHeapDouble_ReturnsFalse() {
        // old 是通过 ExactDouble 存入的 heap NaN（LSB=1），RoundedDouble Update 应检测到 NaN 互等
        double heapNaN = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001); // LSB=1 → heap
        var box = ValueBox.ExactDoubleFace.From(heapNaN);
        int poolBefore = PoolCount;
        Assert.False(ValueBox.RoundedDoubleFace.Update(ref box, double.NaN));
        Assert.Equal(poolBefore, PoolCount); // heap slot 未被释放也未分配
    }

    // ═══════════ ExactDouble Update 返回值 ═══════════

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]   // LSB=0 → inline
    [InlineData(-1.0)]
    public void SetExactDouble_SameInlineValue_ReturnsFalse(double value) {
        var box = ValueBox.ExactDoubleFace.From(value);
        Assert.False(ValueBox.ExactDoubleFace.Update(ref box, value));
    }

    [Fact]
    public void SetExactDouble_SameHeapValue_ReturnsFalse() {
        // 0x3FF0_0000_0000_0001 → LSB=1 → heap
        double value = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = ValueBox.ExactDoubleFace.From(value);
        int poolBefore = PoolCount;
        Assert.False(ValueBox.ExactDoubleFace.Update(ref box, value));
        Assert.Equal(poolBefore, PoolCount);
    }

    [Fact]
    public void SetExactDouble_DifferentValue_ReturnsTrue() {
        var box = ValueBox.ExactDoubleFace.From(1.0);
        Assert.True(ValueBox.ExactDoubleFace.Update(ref box, 2.0));
    }

    // ═══════════ ExactDouble BitExact: -0.0 ≠ +0.0, different NaN ≠ ═══════════

    [Fact]
    public void SetExactDouble_NegZeroToPosZero_ReturnsTrue() {
        var box = ValueBox.ExactDoubleFace.From(-0.0);
        Assert.True(ValueBox.ExactDoubleFace.Update(ref box, +0.0));
    }

    [Fact]
    public void SetExactDouble_PosZeroToNegZero_ReturnsTrue() {
        var box = ValueBox.ExactDoubleFace.From(+0.0);
        Assert.True(ValueBox.ExactDoubleFace.Update(ref box, -0.0));
    }

    [Fact]
    public void SetExactDouble_SameNaN_ReturnsFalse() {
        var box = ValueBox.ExactDoubleFace.From(double.NaN);
        Assert.False(ValueBox.ExactDoubleFace.Update(ref box, double.NaN));
    }

    [Fact]
    public void SetExactDouble_DifferentNaNPayload_ReturnsTrue() {
        // BitExact: 不同 payload 的 NaN 视为不同
        double nan1 = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0000);
        double nan2 = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0002); // LSB=0, inline
        var box = ValueBox.ExactDoubleFace.From(nan1);
        Assert.True(ValueBox.ExactDoubleFace.Update(ref box, nan2));
    }

    [Fact]
    public void SetExactDouble_SameHeapNaN_ReturnsFalse() {
        // 同一个 NaN payload (LSB=1 → heap)，BitExact 下应相等
        double nan = BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001);
        var box = ValueBox.ExactDoubleFace.From(nan);
        int poolBefore = PoolCount;
        Assert.False(ValueBox.ExactDoubleFace.Update(ref box, nan));
        Assert.Equal(poolBefore, PoolCount);
    }

    [Fact]
    public void SetExactDouble_SameValue_FromRoundedInline_ReturnsFalse() {
        // old 是通过 RoundedDouble 存入的 inline double，ExactDouble Update 应检测到值相同
        double value = 1.0; // 常规值，RoundedDouble 无损
        var box = ValueBox.RoundedDoubleFace.From(value);
        Assert.False(ValueBox.ExactDoubleFace.Update(ref box, value));
    }

    // ═══════════ Single Update 返回值 ═══════════

    [Theory]
    [InlineData(0.0f)]
    [InlineData(1.5f)]
    [InlineData(-1.5f)]
    [InlineData(float.PositiveInfinity)]
    public void SetSingle_SameValue_ReturnsFalse(float value) {
        var box = ValueBox.SingleFace.From(value);
        Assert.False(ValueBox.SingleFace.Update(ref box, value));
    }

    [Fact]
    public void SetSingle_DifferentValue_ReturnsTrue() {
        var box = ValueBox.SingleFace.From(1.0f);
        Assert.True(ValueBox.SingleFace.Update(ref box, 2.0f));
    }

    // ═══════════ Single NumericEquiv: -0.0f ≠ +0.0f, all NaN equal ═══════════

    [Fact]
    public void SetSingle_NegZeroToPosZero_ReturnsTrue() {
        float neg0 = BitConverter.Int32BitsToSingle(unchecked((int)0x8000_0000));
        var box = ValueBox.SingleFace.From(neg0);
        Assert.True(ValueBox.SingleFace.Update(ref box, +0.0f));
    }

    [Fact]
    public void SetSingle_PosZeroToNegZero_ReturnsTrue() {
        float neg0 = BitConverter.Int32BitsToSingle(unchecked((int)0x8000_0000));
        var box = ValueBox.SingleFace.From(+0.0f);
        Assert.True(ValueBox.SingleFace.Update(ref box, neg0));
    }

    [Fact]
    public void SetSingle_SameNaN_ReturnsFalse() {
        var box = ValueBox.SingleFace.From(float.NaN);
        Assert.False(ValueBox.SingleFace.Update(ref box, float.NaN));
    }

    [Fact]
    public void SetSingle_DifferentNaNPayload_ReturnsFalse() {
        // float 的不同 NaN payload 拓宽为 double 后仍然是不同的 NaN，但 NumericEquiv 下都互等
        float nan1 = float.NaN;
        float nan2 = BitConverter.Int32BitsToSingle(0x7FC0_0001); // 不同 payload
        var box = ValueBox.SingleFace.From(nan1);
        Assert.False(ValueBox.SingleFace.Update(ref box, nan2));
    }

    // ═══════════ Half Update 返回值 ═══════════

    [Fact]
    public void SetHalf_SameValue_ReturnsFalse() {
        Half value = (Half)1.5;
        var box = ValueBox.HalfFace.From(value);
        Assert.False(ValueBox.HalfFace.Update(ref box, value));
    }

    [Fact]
    public void SetHalf_DifferentValue_ReturnsTrue() {
        var box = ValueBox.HalfFace.From((Half)1.0);
        Assert.True(ValueBox.HalfFace.Update(ref box, (Half)2.0));
    }

    // ═══════════ Half NumericEquiv: -0 ≠ +0, all NaN equal ═══════════

    [Fact]
    public void SetHalf_NegZeroToPosZero_ReturnsTrue() {
        Half neg0 = BitConverter.UInt16BitsToHalf(0x8000);
        var box = ValueBox.HalfFace.From(neg0);
        Assert.True(ValueBox.HalfFace.Update(ref box, (Half)0));
    }

    [Fact]
    public void SetHalf_PosZeroToNegZero_ReturnsTrue() {
        Half neg0 = BitConverter.UInt16BitsToHalf(0x8000);
        var box = ValueBox.HalfFace.From((Half)0);
        Assert.True(ValueBox.HalfFace.Update(ref box, neg0));
    }

    [Fact]
    public void SetHalf_SameNaN_ReturnsFalse() {
        var box = ValueBox.HalfFace.From(Half.NaN);
        Assert.False(ValueBox.HalfFace.Update(ref box, Half.NaN));
    }

    // ═══════════ String Update 返回值 ═══════════

    [Fact]
    public void SetString_SameValue_ReturnsFalse() {
        var box = ValueBox.StringFace.From("hello");
        Assert.False(ValueBox.StringFace.Update(ref box, "hello"));
    }

    [Fact]
    public void SetString_SameNull_ReturnsFalse() {
        var box = ValueBox.StringFace.From((string?)null);
        Assert.False(ValueBox.StringFace.Update(ref box, null));
    }

    [Fact]
    public void SetString_DifferentValue_ReturnsTrue() {
        var box = ValueBox.StringFace.From("hello");
        Assert.True(ValueBox.StringFace.Update(ref box, "world"));
    }

    [Fact]
    public void SetString_NullToNonNull_ReturnsTrue() {
        var box = ValueBox.StringFace.From((string?)null);
        Assert.True(ValueBox.StringFace.Update(ref box, "hello"));
    }

    [Fact]
    public void SetString_NonNullToNull_ReturnsTrue() {
        var box = ValueBox.StringFace.From("hello");
        Assert.True(ValueBox.StringFace.Update(ref box, null));
    }

    // ═══════════ DurableObject Update 返回值 ═══════════

    [Fact]
    public void SetDurableObject_SameNull_ReturnsFalse() {
        var box = ValueBox.DurableObjectFace.From((DurableObject?)null);
        Assert.False(ValueBox.DurableObjectFace.Update(ref box, null));
    }

    [Fact]
    public void SetDurableObject_NullToNonNull_ReturnsTrue() {
        var box = ValueBox.DurableObjectFace.From((DurableObject?)null);
        var obj = new TestDurableDict();
        Assert.True(ValueBox.DurableObjectFace.Update(ref box, obj));
    }

    private sealed class TestDurableDict : DurableDict<string> {
        internal override void WritePendingDiff(IDiffWriter writer, DiffWriteContext context) { }
        internal override void OnCommitSucceeded() { }
        public override void DiscardChanges() { }
    }
}
