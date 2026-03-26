using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class GenericRulesTests {

    // ═══════════════════════ Key 验证 ═══════════════════════

    [Theory]
    [InlineData(typeof(bool), true)]
    [InlineData(typeof(string), true)]
    // [InlineData(typeof(LocalId), true)]
    [InlineData(typeof(int), true)]
    [InlineData(typeof(long), true)]
    [InlineData(typeof(short), true)]
    [InlineData(typeof(sbyte), true)]
    [InlineData(typeof(byte), true)]
    [InlineData(typeof(uint), true)]
    [InlineData(typeof(ulong), true)]
    [InlineData(typeof(ushort), true)]
    [InlineData(typeof(double), true)]
    [InlineData(typeof(float), true)]
    [InlineData(typeof(Half), true)]
    [InlineData(typeof(decimal), false)]
    public void IsValidKey(Type type, bool expected) =>
        Assert.Equal(expected, HelperRegistry.IsValidKey(type));

    // ═══════════════════════ Value 验证 — 叶子 ═══════════════════════

    [Theory]
    [InlineData(typeof(bool), true)]
    [InlineData(typeof(string), true)]
    // [InlineData(typeof(LocalId), true)]
    [InlineData(typeof(int), true)]
    [InlineData(typeof(long), true)]
    [InlineData(typeof(short), true)]
    [InlineData(typeof(sbyte), true)]
    [InlineData(typeof(byte), true)]
    [InlineData(typeof(uint), true)]
    [InlineData(typeof(ulong), true)]
    [InlineData(typeof(ushort), true)]
    [InlineData(typeof(double), true)]
    [InlineData(typeof(float), true)]
    [InlineData(typeof(Half), true)]
    [InlineData(typeof(ValueBox), false)]
    [InlineData(typeof(decimal), false)]
    public void IsValidValue_Leaf(Type type, bool expected) =>
        Assert.Equal(expected, HelperRegistry.IsValidValue(type));

    // ═══════════════════════ Value 验证 — 嵌套容器 ═══════════════════════

    [Fact]
    public void IsValidValue_SJDict_ValidKeyValue() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDict<int, double>)));

    [Fact]
    public void IsValidValue_SJDict_StringKey_Valid() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDict<string, double>)));

    [Fact]
    public void IsValidValue_SJDict_StringValue_Valid() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDict<int, string>)));

    [Fact]
    public void IsValidValue_SJDeque_ValidElement() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDeque<int>)));

    [Fact]
    public void IsValidValue_SJDeque_StringElement_Valid() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDeque<string>)));

    [Fact]
    public void IsValidValue_NonGenericSJDict_Rejected() =>
        Assert.False(HelperRegistry.IsValidValue(typeof(DurableObject)));

    [Fact]
    public void IsValidValue_NonGenericSJDeque_Accepted() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDeque)));

    // ═══════════════════════ 多层嵌套 ═══════════════════════

    [Fact]
    public void IsValidValue_NestedSJDict() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDict<int, DurableDict<double, int>>)));

    [Fact]
    public void IsValidValue_DeeplyNestedSJDict_WithValueBox_Rejected() =>
        Assert.False(HelperRegistry.IsValidValue(typeof(DurableDict<int, DurableDict<double, DurableDict<int, ValueBox>>>)));

    [Fact]
    public void IsValidValue_SJDeque_OfSJDict() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDeque<DurableDict<int, double>>)));

    [Fact]
    public void IsValidValue_SJDict_OfSJDeque() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDict<int, DurableDeque<double>>)));

    [Fact]
    public void IsValidValue_Nested_StringKey_Valid() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDict<int, DurableDict<string, int>>)));

    [Fact]
    public void IsValidValue_Nested_StringValue_Valid() =>
        Assert.True(HelperRegistry.IsValidValue(typeof(DurableDict<int, DurableDict<int, string>>)));

    // ═══════════════════════ SJ 门面集成 ═══════════════════════

    [Fact]
    public void SJ_Dict_Valid_ReturnsInstance() {
        var d = Durable.Dict<int, double>();
        Assert.NotNull(d);
        Assert.IsAssignableFrom<DurableDict<int, double>>(d);
    }

    [Fact]
    public void SJ_Dict_Nested_ReturnsInstance() {
        var d = Durable.Dict<int, DurableDict<double, int>>();
        Assert.NotNull(d);
    }

    [Fact]
    public void SJ_Dict_StringKey_ReturnsInstance() {
        var d = Durable.Dict<string, int>();
        Assert.NotNull(d);
        Assert.IsAssignableFrom<DurableDict<string, int>>(d);
    }

    [Fact]
    public void SJ_Dict_StringValue_ReturnsInstance() {
        var d = Durable.Dict<int, string>();
        Assert.NotNull(d);
        Assert.IsAssignableFrom<DurableDict<int, string>>(d);
    }

    [Fact]
    public void SJ_Deque_Valid_ReturnsInstance() {
        var l = Durable.Deque<int>();
        Assert.NotNull(l);
        Assert.IsAssignableFrom<DurableDeque<int>>(l);
    }

    [Fact]
    public void SJ_Deque_String_ThrowsUntilSymbolBackedImplIsWired() =>
        Assert.Throws<ArgumentException>(() => Durable.Deque<string>());

    [Fact]
    public void SJ_Deque_Mixed_ReturnsInstance() {
        var l = Durable.Deque();
        Assert.NotNull(l);
        Assert.IsAssignableFrom<DurableDeque>(l);
    }

    [Fact]
    public void SJ_Deque_ValueBox_Throws() =>
        Assert.Throws<ArgumentException>(() => Durable.Deque<ValueBox>());
}
