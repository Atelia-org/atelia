using Atelia.Data;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;
// ai:impl `src/StateJournal/Internal/ValueBox.Equality.cs`

/// <summary>
/// <see cref="ValueBox.ValueEquals"/>、<see cref="ValueBox.ValueHashCode"/>
/// 和 <see cref="ValueBox.EqualityComparer"/> 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - 快速路径：bits 相同 → true（inline 值、布尔、null、uninitialized）。
/// - 慢路径：heap 数值同值同 Kind → true（GcPool 独占 slot，同值不同 handle）。
/// - 跨 Kind 不等：整数 ≠ 浮点（42 ≠ 42.0 的语义决策）。
/// - HashCode 一致性：ValueEquals 为 true → 同 HashCode。
/// - EqualityComparer 单例委托。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxEqualityTests {

    private sealed class FakeDurable : DurableObject {
        public override DurableObjectKind Kind => DurableObjectKind.MixedDict;
        public override bool HasChanges => false;
        internal override SizedPtr HeadTicket => default;
        internal override bool IsTracked => false;
        internal override FrameTag WritePendingDiff(BinaryDiffWriter writer, ref DiffWriteContext context) => throw new NotSupportedException();
        internal override void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context) => throw new NotSupportedException();
        internal override void DiscardChanges() => throw new NotSupportedException();

        internal override void ApplyDelta(ref BinaryDiffReader reader, SizedPtr previousVersion) => throw new NotSupportedException();
        internal override void OnLoadCompleted(SizedPtr versionTicket) => throw new NotSupportedException();

        internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) { }

        internal override bool AcceptChildRefRewrite<TRewriter>(ref TRewriter rewriter) => false;

        private protected override ReadOnlySpan<byte> TypeCode => null;
    }

    // ═══════════════════════ Helpers ═══════════════════════

    private static ValueBox Null => ValueBox.Null;
    private static ValueBox Uninitialized => default;
    private static ValueBox BoolFalse => new(LzcConstants.BoxFalse);
    private static ValueBox BoolTrue => new(LzcConstants.BoxTrue);

    public static IEnumerable<object[]> EqualsTruePairsForHashContract() {
        yield return new object[] {
            "Inline int same bits",
            ValueBox.Int64Face.From(42),
            ValueBox.Int32Face.From(42)
        };

        yield return new object[] {
            "Inline rounded double same bits",
            ValueBox.RoundedDoubleFace.From(3.14),
            ValueBox.RoundedDoubleFace.From(3.14)
        };

        yield return new object[] {
            "Heap uint64 same value different handles",
            ValueBox.UInt64Face.From(ulong.MaxValue),
            ValueBox.UInt64Face.From(ulong.MaxValue)
        };

        yield return new object[] {
            "Heap int64 same value different handles",
            ValueBox.Int64Face.From(long.MinValue),
            ValueBox.Int64Face.From(long.MinValue)
        };

        yield return new object[] {
            "Heap exact double same value different handles",
            ValueBox.ExactDoubleFace.From(double.MaxValue),
            ValueBox.ExactDoubleFace.From(double.MaxValue)
        };

        {
            ValueBox exclusive = ValueBox.UInt64Face.From(ulong.MaxValue);
            ValueBox frozen = ValueBox.Freeze(exclusive);
            yield return new object[] {
                "Heap numeric exclusive vs frozen",
                exclusive,
                frozen
            };
        }

        {
            ValueBox exclusive = ValueBox.FromSymbolId(new SymbolId(42));
            ValueBox frozen = ValueBox.Freeze(exclusive);
            yield return new object[] {
                "Heap string exclusive vs frozen",
                exclusive,
                frozen
            };
        }

        {
            ValueBox exclusive = ValueBox.DurableRefFace.From(new DurableRef(DurableObjectKind.MixedDict, new LocalId(0x1234)));
            ValueBox frozen = ValueBox.Freeze(exclusive);
            yield return new object[] {
                "Inline-DurableRef exclusive vs frozen",
                exclusive,
                frozen
            };
        }
    }

    // ═══════════════════════ ValueEquals 快速路径 — bits 相等 → true ═══════════════════════

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(42L)]
    [InlineData(-1L)]
    [InlineData(-42L)]
    public void ValueEquals_SameInlineInt_True(long value) {
        var a = ValueBox.Int64Face.From(value);
        var b = ValueBox.Int64Face.From(value);
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_FromInt64_42_And_FromInt32_42_SameBits_True() {
        // @[SAME-INLINE-SAME-VALUEBOX]: 同值 inline 整数跨类型产生相同 bits
        var a = ValueBox.Int64Face.From(42);
        var b = ValueBox.Int32Face.From(42);
        Assert.Equal(a.GetBits(), b.GetBits()); // 前提：bits 确实相同
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_Null_SelfEquals() {
        Assert.True(ValueBox.ValueEquals(Null, Null));
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
        var a = ValueBox.RoundedDoubleFace.From(value);
        var b = ValueBox.RoundedDoubleFace.From(value);
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    // ═══════════════════════ ValueEquals 慢路径 — heap 数值同值同 Kind → true ═══════════════════════

    [Fact]
    public void ValueEquals_TwoHeapUInt64Max_True() {
        // ulong.MaxValue > 2^62-1 → 走 heap，两次独立分配 → 不同 handle → 慢路径
        var a = ValueBox.UInt64Face.From(ulong.MaxValue);
        var b = ValueBox.UInt64Face.From(ulong.MaxValue);
        Assert.NotEqual(a.GetBits(), b.GetBits()); // 不同 handle → 不同 bits
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_TwoHeapInt64Min_True() {
        // long.MinValue = -2^63，超出 inline 范围 [-2^61, 2^62-1] → 走 heap
        var a = ValueBox.Int64Face.From(long.MinValue);
        var b = ValueBox.Int64Face.From(long.MinValue);
        Assert.NotEqual(a.GetBits(), b.GetBits()); // 不同 handle
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_TwoHeapExactDoubleMax_True() {
        // double.MaxValue = 0x7FEF_FFFF_FFFF_FFFF，LSB=1 → FromExactDouble 走 heap
        var a = ValueBox.ExactDoubleFace.From(double.MaxValue);
        var b = ValueBox.ExactDoubleFace.From(double.MaxValue);
        Assert.NotEqual(a.GetBits(), b.GetBits()); // 不同 handle
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_String_FrozenVsExclusive_True() {
        var exclusive = ValueBox.FromSymbolId(new SymbolId(100));
        var frozen = ValueBox.Freeze(exclusive);

        Assert.NotEqual(exclusive.GetBits(), frozen.GetBits());
        Assert.True(ValueBox.ValueEquals(exclusive, frozen));
        Assert.Equal(ValueBox.ValueHashCode(exclusive), ValueBox.ValueHashCode(frozen));
    }

    // ═══════════════════════ ValueEquals — heap 数值不同 Kind → false ═══════════════════════

    [Fact]
    public void ValueEquals_InlineInt42_vs_InlineDouble42_False() {
        // 设计决策：42 != 42.0，整数与浮点不互等
        // 42 → inline int，42.0 → inline double → bits 不同 → 快速路径 false
        var intBox = ValueBox.Int64Face.From(42);
        var dblBox = ValueBox.RoundedDoubleFace.From(42.0);
        Assert.False(ValueBox.ValueEquals(intBox, dblBox));
    }

    [Fact]
    public void ValueEquals_HeapNonnegInt_vs_HeapFloat_False() {
        // 两个都走 heap Bits64，但 Kind 分别是 NonnegativeInteger 和 FloatingPoint → false
        var intBox = ValueBox.UInt64Face.From(ulong.MaxValue); // NonnegativeInteger heap
        var dblBox = ValueBox.ExactDoubleFace.From(double.MaxValue); // FloatingPoint heap (LSB=1)
        Assert.False(ValueBox.ValueEquals(intBox, dblBox));
    }

    // ═══════════════════════ ValueEquals — heap 数值不同值同 Kind → false ═══════════════════════

    [Fact]
    public void ValueEquals_HeapUInt64Max_vs_HeapUInt64MaxMinus1_False() {
        var a = ValueBox.UInt64Face.From(ulong.MaxValue);
        var b = ValueBox.UInt64Face.From(ulong.MaxValue - 1);
        Assert.False(ValueBox.ValueEquals(a, b));
    }

    // ═══════════════════════ ValueEquals — 非 heap 值不等 → false ═══════════════════════

    [Fact]
    public void ValueEquals_InlineInt1_vs_InlineInt2_False() {
        Assert.False(ValueBox.ValueEquals(ValueBox.Int64Face.From(1), ValueBox.Int64Face.From(2)));
    }

    [Fact]
    public void ValueEquals_InlineNonnegInts_DifferOnlyPayloadBit32_AreNotEqual() {
        // 回归保护：bit32 是 inline payload 的一部分，不是 ExclusiveBit 语义位。
        // 1 与 (1 + 2^32) 在 inline nonneg 编码下仅 payload bit32 不同。
        long x = 1;
        long y = 1 + (1L << 32);

        var a = ValueBox.Int64Face.From(x);
        var b = ValueBox.Int64Face.From(y);

        Assert.NotEqual(a.GetBits(), b.GetBits());
        Assert.False(ValueBox.ValueEquals(a, b));
        Assert.False(ValueBox.ValueEquals(b, a));
    }

    [Fact]
    public void ValueEquals_InlineDouble1_vs_InlineDouble2_False() {
        Assert.False(ValueBox.ValueEquals(ValueBox.RoundedDoubleFace.From(1.0), ValueBox.RoundedDoubleFace.From(2.0)));
    }

    [Fact]
    public void ValueEquals_InlineInt0_vs_Null_False() {
        // FromInt64(0) 编码为 inline 非负整数（LZC=1），ValueBox.Null 是 Null（LZC=63）
        var intZero = ValueBox.Int64Face.From(0);
        Assert.False(ValueBox.ValueEquals(intZero, Null));
    }

    // ═══════════════════════ ValueEquals — 跨类型快速 false ═══════════════════════

    [Fact]
    public void ValueEquals_InlineInt_vs_InlineDouble_False() {
        Assert.False(ValueBox.ValueEquals(ValueBox.Int64Face.From(1), ValueBox.RoundedDoubleFace.From(1.0)));
    }

    [Fact]
    public void ValueEquals_Null_vs_Boolean_False() {
        Assert.False(ValueBox.ValueEquals(Null, BoolFalse));
        Assert.False(ValueBox.ValueEquals(Null, BoolTrue));
    }

    [Fact]
    public void ValueEquals_IsSymmetric_ForRepresentativePairs() {
        var heapU64A = ValueBox.UInt64Face.From(ulong.MaxValue);
        var heapU64B = ValueBox.UInt64Face.From(ulong.MaxValue);
        var heapU64Frozen = ValueBox.Freeze(heapU64A);

        var heapStringExclusive = ValueBox.FromSymbolId(new SymbolId(200));
        var heapStringFrozen = ValueBox.Freeze(heapStringExclusive);

        var pairs = new (ValueBox A, ValueBox B)[] {
            (ValueBox.Int64Face.From(42), ValueBox.Int64Face.From(42)),
            (ValueBox.Int64Face.From(1), ValueBox.Int64Face.From(1 + (1L << 32))),
            (heapU64A, heapU64B),
            (heapU64A, heapU64Frozen),
            (heapStringExclusive, heapStringFrozen),
            (ValueBox.UInt64Face.From(ulong.MaxValue), ValueBox.ExactDoubleFace.From(double.MaxValue)),
            (Null, BoolTrue),
        };

        foreach (var (a, b) in pairs) {
            Assert.Equal(ValueBox.ValueEquals(a, b), ValueBox.ValueEquals(b, a));
        }
    }

    // ═══════════════════════ Equality/Hash 契约：ValueEquals=true ⇒ Hash 相等 ═══════════════════════

    [Theory]
    [MemberData(nameof(EqualsTruePairsForHashContract))]
    public void HashContract_WhenValueEqualsTrue_HashCodeMustMatchSymmetrically(string caseName, object leftObj, object rightObj) {
        ValueBox left = (ValueBox)leftObj;
        ValueBox right = (ValueBox)rightObj;

        Assert.True(ValueBox.ValueEquals(left, right), caseName);
        Assert.True(ValueBox.ValueEquals(right, left), caseName);

        int leftHash = ValueBox.ValueHashCode(left);
        int rightHash = ValueBox.ValueHashCode(right);
        Assert.Equal(leftHash, rightHash);
    }

    // ═══════════════════════ ValueHashCode 一致性 ═══════════════════════

    [Theory]
    [InlineData(0L)]
    [InlineData(42L)]
    [InlineData(-1L)]
    public void ValueHashCode_SameInlineInt_SameHash(long value) {
        var a = ValueBox.Int64Face.From(value);
        var b = ValueBox.Int64Face.From(value);
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.14)]
    public void ValueHashCode_SameInlineDouble_SameHash(double value) {
        var a = ValueBox.RoundedDoubleFace.From(value);
        var b = ValueBox.RoundedDoubleFace.From(value);
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    [Fact]
    public void ValueHashCode_TwoHeapUInt64Max_SameHash() {
        var a = ValueBox.UInt64Face.From(ulong.MaxValue);
        var b = ValueBox.UInt64Face.From(ulong.MaxValue);
        Assert.True(ValueBox.ValueEquals(a, b)); // 前提：ValueEquals 为 true
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    [Fact]
    public void ValueHashCode_TwoHeapExactDoubleMax_SameHash() {
        var a = ValueBox.ExactDoubleFace.From(double.MaxValue);
        var b = ValueBox.ExactDoubleFace.From(double.MaxValue);
        Assert.True(ValueBox.ValueEquals(a, b)); // 前提
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    [Fact]
    public void ValueHashCode_TwoHeapInt64Min_SameHash() {
        var a = ValueBox.Int64Face.From(long.MinValue);
        var b = ValueBox.Int64Face.From(long.MinValue);
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
        var x = ValueBox.Int64Face.From(42);
        var y = ValueBox.Int64Face.From(42);
        var z = ValueBox.Int64Face.From(99);
        Assert.True(comparer.Equals(x, y));
        Assert.False(comparer.Equals(x, z));
    }

    [Fact]
    public void EqualityComparer_GetHashCode_DelegatesToValueHashCode() {
        var comparer = ValueBox.EqualityComparer.Instance;
        var box = ValueBox.Int64Face.From(42);
        Assert.Equal(ValueBox.ValueHashCode(box), comparer.GetHashCode(box));
    }

    [Fact]
    public void EqualityComparer_Equals_HeapValues_Works() {
        var comparer = ValueBox.EqualityComparer.Instance;
        var a = ValueBox.UInt64Face.From(ulong.MaxValue);
        var b = ValueBox.UInt64Face.From(ulong.MaxValue);
        Assert.True(comparer.Equals(a, b));
        Assert.Equal(comparer.GetHashCode(a), comparer.GetHashCode(b));
    }
}
