using Xunit;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal.Tests;

// ai:impl `src/StateJournal/Internal/ValueBox.String.cs`
/// <summary>
/// <see cref="ValueBox.FromSymbolId"/>、<see cref="ValueBox.GetSymbolId"/>
/// 和 <see cref="ValueBox.SymbolIdFace"/> 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - Roundtrip：FromSymbolId → GetSymbolId 往返正确性。
/// - SymbolIdFace：ITypedFace 接口的 From/Get/UpdateOrInit 行为。
/// - TypeMismatch：跨类型 GetSymbolId 返回 TypeMismatch。
/// - ValueEquals / ValueHashCode：SymbolId box 与自身、不同 id、不同类型之间的相等性。
/// - UpdateOrInit：从 Null/整数/堆整数/SymbolId 更新为 SymbolId，验证旧 Bits64 slot 释放。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxStringTests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static int Bits64Count => ValuePools.OfBits64.Count;

    private static ValueBox Null => new(LzcConstants.BoxNull);
    private static ValueBox BoolTrue => new(LzcConstants.BoxTrue);
    private static ValueBox BoolFalse => new(LzcConstants.BoxFalse);

    /// <summary>创建一个需要堆分配的正整数 ValueBox（超出 inline 范围）。</summary>
    private static ValueBox HeapNonnegInt() =>
        ValueBox.Int64Face.From((long)LzcConstants.NonnegIntInlineCap); // 2^62，刚好溢出 inline

    // ═══════════════════════ FromSymbolId / GetSymbolId ═══════════════════════

    [Fact]
    public void FromSymbolId_NullId_ReturnsNullBox() {
        var box = ValueBox.FromSymbolId(SymbolId.Null);
        Assert.True(box.IsNull);
    }

    [Fact]
    public void FromSymbolId_NonNull_Roundtrips() {
        var id = new SymbolId(123);
        var box = ValueBox.FromSymbolId(id);
        Assert.False(box.IsNull);

        var issue = ValueBox.GetSymbolId(box, out var decoded);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(123u, decoded.Value);
    }

    [Fact]
    public void GetSymbolId_NullBox_ReturnsNullId() {
        var issue = ValueBox.GetSymbolId(ValueBox.Null, out var id);
        Assert.Equal(GetIssue.None, issue);
        Assert.True(id.IsNull);
    }

    [Fact]
    public void GetSymbolId_NonStringBox_ReturnsTypeMismatch() {
        var intBox = ValueBox.UInt32Face.From(42);
        var issue = ValueBox.GetSymbolId(intBox, out _);
        Assert.Equal(GetIssue.TypeMismatch, issue);
    }

    [Fact]
    public void FromSymbolId_SameId_SameBits() {
        var box1 = ValueBox.FromSymbolId(new SymbolId(99));
        var box2 = ValueBox.FromSymbolId(new SymbolId(99));
        Assert.Equal(box1.GetBits(), box2.GetBits());
    }

    [Fact]
    public void FromSymbolId_DifferentId_DifferentBits() {
        var box1 = ValueBox.FromSymbolId(new SymbolId(1));
        var box2 = ValueBox.FromSymbolId(new SymbolId(2));
        Assert.NotEqual(box1.GetBits(), box2.GetBits());
    }

    // ═══════════════════════ SymbolIdFace — ITypedFace<SymbolId> ═══════════════════════

    [Fact]
    public void SymbolIdFace_From_NullId_ReturnsNullBox() {
        var box = ValueBox.SymbolIdFace.From(SymbolId.Null);
        Assert.True(box.IsNull);
    }

    [Fact]
    public void SymbolIdFace_From_NonNull_Roundtrips() {
        var id = new SymbolId(456);
        var box = ValueBox.SymbolIdFace.From(id);
        var issue = ValueBox.SymbolIdFace.Get(box, out var decoded);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(456u, decoded.Value);
    }

    [Fact]
    public void SymbolIdFace_Get_NullBox_ReturnsNullId() {
        var issue = ValueBox.SymbolIdFace.Get(Null, out var id);
        Assert.Equal(GetIssue.None, issue);
        Assert.True(id.IsNull);
    }

    [Fact]
    public void SymbolIdFace_Get_IntBox_TypeMismatch() {
        var box = ValueBox.Int64Face.From(42);
        var issue = ValueBox.SymbolIdFace.Get(box, out _);
        Assert.Equal(GetIssue.TypeMismatch, issue);
    }

    // ═══════════════════════ SymbolIdFace.UpdateOrInit ═══════════════════════

    [Fact]
    public void SymbolIdFace_UpdateOrInit_FromNull_SetsValue() {
        var box = Null;
        bool changed = ValueBox.SymbolIdFace.UpdateOrInit(ref box, new SymbolId(10), out _);
        Assert.True(changed);
        var issue = ValueBox.SymbolIdFace.Get(box, out var id);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(10u, id.Value);
    }

    [Fact]
    public void SymbolIdFace_UpdateOrInit_SameValue_ReturnsFalse() {
        var id = new SymbolId(77);
        var box = ValueBox.SymbolIdFace.From(id);
        bool changed = ValueBox.SymbolIdFace.UpdateOrInit(ref box, id, out _);
        Assert.False(changed);
    }

    [Fact]
    public void SymbolIdFace_UpdateOrInit_DifferentValue_ReturnsTrue() {
        var box = ValueBox.SymbolIdFace.From(new SymbolId(1));
        bool changed = ValueBox.SymbolIdFace.UpdateOrInit(ref box, new SymbolId(2), out _);
        Assert.True(changed);
        ValueBox.SymbolIdFace.Get(box, out var id);
        Assert.Equal(2u, id.Value);
    }

    [Fact]
    public void SymbolIdFace_UpdateOrInit_FromHeapInt_FreesBits64Slot() {
        var box = HeapNonnegInt();
        int bits64Before = Bits64Count;
        ValueBox.SymbolIdFace.UpdateOrInit(ref box, new SymbolId(99), out _);
        Assert.Equal(bits64Before - 1, Bits64Count);
        ValueBox.SymbolIdFace.Get(box, out var id);
        Assert.Equal(99u, id.Value);
    }

    [Fact]
    public void SymbolIdFace_UpdateOrInit_FromInlineInt_DoesNotFreeBits64() {
        var box = ValueBox.Int64Face.From(42);
        int bits64Before = Bits64Count;
        ValueBox.SymbolIdFace.UpdateOrInit(ref box, new SymbolId(88), out _);
        Assert.Equal(bits64Before, Bits64Count);
        ValueBox.SymbolIdFace.Get(box, out var id);
        Assert.Equal(88u, id.Value);
    }

    // ═══════════════════════ GetSymbolId — TypeMismatch (cross-type) ═══════════════════════

    [Fact]
    public void GetSymbolId_FromDouble_TypeMismatch() {
        var box = ValueBox.RoundedDoubleFace.From(3.14);
        var issue = ValueBox.GetSymbolId(box, out _);
        Assert.Equal(GetIssue.TypeMismatch, issue);
    }

    [Fact]
    public void GetSymbolId_FromBoolTrue_TypeMismatch() {
        var issue = ValueBox.GetSymbolId(BoolTrue, out _);
        Assert.Equal(GetIssue.TypeMismatch, issue);
    }

    [Fact]
    public void GetSymbolId_FromBoolFalse_TypeMismatch() {
        var issue = ValueBox.GetSymbolId(BoolFalse, out _);
        Assert.Equal(GetIssue.TypeMismatch, issue);
    }

    // ═══════════════════════ 反向：从 SymbolId ValueBox 读数值 → TypeMismatch ═══════════════════════

    [Fact]
    public void GetLong_FromSymbolId_TypeMismatch() {
        var box = ValueBox.FromSymbolId(new SymbolId(42));
        var issue = ValueBox.Int64Face.Get(box, out long value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetDouble_FromSymbolId_TypeMismatch() {
        var box = ValueBox.FromSymbolId(new SymbolId(42));
        var issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void ValidateReconstructedMixedSymbol_WhenSymbolExists_ReturnsNull() {
        var pool = StringPool.Rebuild([(new SlotHandle(7), "alpha")]);
        var box = ValueBox.FromSymbolId(new SymbolId(7));

        var error = ValueBox.ValidateReconstructedMixedSymbol(box, pool, "MixedDict");

        Assert.Null(error);
    }

    [Fact]
    public void ValidateReconstructedMixedSymbol_WhenSymbolMissing_ReturnsCorruption() {
        var box = ValueBox.FromSymbolId(new SymbolId(7));

        var error = ValueBox.ValidateReconstructedMixedSymbol(box, StringPool.Rebuild([]), "MixedDict");

        var corruption = Assert.IsType<SjCorruptionError>(error);
        Assert.Contains("MixedDict", corruption.Message);
        Assert.Contains("dangling SymbolId 7", corruption.Message);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    public void ValidateReconstructedMixedSymbol_NonSymbolBox_ReturnsNull(int intValue) {
        var pool = StringPool.Rebuild([]);
        var box = ValueBox.Int32Face.From(intValue);

        var error = ValueBox.ValidateReconstructedMixedSymbol(box, pool, "MixedDeque");

        Assert.Null(error);
    }

    // ═══════════════════════ ValueEquals 与 SymbolId ═══════════════════════

    [Fact]
    public void ValueEquals_SameSymbolId_True() {
        var a = ValueBox.FromSymbolId(new SymbolId(10));
        var b = ValueBox.FromSymbolId(new SymbolId(10));
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_DifferentSymbolIds_False() {
        var a = ValueBox.FromSymbolId(new SymbolId(10));
        var b = ValueBox.FromSymbolId(new SymbolId(20));
        Assert.False(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_SymbolIdVsInt_False() {
        var sym = ValueBox.FromSymbolId(new SymbolId(42));
        var num = ValueBox.Int64Face.From(42);
        Assert.False(ValueBox.ValueEquals(sym, num));
    }

    [Fact]
    public void ValueEquals_SymbolIdVsNull_False() {
        var sym = ValueBox.FromSymbolId(new SymbolId(1));
        Assert.False(ValueBox.ValueEquals(sym, Null));
    }

    [Fact]
    public void ValueEquals_SymbolId_SelfEquals() {
        var box = ValueBox.FromSymbolId(new SymbolId(7));
        Assert.True(ValueBox.ValueEquals(box, box));
    }

    // ═══════════════════════ ValueHashCode 与 SymbolId ═══════════════════════

    [Fact]
    public void ValueHashCode_SameSymbolId_SameHash() {
        var a = ValueBox.FromSymbolId(new SymbolId(10));
        var b = ValueBox.FromSymbolId(new SymbolId(10));
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    [Fact]
    public void ValueHashCode_SameSymbolId_ConsistentWithValueEquals() {
        var a = ValueBox.FromSymbolId(new SymbolId(10));
        var b = ValueBox.FromSymbolId(new SymbolId(10));
        Assert.True(ValueBox.ValueEquals(a, b));
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    // ═══════════════════════ FromSymbolId 不分配 Bits64 ═══════════════════════

    [Fact]
    public void FromSymbolId_DoesNotAllocateBits64() {
        int before = Bits64Count;
        _ = ValueBox.FromSymbolId(new SymbolId(999));
        Assert.Equal(before, Bits64Count);
    }
}
