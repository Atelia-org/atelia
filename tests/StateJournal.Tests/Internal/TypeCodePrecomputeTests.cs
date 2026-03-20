using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

/// <summary>
/// 验证 HelperRegistry / DurableFactory 预计算的 TypeCode
/// 能被 TypeCodec.TryDecode 正确还原为对应的 Type。
/// </summary>
public class TypeCodePrecomputeTests {

    // ═══════════════════════ 辅助 ═══════════════════════

    private static void AssertRoundTrip(byte[]? typeCode, Type expected) {
        Assert.NotNull(typeCode);
        Assert.True(TypeCodec.TryDecode(typeCode, out var decoded), $"TryDecode failed for {expected}");
        Assert.Equal(expected, decoded);
    }

    // ═══════════════════════ Key 叶子类型 ═══════════════════════

    [Theory]
    [InlineData(typeof(bool))]
    [InlineData(typeof(string))]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    [InlineData(typeof(Half))]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(ushort))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(long))]
    [InlineData(typeof(int))]
    [InlineData(typeof(short))]
    [InlineData(typeof(sbyte))]
    public void ResolveKeyHelper_TypeCode_RoundTrips(Type keyType) {
        var entry = HelperRegistry.ResolveKeyHelper(keyType);
        Assert.True(entry.IsValid);
        AssertRoundTrip(entry.TypeCode, keyType);
    }

    // ═══════════════════════ Value 叶子类型（通过缓存） ═══════════════════════

    [Fact]
    public void ResolveValueHelper_DurableDeque_TypeCode_RoundTrips() {
        var entry = HelperRegistry.ResolveValueHelper(typeof(DurableDeque));
        Assert.True(entry.IsValid);
        AssertRoundTrip(entry.TypeCode, typeof(DurableDeque));
    }

    // ═══════════════════════ 泛型容器 — HelperRegistry ═══════════════════════

    [Fact]
    public void ResolveValueHelper_TypedDict_TypeCode_RoundTrips() {
        var entry = HelperRegistry.ResolveValueHelper(typeof(DurableDict<string, int>));
        Assert.True(entry.IsValid);
        AssertRoundTrip(entry.TypeCode, typeof(DurableDict<string, int>));
    }

    [Fact]
    public void ResolveValueHelper_MixedDict_TypeCode_RoundTrips() {
        var entry = HelperRegistry.ResolveValueHelper(typeof(DurableDict<string>));
        Assert.True(entry.IsValid);
        AssertRoundTrip(entry.TypeCode, typeof(DurableDict<string>));
    }

    [Fact]
    public void ResolveValueHelper_TypedDeque_TypeCode_RoundTrips() {
        var entry = HelperRegistry.ResolveValueHelper(typeof(DurableDeque<int>));
        Assert.True(entry.IsValid);
        AssertRoundTrip(entry.TypeCode, typeof(DurableDeque<int>));
    }

    [Fact]
    public void ResolveValueHelper_NestedDict_TypeCode_RoundTrips() {
        // DurableDict<int, DurableDict<string, double>>
        var type = typeof(DurableDict<int, DurableDict<string, double>>);
        var entry = HelperRegistry.ResolveValueHelper(type);
        Assert.True(entry.IsValid);
        AssertRoundTrip(entry.TypeCode, type);
    }

    [Fact]
    public void ResolveValueHelper_DequeOfDict_TypeCode_RoundTrips() {
        // DurableDeque<DurableDict<int, string>>
        var type = typeof(DurableDeque<DurableDict<int, string>>);
        var entry = HelperRegistry.ResolveValueHelper(type);
        Assert.True(entry.IsValid);
        AssertRoundTrip(entry.TypeCode, type);
    }

    [Fact]
    public void ResolveValueHelper_DictOfDeque_TypeCode_RoundTrips() {
        // DurableDict<int, DurableDeque<double>>
        var type = typeof(DurableDict<int, DurableDeque<double>>);
        var entry = HelperRegistry.ResolveValueHelper(type);
        Assert.True(entry.IsValid);
        AssertRoundTrip(entry.TypeCode, type);
    }

    // ═══════════════════════ DurableFactory 静态字段 ═══════════════════════

    [Fact]
    public void TypedDictFactory_TypeCode_RoundTrips() {
        AssertRoundTrip(TypedDictFactory<string, int>.TypeCode, typeof(DurableDict<string, int>));
    }

    [Fact]
    public void TypedDictFactory_TypeCode_MatchesHelperRegistry() {
        var factoryCode = TypedDictFactory<string, int>.TypeCode;
        var registryCode = HelperRegistry.ResolveValueHelper(typeof(DurableDict<string, int>)).TypeCode;
        Assert.Equal(registryCode, factoryCode);
    }

    [Fact]
    public void MixedDictFactory_TypeCode_RoundTrips() {
        AssertRoundTrip(MixedDictFactory<string>.TypeCode, typeof(DurableDict<string>));
    }

    [Fact]
    public void MixedDictFactory_TypeCode_MatchesHelperRegistry() {
        var factoryCode = MixedDictFactory<string>.TypeCode;
        var registryCode = HelperRegistry.ResolveValueHelper(typeof(DurableDict<string>)).TypeCode;
        Assert.Equal(registryCode, factoryCode);
    }

    [Fact]
    public void TypedDequeFactory_TypeCode_RoundTrips() {
        AssertRoundTrip(TypedDequeFactory<double>.TypeCode, typeof(DurableDeque<double>));
    }

    [Fact]
    public void TypedDequeFactory_TypeCode_MatchesHelperRegistry() {
        var factoryCode = TypedDequeFactory<double>.TypeCode;
        var registryCode = HelperRegistry.ResolveValueHelper(typeof(DurableDeque<double>)).TypeCode;
        Assert.Equal(registryCode, factoryCode);
    }

    // ═══════════════════════ 不支持的类型返回无效 ═══════════════════════

    [Fact]
    public void ResolveKeyHelper_UnsupportedType_ReturnsDefault() {
        var entry = HelperRegistry.ResolveKeyHelper(typeof(decimal));
        Assert.False(entry.IsValid);
        Assert.Null(entry.TypeCode);
    }

    [Fact]
    public void ResolveValueHelper_UnsupportedType_ReturnsDefault() {
        var entry = HelperRegistry.ResolveValueHelper(typeof(DurableObject));
        Assert.False(entry.IsValid);
        Assert.Null(entry.TypeCode);
    }
}
