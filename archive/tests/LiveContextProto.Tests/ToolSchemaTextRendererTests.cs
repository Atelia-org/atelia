using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class ToolSchemaTextRendererTests {
    [Fact]
    public void RenderDefinition_RendersNestedAndNonFlatPropertiesFromInputSchema() {
        var definition = new ToolDefinition(
            "nested_tool",
            "Render nested schema",
            new ToolSchema.Object(
                [
                    new ToolSchema.Property(
                        "payload",
                        new ToolSchema.Object(
                            [
                                new ToolSchema.Property(
                                    "note",
                                    new ToolSchema.Value(ToolParamType.String, description: "nested note"),
                                    isRequired: true
                                )
                            ],
                            description: "payload object"
                        ),
                        isRequired: true
                    ),
                    new ToolSchema.Property(
                        "comment",
                        new ToolSchema.Value(ToolParamType.String, description: "optional comment"),
                        isRequired: false
                    )
                ]
            )
        );

        var rendered = ToolSchemaTextRenderer.RenderDefinition(definition);

        Assert.Contains("- nested_tool: Render nested schema", rendered);
        Assert.Contains("payload (必填, object): payload object", rendered);
        Assert.Contains("note (必填, string): nested note", rendered);
        Assert.Contains("comment (可省略, string): optional comment", rendered);
    }
}
