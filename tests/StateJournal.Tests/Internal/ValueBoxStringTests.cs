using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

// ai:impl `src/StateJournal/Internal/ValueBox.String.cs`
/// <summary>
/// <see cref="ValueBox.FromString"/>、<see cref="ValueBox.Get(out string)"/>
/// 和 <see cref="ValueBox.UpdateByString"/> 的单元测试。
/// </summary>
/// <remarks>
/// 测试策略：
/// - Roundtrip：FromString → Get(out string) 往返正确性，覆盖空串、ASCII、Unicode、长字符串。
/// - InternPool 去重：同值同 bits，不同值不同 bits。
/// - Pool 行为：FromString 只影响 Strings 池，不影响 Bits64 池。
/// - TypeMismatch：跨类型 Get 返回 TypeMismatch。
/// - ValueEquals / ValueHashCode：字符串与自身、不同字符串、不同类型之间的相等性。
/// - InternSetString：从 Null/整数/堆整数/字符串更新为字符串，验证旧 Bits64 slot 释放。
/// </remarks>
[Collection("ValueBox")]
public class ValueBoxStringTests {

    // ═══════════════════════ Helpers ═══════════════════════

    private static int Bits64Count => ValuePools.OfBits64.Count;
    private static int StringsCount => ValuePools.OfString.Count;

    private static ValueBox Null => new(LzcConstants.BoxNull);
    private static ValueBox Uninitialized => new(LzcConstants.BoxUninitialized);
    private static ValueBox BoolFalse => new(LzcConstants.BoxFalse);
    private static ValueBox BoolTrue => new(LzcConstants.BoxTrue);

    private static void AssertRoundtrip(string expected) {
        var box = ValueBox.StringFace.From(expected);
        GetIssue issue = ValueBox.StringFace.Get(box, out string? actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(expected, actual);
    }

    /// <summary>创建一个需要堆分配的正整数 ValueBox（超出 inline 范围）。</summary>
    private static ValueBox HeapNonnegInt() =>
        ValueBox.Int64Face.From((long)LzcConstants.NonnegIntInlineCap); // 2^62，刚好溢出 inline

    // ═══════════════════════ FromString → Get(out string) Roundtrip ═══════════════════════

    [Fact]
    public void FromString_EmptyString_Roundtrips() => AssertRoundtrip("");

    [Fact]
    public void FromString_SimpleAscii_Roundtrips() => AssertRoundtrip("hello");

    [Fact]
    public void FromString_ChineseCharacters_Roundtrips() => AssertRoundtrip("你好世界");

    [Fact]
    public void FromString_Emoji_Roundtrips() => AssertRoundtrip("🎉");

    [Fact]
    public void FromString_MixedUnicode_Roundtrips() => AssertRoundtrip("Hello 你好 🎉!");

    [Fact]
    public void FromString_LongString_Roundtrips() {
        string longStr = new('x', 10_000);
        AssertRoundtrip(longStr);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("abc")]
    [InlineData("hello world")]
    public void FromString_Theory_Roundtrips(string value) => AssertRoundtrip(value);

    // ═══════════════════════ InternPool 去重 — 同值同 bits ═══════════════════════

    [Fact]
    public void FromString_SameValue_ProducesSameBits() {
        var a = ValueBox.StringFace.From("hello");
        var b = ValueBox.StringFace.From("hello");
        Assert.Equal(a.GetBits(), b.GetBits());
    }

    [Fact]
    public void FromString_DifferentValues_ProduceDifferentBits() {
        var a = ValueBox.StringFace.From("hello");
        var b = ValueBox.StringFace.From("world");
        Assert.NotEqual(a.GetBits(), b.GetBits());
    }

    [Fact]
    public void FromString_EmptyAndNonEmpty_DifferentBits() {
        var a = ValueBox.StringFace.From("");
        var b = ValueBox.StringFace.From("x");
        Assert.NotEqual(a.GetBits(), b.GetBits());
    }

    // ═══════════════════════ Pool 行为 ═══════════════════════

    [Fact]
    public void FromString_DoesNotAllocateBits64() {
        int before = Bits64Count;
        _ = ValueBox.StringFace.From("test_bits64_not_affected");
        Assert.Equal(before, Bits64Count);
    }

    [Fact]
    public void FromString_AllocatesInStringsPool() {
        // 使用足够独特的字符串，确保之前未被 intern
        string unique = $"unique_string_{Guid.NewGuid()}";
        int before = StringsCount;
        _ = ValueBox.StringFace.From(unique);
        Assert.Equal(before + 1, StringsCount);
    }

    [Fact]
    public void FromString_SameValue_DoesNotAllocateSecondSlot() {
        string unique = $"dedup_test_{Guid.NewGuid()}";
        _ = ValueBox.StringFace.From(unique);
        int after1 = StringsCount;
        _ = ValueBox.StringFace.From(unique);
        Assert.Equal(after1, StringsCount); // 去重，不再分配
    }

    // ═══════════════════════ Get(out string) — TypeMismatch ═══════════════════════

    [Fact]
    public void GetString_FromInt64_TypeMismatch() {
        var box = ValueBox.Int64Face.From(42);
        GetIssue issue = ValueBox.StringFace.Get(box, out string? value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Null(value);
    }

    [Fact]
    public void GetString_FromDouble_TypeMismatch() {
        var box = ValueBox.RoundedDoubleFace.From(3.14);
        GetIssue issue = ValueBox.StringFace.Get(box, out string? value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Null(value);
    }

    [Fact]
    public void GetString_FromNull_None() {
        var box = Null;
        GetIssue issue = ValueBox.StringFace.Get(box, out string? value);
        Assert.Equal(GetIssue.None, issue);
        Assert.Null(value);
    }

    [Fact]
    public void GetString_FromBooleanTrue_TypeMismatch() {
        var box = BoolTrue;
        GetIssue issue = ValueBox.StringFace.Get(box, out string? value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Null(value);
    }

    [Fact]
    public void GetString_FromBooleanFalse_TypeMismatch() {
        var box = BoolFalse;
        GetIssue issue = ValueBox.StringFace.Get(box, out string? value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Null(value);
    }

    // ═══════════════════════ 反向：从 String ValueBox 读数值 → TypeMismatch ═══════════════════════

    [Fact]
    public void GetLong_FromString_TypeMismatch() {
        var box = ValueBox.StringFace.From("42");
        GetIssue issue = ValueBox.Int64Face.Get(box, out long value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    [Fact]
    public void GetDouble_FromString_TypeMismatch() {
        var box = ValueBox.StringFace.From("3.14");
        GetIssue issue = box.GetDouble(out double value);
        Assert.Equal(GetIssue.TypeMismatch, issue);
        Assert.Equal(default, value);
    }

    // ═══════════════════════ ValueEquals 与 String ═══════════════════════

    [Fact]
    public void ValueEquals_SameString_True() {
        var a = ValueBox.StringFace.From("hello");
        var b = ValueBox.StringFace.From("hello");
        Assert.True(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_DifferentStrings_False() {
        var a = ValueBox.StringFace.From("hello");
        var b = ValueBox.StringFace.From("world");
        Assert.False(ValueBox.ValueEquals(a, b));
    }

    [Fact]
    public void ValueEquals_StringVsInt_DifferentType_False() {
        var str = ValueBox.StringFace.From("42");
        var num = ValueBox.Int64Face.From(42);
        Assert.False(ValueBox.ValueEquals(str, num));
    }

    [Fact]
    public void ValueEquals_EmptyStringVsNull_False() {
        var str = ValueBox.StringFace.From("");
        Assert.False(ValueBox.ValueEquals(str, Null));
    }

    [Fact]
    public void ValueEquals_String_SelfEquals() {
        var box = ValueBox.StringFace.From("self");
        Assert.True(ValueBox.ValueEquals(box, box));
    }

    // ═══════════════════════ ValueHashCode 与 String ═══════════════════════

    [Fact]
    public void ValueHashCode_SameString_SameHash() {
        var a = ValueBox.StringFace.From("hello");
        var b = ValueBox.StringFace.From("hello");
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    [Fact]
    public void ValueHashCode_SameString_ConsistentWithValueEquals() {
        var a = ValueBox.StringFace.From("hello");
        var b = ValueBox.StringFace.From("hello");
        Assert.True(ValueBox.ValueEquals(a, b)); // 前提
        Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
    }

    // ═══════════════════════ InternSetString — 从 Null 更新为 String ═══════════════════════

    [Fact]
    public void InternSetString_FromNull_RoundtripsCorrectly() {
        var box = Null;
        ValueBox.StringFace.Update(ref box, "hello");
        GetIssue issue = ValueBox.StringFace.Get(box, out string? actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal("hello", actual);
    }

    // ═══════════════════════ InternSetString — 从 String 更新为不同 String ═══════════════════════

    [Fact]
    public void InternSetString_FromString_ToAnotherString() {
        var box = ValueBox.StringFace.From("old");
        ValueBox.StringFace.Update(ref box, "new");
        GetIssue issue = ValueBox.StringFace.Get(box, out string? actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal("new", actual);
    }

    // ═══════════════════════ InternSetString — 从 inline 整数更新为 String ═══════════════════════

    [Fact]
    public void InternSetString_FromInlineInt_ToStringCorrectly() {
        var box = ValueBox.Int64Face.From(42); // inline int
        int bits64Before = Bits64Count;
        ValueBox.StringFace.Update(ref box, "replaced");
        // inline int 没有 Bits64 slot，Bits64.Count 不变
        Assert.Equal(bits64Before, Bits64Count);
        GetIssue issue = ValueBox.StringFace.Get(box, out string? actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal("replaced", actual);
    }

    // ═══════════════════════ InternSetString — 从堆整数更新为 String（验证 Bits64 释放）═══════════════════════

    [Fact]
    public void InternSetString_FromHeapInt_FreesBits64Slot() {
        var box = HeapNonnegInt(); // 堆上的正整数，持有 Bits64 slot
        int bits64Before = Bits64Count;
        ValueBox.StringFace.Update(ref box, "from_heap");
        // 旧的 Bits64 slot 应被释放
        Assert.Equal(bits64Before - 1, Bits64Count);
        GetIssue issue = ValueBox.StringFace.Get(box, out string? actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal("from_heap", actual);
    }

    [Fact]
    public void InternSetString_FromHeapNegInt_FreesBits64Slot() {
        var box = ValueBox.Int64Face.From(LzcConstants.NegIntInlineMin - 1); // 堆上的负整数
        int bits64Before = Bits64Count;
        ValueBox.StringFace.Update(ref box, "from_neg_heap");
        Assert.Equal(bits64Before - 1, Bits64Count);
        GetIssue issue = ValueBox.StringFace.Get(box, out string? actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal("from_neg_heap", actual);
    }

    [Fact]
    public void InternSetString_FromHeapDouble_FreesBits64Slot() {
        // 0x3FF0_0000_0000_0001 的 LSB=1 → 需要堆分配的 double
        double d = BitConverter.UInt64BitsToDouble(0x3FF0_0000_0000_0001);
        var box = ValueBox.ExactDoubleFace.From(d);
        int bits64Before = Bits64Count;
        ValueBox.StringFace.Update(ref box, "from_heap_double");
        Assert.Equal(bits64Before - 1, Bits64Count);
        GetIssue issue = ValueBox.StringFace.Get(box, out string? actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal("from_heap_double", actual);
    }

    // ═══════════════════════ InternSetString — String 不释放旧 String slot ═══════════════════════

    [Fact]
    public void InternSetString_FromString_OldSlotNotFreed() {
        // InternPool 不支持手动 Free，旧 string slot 留给 Mark-Sweep GC
        string unique1 = $"intern_old_{Guid.NewGuid()}";
        string unique2 = $"intern_new_{Guid.NewGuid()}";
        var box = ValueBox.StringFace.From(unique1);
        int stringsBefore = StringsCount;
        int bits64Before = Bits64Count;
        ValueBox.StringFace.Update(ref box, unique2);
        // Strings 池增长 1（新字符串），旧字符串 slot 不释放
        Assert.Equal(stringsBefore + 1, StringsCount);
        // Bits64 池不受影响
        Assert.Equal(bits64Before, Bits64Count);
        GetIssue issue = ValueBox.StringFace.Get(box, out string? actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal(unique2, actual);
    }

    // ═══════════════════════ InternSetString — Uninitialized/Boolean → String ═══════════════════════

    [Fact]
    public void InternSetString_FromUninitialized_Works() {
        var box = Uninitialized;
        ValueBox.StringFace.Update(ref box, "overwrite_undef");
        GetIssue issue = ValueBox.StringFace.Get(box, out string? actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal("overwrite_undef", actual);
    }

    [Fact]
    public void InternSetString_FromBoolTrue_Works() {
        var box = BoolTrue;
        ValueBox.StringFace.Update(ref box, "overwrite_bool");
        GetIssue issue = ValueBox.StringFace.Get(box, out string? actual);
        Assert.Equal(GetIssue.None, issue);
        Assert.Equal("overwrite_bool", actual);
    }
}
