using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class ToolContractsTests {
    [Fact]
    public void CreateCompatibleFlatOverride_AllowsDisplayMetadataChangesOnly() {
        var authoritative = new ToolDefinition(
            "search",
            "authoritative",
            new ToolSchema.Object(
                [
                    new ToolSchema.Property(
                        "mode",
                        new ToolSchema.Value(
                            ToolParamType.String,
                            description: "authoritative mode",
                            example: "fast",
                            stringEnumValues: ["fast", "safe"]
                        ),
                        isRequired: true
                    )
                ]
            )
        );

        var metadataOverride = new ToolDefinition(
            "search",
            "override description",
            new ToolSchema.Object(
                [
                    new ToolSchema.Property(
                        "mode",
                        new ToolSchema.Value(
                            ToolParamType.String,
                            description: "override mode description",
                            example: "safe",
                            stringEnumValues: ["fast", "safe"]
                        ),
                        isRequired: true
                    )
                ]
            )
        );

        var result = ToolContracts.CreateCompatibleFlatOverride(authoritative, metadataOverride);

        Assert.Same(metadataOverride, result);
    }

    [Fact]
    public void CreateCompatibleFlatOverride_RejectsToolRename() {
        var authoritative = new ToolDefinition("search", "authoritative", new ToolSchema.Object());
        var metadataOverride = new ToolDefinition("search_override", "override description", new ToolSchema.Object());

        var ex = Assert.Throws<InvalidOperationException>(() => ToolContracts.CreateCompatibleFlatOverride(authoritative, metadataOverride));

        Assert.Contains("不能改名", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCompatibleFlatOverride_RejectsProviderVisibleSchemaChanges() {
        var authoritative = new ToolDefinition(
            "search",
            "authoritative",
            new ToolSchema.Object(
                [
                    new ToolSchema.Property(
                        "mode",
                        new ToolSchema.Value(
                            ToolParamType.String,
                            description: "mode",
                            stringEnumValues: ["fast", "safe"]
                        ),
                        isRequired: true
                    )
                ]
            )
        );

        var metadataOverride = new ToolDefinition(
            "search",
            "override description",
            new ToolSchema.Object(
                [
                    new ToolSchema.Property(
                        "mode",
                        new ToolSchema.Value(
                            ToolParamType.String,
                            description: "mode",
                            stringEnumValues: ["fast", "safe", "slow"]
                        ),
                        isRequired: true
                    )
                ]
            )
        );

        Assert.Throws<InvalidOperationException>(() => ToolContracts.CreateCompatibleFlatOverride(authoritative, metadataOverride));
    }
}
