using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

/// <summary>
/// 容器能力矩阵：对所有容器类型系统性验证
/// HelperRegistry → TypeCodec → DurableFactory 的完整管线。
/// 新增容器类型时只需在 <see cref="AllContainerTypes"/> 添加一行。
/// </summary>
public class ContainerCapabilityMatrixTests {

    public static TheoryData<Type, DurableObjectKind> AllContainerTypes => new() {
        { typeof(DurableDict<int, double>), DurableObjectKind.TypedDict },
        { typeof(DurableDict<int>), DurableObjectKind.MixedDict },
        { typeof(DurableDeque), DurableObjectKind.MixedDeque },
        { typeof(DurableDeque<int>), DurableObjectKind.TypedDeque },
        { typeof(DurableOrderedDict<int, double>), DurableObjectKind.TypedOrderedDict },
        { typeof(DurableOrderedDict<int>), DurableObjectKind.MixedOrderedDict },
    };

    // 使用 string key 的变体，确保引用类型 key 同样走通全管线
    public static TheoryData<Type, DurableObjectKind> StringKeyContainerTypes => new() {
        { typeof(DurableDict<string, int>), DurableObjectKind.TypedDict },
        { typeof(DurableDict<string>), DurableObjectKind.MixedDict },
        { typeof(DurableOrderedDict<string, int>), DurableObjectKind.TypedOrderedDict },
        { typeof(DurableOrderedDict<string>), DurableObjectKind.MixedOrderedDict },
    };

    // ═══════════ HelperRegistry.ResolveValueHelper → valid TypeEntry ═══════════

    [Theory]
    [MemberData(nameof(AllContainerTypes))]
    public void ResolveValueHelper_ReturnsValidEntry(Type containerType, DurableObjectKind _) {
        var entry = HelperRegistry.ResolveValueHelper(containerType);
        Assert.True(entry.IsValid, $"ResolveValueHelper returned invalid entry for {containerType}");
    }

    [Theory]
    [MemberData(nameof(StringKeyContainerTypes))]
    public void ResolveValueHelper_StringKey_ReturnsValidEntry(Type containerType, DurableObjectKind _) {
        var entry = HelperRegistry.ResolveValueHelper(containerType);
        Assert.True(entry.IsValid, $"ResolveValueHelper returned invalid entry for {containerType}");
    }

    // ═══════════ TypeCodec round-trip ═══════════

    [Theory]
    [MemberData(nameof(AllContainerTypes))]
    public void TypeCode_RoundTrips(Type containerType, DurableObjectKind _) {
        var entry = HelperRegistry.ResolveValueHelper(containerType);
        Assert.True(entry.IsValid);
        Assert.True(TypeCodec.TryDecode(entry.TypeCode, out var decoded),
            $"TypeCodec.TryDecode failed for {containerType}");
        Assert.Equal(containerType, decoded);
    }

    [Theory]
    [MemberData(nameof(StringKeyContainerTypes))]
    public void TypeCode_StringKey_RoundTrips(Type containerType, DurableObjectKind _) {
        var entry = HelperRegistry.ResolveValueHelper(containerType);
        Assert.True(entry.IsValid);
        Assert.True(TypeCodec.TryDecode(entry.TypeCode, out var decoded),
            $"TypeCodec.TryDecode failed for {containerType}");
        Assert.Equal(containerType, decoded);
    }

    // ═══════════ DurableFactory round-trip ═══════════

    [Theory]
    [MemberData(nameof(AllContainerTypes))]
    public void DurableFactory_Creates_CorrectKind(Type containerType, DurableObjectKind expectedKind) {
        Assert.True(DurableFactory.TryCreate(containerType, out var obj),
            $"DurableFactory.TryCreate failed for {containerType}");
        Assert.Equal(expectedKind, obj!.Kind);
    }

    [Theory]
    [MemberData(nameof(StringKeyContainerTypes))]
    public void DurableFactory_StringKey_Creates_CorrectKind(Type containerType, DurableObjectKind expectedKind) {
        Assert.True(DurableFactory.TryCreate(containerType, out var obj),
            $"DurableFactory.TryCreate failed for {containerType}");
        Assert.Equal(expectedKind, obj!.Kind);
    }

    // ═══════════ Full pipeline: Resolve → Decode → Create → Kind ═══════════

    [Theory]
    [MemberData(nameof(AllContainerTypes))]
    public void FullPipeline_ResolveDecodeCreateKind(Type containerType, DurableObjectKind expectedKind) {
        // 1. HelperRegistry → TypeEntry
        var entry = HelperRegistry.ResolveValueHelper(containerType);
        Assert.True(entry.IsValid, $"Step 1 (ResolveValueHelper) failed for {containerType}");

        // 2. TypeCodec → round-trip Type
        Assert.True(TypeCodec.TryDecode(entry.TypeCode, out var decodedType),
            $"Step 2 (TypeCodec.TryDecode) failed for {containerType}");
        Assert.Equal(containerType, decodedType);

        // 3. DurableFactory → create instance from decoded type
        Assert.True(DurableFactory.TryCreate(decodedType!, out var obj),
            $"Step 3 (DurableFactory.TryCreate) failed for decoded type {decodedType}");

        // 4. Kind matches
        Assert.Equal(expectedKind, obj!.Kind);
    }
}
