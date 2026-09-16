using System.Buffers;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal static class RuntimeRenderer {
    internal static IHistoryMessage RenderPrior(
        IReadOnlyList<RecapCellArtifact> orderedCells,
        IReadOnlyDictionary<MaintainerDefinitionDigest,
            MaintainerDefinitionRevision> definitions
    ) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString(
                "schema",
                RecapRewriterProtocolV3.PriorProjectionSchemaId
            );
            writer.WritePropertyName("columns");
            writer.WriteStartArray();
            foreach (RecapCellArtifact cell in orderedCells) {
                writer.WriteStartObject();
                writer.WriteString(
                    "logicalColumnId",
                    cell.LogicalColumnId.Value
                );
                if (definitions.TryGetValue(
                        cell.DefinitionDigest,
                        out MaintainerDefinitionRevision? definition)) {
                    writer.WriteString(
                        "semanticHeading",
                        definition.Target.SemanticHeading
                    );
                    writer.WriteString("carrier", definition.Target.Carrier.ToString());
                    writer.WriteString("blockKey", definition.Target.BlockKey);
                }
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
                case SessionInputObservationMessage observation:
                    // Content selection is semantic. The host projects this
                    // record only at the individual completion boundary.
                    visible.Add(observation);
                    break;
                case SessionTurnEndedMessage ended:
                    visible.Add(ended);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"History message subtype '{unit.Message?.GetType().FullName}' is unsupported."
                    );
            }
        }
        return visible;
    }

    internal static CompletionRequest ProjectRequest(
        CompletionRequest semanticRequest,
        ISessionInputProjector? projector
    ) {
        if (!semanticRequest.PromptPrefix.SharedContextMessages.Any(static message => message is SessionInputObservationMessage or SessionTurnEndedMessage)
            && !semanticRequest.TailMessages.Any(static message => message is SessionInputObservationMessage or SessionTurnEndedMessage)) {
            return semanticRequest;
        }
        return new(
            semanticRequest.ModelId,
            new CompletionPromptPrefix(
                semanticRequest.PromptPrefix.SystemPrompt,
                semanticRequest.PromptPrefix.OutputContract,
                semanticRequest.PromptPrefix.SharedContextMessages
                    .Select(message => ProjectInput(message, projector)).ToArray()
            ),
            semanticRequest.TailMessages
                .Select(message => ProjectInput(message, projector)).ToArray()
        );
    }

    private static IHistoryMessage ProjectInput(
        IHistoryMessage message,
        ISessionInputProjector? projector
    ) => message switch {
        SessionInputObservationMessage observation => new ObservationMessage(observation.Content.IsStructured
            ? (projector ?? throw new NotSupportedException(
                "Structured recap history requires a host input projector."
            )).Project(observation.Content)
            : observation.Content.TextValue),
        SessionTurnEndedMessage ended => new ObservationMessage(ended.Render()),
        _ => message
    };

    internal static IHistoryMessage RenderWorkTail(FrozenRecapCellWork work) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString(
                "schema",
                RecapRewriterProtocolV3.InputProtocolId
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
            // atelia.recap.input.v1 is frozen: provider-facing semanticHeading
            // belongs to pre-Prepared request rendering, not maintainer work input.
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
