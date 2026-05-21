using System.Collections.Immutable;
using System.Reflection;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.TextEditScript;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class LlmPlayerAgentDriverPromptTests {
    [Fact]
    public void BuildInitialObservation_RendersSchemaFromInputSchemaWhenFlatProjectionIsUnavailable() {
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

        var ex = Assert.Throws<InvalidOperationException>(() => ToolContracts.EnsureStableFlatProjection(definition, definition.Name));
        Assert.Contains("property 'payload' is not a flat value schema.", ex.Message, StringComparison.Ordinal);

        var observation = InvokeBuildInitialObservation(CreateMinimalPerception(), ImmutableArray.Create(definition));

        Assert.Contains("[当前可用原生工具 schema]", observation);
        Assert.Contains("- nested_tool: Render nested schema", observation);
        Assert.Contains("payload (必填, object): payload object", observation);
        Assert.Contains("note (必填, string): nested note", observation);
        Assert.Contains("comment (可省略, string): optional comment", observation);
    }

    private static string InvokeBuildInitialObservation(
        PerceptionBundle perception,
        ImmutableArray<ToolDefinition> toolDefinitions
    ) {
        var method = typeof(LlmPlayerAgentDriver).GetMethod("BuildInitialObservation", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [perception, toolDefinitions]));
    }

    private static PerceptionBundle CreateMinimalPerception() {
        return new PerceptionBundle(
            ActorId: "player",
            ActorKind: "player",
            ActorName: "测试者",
            ActorProfileNote: "正在检查 prompt。",
            Day: 1,
            Slot: 1,
            SlotsPerDay: 4,
            Location: new LocationPerception(
                LocationId: "room",
                Name: "测试房间",
                Description: "只有一张桌子。",
                Exits: [],
                Items: [],
                Actors: [],
                Interactions: []
            ),
            InventoryItems: [],
            NotebookBlocks: TextBlockSnapshotDocument.Empty,
            AcceptedSteps: [],
            LastResolution: null
        );
    }
}
