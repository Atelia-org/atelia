using System.Buffers;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal static class RuntimeRenderer {
    internal static IHistoryMessage RenderPrior(
        IReadOnlyList<RecapCellArtifact> orderedCells
    ) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString(
                "schema",
                RecapRewriterProtocolV1.PriorProjectionSchemaId
            );
            writer.WritePropertyName("columns");
            writer.WriteStartArray();
            foreach (RecapCellArtifact cell in orderedCells) {
                writer.WriteStartObject();
                writer.WriteString(
                    "logicalColumnId",
                    cell.LogicalColumnId.Value
                );
                writer.WriteString("content", cell.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return new ObservationMessage(
            Encoding.UTF8.GetString(buffer.WrittenSpan)
        );
    }

    internal static IReadOnlyList<IHistoryMessage> ProjectHistory(
        SessionHistoryPlanningWindow window
    ) {
        ArgumentNullException.ThrowIfNull(window);
        var visible = new List<IHistoryMessage>(window.Units.Count);
        foreach (SessionHistoryPlanningUnit unit in window.Units) {
            switch (unit.Message) {
                case SessionContextHeader header:
                    if (header.SystemPromptFragment is not null) {
                        visible.Add(new ObservationMessage(
                            "[context.system]\n" + header.SystemPromptFragment
                        ));
                    }
                    if (header.ObservationMessage is not null) {
                        visible.Add(new ObservationMessage(
                            "[context.observation]\n"
                                + header.ObservationMessage
                        ));
                    }
                    if (header.ActionMessage is not null
                        && FilterAction(header.ActionMessage) is { } headerAction) {
                        visible.Add(headerAction);
                    }
                    break;
                case ActionMessage action:
                    if (FilterAction(action) is { } filtered) {
                        visible.Add(filtered);
                    }
                    break;
                case ToolResultsMessage toolResults:
                    visible.Add(toolResults);
                    break;
                case ObservationMessage observation:
                    visible.Add(observation);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"History message subtype '{unit.Message?.GetType().FullName}' is unsupported."
                    );
            }
        }
        return visible;
    }

    internal static IHistoryMessage RenderWorkTail(FrozenRecapCellWork work) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString(
                "schema",
                RecapRewriterProtocolV1.InputProtocolId
            );
            writer.WriteString(
                "logicalColumnId",
                work.LogicalColumnId.Value
            );
            writer.WriteString(
                "topic",
                work.Definition.DeclarativeSpec.Topic
            );
            writer.WriteString(
                "userPromptTemplate",
                work.Definition.DeclarativeSpec.UserPromptTemplate
            );
            writer.WritePropertyName("target");
            writer.WriteStartObject();
            writer.WriteString(
                "carrier",
                ContextHeaderCarrierTokens.ToStorageToken(
                    work.Definition.Target.Carrier
                )
            );
            writer.WriteString(
                "blockKey",
                work.Definition.Target.BlockKey
            );
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return new ObservationMessage(
            Encoding.UTF8.GetString(buffer.WrittenSpan)
        );
    }

    private static ActionMessage? FilterAction(ActionMessage source) {
        var blocks = new List<ActionBlock>(source.Blocks.Count);
        foreach (ActionBlock block in source.Blocks) {
            switch (block) {
                case ActionBlock.Text text:
                    string visible = InlineThinkTextFilter
                        .StripInlineThinkBlocks(text.Content);
                    if (visible.Length != 0) {
                        blocks.Add(new ActionBlock.Text(visible));
                    }
                    break;
                case ActionBlock.ToolCall toolCall:
                    blocks.Add(toolCall);
                    break;
                case ActionBlock.ReasoningBlock:
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Action block subtype '{block?.GetType().FullName}' is unsupported."
                    );
            }
        }
        return blocks.Count == 0 ? null : new ActionMessage(blocks);
    }
}
