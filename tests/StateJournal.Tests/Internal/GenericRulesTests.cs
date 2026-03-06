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
        Assert.Equal(expected, GenericRules.IsValidKey(type));

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
        Assert.Equal(expected, GenericRules.IsValidValue(type));

    // ═══════════════════════ Value 验证 — 嵌套容器 ═══════════════════════

    [Fact]
    public void IsValidValue_SJDict_ValidKeyValue() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableDict<int, double>)));

    [Fact]
    public void IsValidValue_SJDict_StringKey_Valid() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableDict<string, double>)));

    [Fact]
    public void IsValidValue_SJDict_StringValue_Valid() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableDict<int, string>)));

    [Fact]
    public void IsValidValue_SJList_ValidElement() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableList<int>)));

    [Fact]
    public void IsValidValue_SJList_StringElement_Valid() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableList<string>)));

    [Fact]
    public void IsValidValue_NonGenericSJDict_Rejected() =>
        Assert.False(GenericRules.IsValidValue(typeof(DurableObject)));

    [Fact]
    public void IsValidValue_NonGenericSJList_Accepted() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableList)));

    // ═══════════════════════ 多层嵌套 ═══════════════════════

    [Fact]
    public void IsValidValue_NestedSJDict() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableDict<int, DurableDict<double, int>>)));

    [Fact]
    public void IsValidValue_DeeplyNestedSJDict_WithValueBox_Rejected() =>
        Assert.False(GenericRules.IsValidValue(typeof(DurableDict<int, DurableDict<double, DurableDict<int, ValueBox>>>)));

    [Fact]
    public void IsValidValue_SJList_OfSJDict() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableList<DurableDict<int, double>>)));

    [Fact]
    public void IsValidValue_SJDict_OfSJList() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableDict<int, DurableList<double>>)));

    [Fact]
    public void IsValidValue_Nested_StringKey_Valid() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableDict<int, DurableDict<string, int>>)));

    [Fact]
    public void IsValidValue_Nested_StringValue_Valid() =>
        Assert.True(GenericRules.IsValidValue(typeof(DurableDict<int, DurableDict<int, string>>)));

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
    public void SJ_List_Valid_ReturnsInstance() {
        var l = Durable.List<int>();
        Assert.NotNull(l);
        Assert.IsAssignableFrom<DurableList<int>>(l);
    }

    [Fact]
    public void SJ_List_String_ReturnsInstance() {
        var l = Durable.List<string>();
        Assert.NotNull(l);
        Assert.IsAssignableFrom<DurableList<string>>(l);
    }

    [Fact]
    public void SJ_List_Mixed_ReturnsInstance() {
        var l = Durable.List();
        Assert.NotNull(l);
        Assert.IsAssignableFrom<DurableList>(l);
    }

    [Fact]
    public void SJ_List_ValueBox_Throws() =>
        Assert.Throws<ArgumentException>(() => Durable.List<ValueBox>());
}
