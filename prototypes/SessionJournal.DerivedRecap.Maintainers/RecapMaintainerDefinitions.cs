using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Abstractions;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers;

public abstract class RecapMaintainerOutputProtocol {
    protected RecapMaintainerOutputProtocol(
        string schemaId,
        CompletionOutputContract requestContract
    ) {
        SchemaId = string.IsNullOrWhiteSpace(schemaId)
            ? throw new ArgumentException(
                "Output protocol schema id cannot be empty.",
                nameof(schemaId)
            )
            : schemaId;
        RequestContract = requestContract
            ?? throw new ArgumentNullException(nameof(requestContract));
        SemanticFingerprint = RecapMaintainerSemanticFingerprint.Hash(
            writer => {
                writer.WriteStartObject();
                writer.WriteString(
                    "schema",
                    "atelia.session-journal.recap-output-protocol.v1"
                );
                writer.WriteString("parserSchema", SchemaId);
                writer.WriteString(
                    "completionOutputContract",
                    RequestContract.SemanticFingerprint
                );
                writer.WriteEndObject();
            }
        );
    }

    public string SchemaId { get; }

    public CompletionOutputContract RequestContract { get; }

    public string SemanticFingerprint { get; }

    public abstract RecapMaintenanceSuccess ParseAndValidate(
        CompletionResult result
    );
}

public sealed class StructuredRecapMaintainerOutputProtocol
    : RecapMaintainerOutputProtocol {
    public const string SubmitToolName = "recap.submit";
    public const string UpdatedOutcome = "updated";
    public const string KeepUnchangedOutcome = "keep-unchanged";

    private const string ProtocolSchema =
        "atelia.session-journal.recap-submit.v1";

    public static StructuredRecapMaintainerOutputProtocol Shared {
        get;
    } = new();

    private StructuredRecapMaintainerOutputProtocol()
        : base(ProtocolSchema, CreateRequestContract()) {
    }

    public override RecapMaintenanceSuccess ParseAndValidate(
        CompletionResult result
    ) {
        ArgumentNullException.ThrowIfNull(result);
        RawToolCall? submitCall = null;
        foreach (ActionBlock block in result.Message.Blocks) {
            switch (block) {
                case ActionBlock.ReasoningBlock:
                    break;
                case ActionBlock.Text:
                    throw Invalid(
                        "Structured recap output cannot contain text blocks."
                    );
                case ActionBlock.ToolCall toolCall:
                    if (submitCall is not null) {
                        throw Invalid(
                            "Structured recap output must contain exactly one tool call."
                        );
                    }
                    submitCall = toolCall.Call;
                    break;
                default:
                    throw Invalid(
                        $"Unsupported action block '{block.GetType().FullName}'."
                    );
            }
        }

        if (submitCall is null) {
            throw Invalid(
                "Structured recap output must contain exactly one tool call."
            );
        }
        if (!string.Equals(
                submitCall.ToolName,
                SubmitToolName,
                StringComparison.Ordinal
            )) {
            throw Invalid(
                $"Structured recap output called unexpected tool '{submitCall.ToolName}'."
            );
        }

        JsonDocument document;
        try {
            document = JsonDocument.Parse(
                submitCall.RawArgumentsJson,
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                }
            );
        }
        catch (JsonException error) {
            throw Invalid(
                "Structured recap output arguments are not strict JSON.",
                error
            );
        }

        using (document) {
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object) {
                throw Invalid(
                    "Structured recap output arguments must be an object."
                );
            }

            string? outcome = null;
            JsonElement content = default;
            bool hasContent = false;
            int propertyCount = 0;
            foreach (JsonProperty property in root.EnumerateObject()) {
                propertyCount++;
                switch (property.Name) {
                    case "outcome" when outcome is null:
                        if (property.Value.ValueKind
                            is not JsonValueKind.String) {
                            throw Invalid(
                                "Structured recap outcome must be a string."
                            );
                        }
                        outcome = property.Value.GetString();
                        break;
                    case "content" when !hasContent:
                        content = property.Value;
                        hasContent = true;
                        break;
                    case "outcome":
                    case "content":
                        throw Invalid(
                            $"Structured recap output contains duplicate property '{property.Name}'."
                        );
                    default:
                        throw Invalid(
                            $"Structured recap output contains unknown property '{property.Name}'."
                        );
                }
            }

            if (propertyCount != 2
                || outcome is null
                || !hasContent) {
                throw Invalid(
                    "Structured recap output requires exactly 'outcome' and 'content'."
                );
            }

            return outcome switch {
                UpdatedOutcome => ParseUpdated(content),
                KeepUnchangedOutcome => ParseKeepUnchanged(content),
                _ => throw Invalid(
                    $"Unsupported recap outcome '{outcome}'."
                )
            };
        }
    }

    private static RecapMaintenanceSuccess ParseUpdated(
        JsonElement content
    ) {
        if (content.ValueKind is not JsonValueKind.String) {
            throw Invalid(
                "Updated recap output requires string content."
            );
        }
        string value = content.GetString()!;
        if (string.IsNullOrWhiteSpace(value)) {
            throw Invalid(
                "Updated recap output content cannot be empty."
            );
        }
        return new RecapMaintenanceSuccess.Updated(value);
    }

    private static RecapMaintenanceSuccess ParseKeepUnchanged(
        JsonElement content
    ) {
        if (content.ValueKind is not JsonValueKind.Null) {
            throw Invalid(
                "Keep-unchanged recap output requires null content."
            );
        }
        return RecapMaintenanceSuccess.KeepUnchanged.Instance;
    }

    private static CompletionOutputContract CreateRequestContract() {
        var definition = new ToolDefinition(
            SubmitToolName,
            "Submit the maintained recap block or explicitly keep it unchanged.",
            new ToolSchema.Object([
                new ToolSchema.Property(
                    "outcome",
                    new ToolSchema.Value(
                        ToolParamType.String,
                        description:
                            "Whether the recap is updated or unchanged.",
                        stringEnumValues: [
                            UpdatedOutcome,
                            KeepUnchangedOutcome
                        ]
                    ),
                    isRequired: true
                ),
                new ToolSchema.Property(
                    "content",
                    new ToolSchema.Value(
                        ToolParamType.String,
                        isNullable: true,
                        description:
                            "New recap content for updated; null for keep-unchanged."
                    ),
                    isRequired: true
                )
            ])
        );
        // Auto is intentional: Anthropic extended thinking rejects a forced
        // tool choice, while some Gemini routes reject an explicit
        // allowParallelToolCalls=false. ParseAndValidate still fails closed
        // unless the model returns exactly one recap.submit call.
        return new CompletionOutputContract(
            [definition],
            CompletionToolChoice.Auto,
            allowParallelToolCalls: null
        );
    }

    private static InvalidDataException Invalid(
        string message,
        Exception? inner = null
    ) => new(message, inner);
}

public sealed class RecapMaintainerFamilyDefinition {
    public const string ContextProjectionSchema =
        "atelia.session-journal.recap-shared-context-projection.v1";

    public RecapMaintainerFamilyDefinition(
        string diagnosticName,
        string systemPrompt,
        RecapMaintainerOutputProtocol outputProtocol
    ) {
        DiagnosticName = string.IsNullOrWhiteSpace(diagnosticName)
            ? throw new ArgumentException(
                "Family diagnostic name cannot be empty.",
                nameof(diagnosticName)
            )
            : diagnosticName;
        SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? throw new ArgumentException(
                "Family system prompt cannot be empty.",
                nameof(systemPrompt)
            )
            : systemPrompt;
        OutputProtocol = outputProtocol
            ?? throw new ArgumentNullException(nameof(outputProtocol));
        SemanticFingerprint = RecapMaintainerSemanticFingerprint.Hash(
            writer => {
                writer.WriteStartObject();
                writer.WriteString(
                    "schema",
                    "atelia.session-journal.recap-family.v1"
                );
                writer.WriteString("systemPrompt", SystemPrompt);
                writer.WriteString(
                    "contextProjectionSchema",
                    ContextProjectionSchema
                );
                writer.WriteString(
                    "outputProtocol",
                    OutputProtocol.SemanticFingerprint
                );
                writer.WriteEndObject();
            }
        );
    }

    public string DiagnosticName { get; }

    public string SystemPrompt { get; }

    public RecapMaintainerOutputProtocol OutputProtocol { get; }

    public string SemanticFingerprint { get; }

    public CompletionPromptPrefix CreatePromptPrefix(
        RecapMaintenanceEpochInput epochInput
    ) {
        ArgumentNullException.ThrowIfNull(epochInput);
        return new CompletionPromptPrefix(
            SystemPrompt,
            OutputProtocol.RequestContract,
            RecapSharedContextProjector.Project(epochInput)
        );
    }
}

public sealed class RecapMaintainerDefinition {
    public RecapMaintainerDefinition(
        string implementationId,
        string maintainerId,
        ContextHeaderBlockPath target,
        RecapMaintainerFamilyDefinition family,
        string taskInstruction
    ) {
        ImplementationId = string.IsNullOrWhiteSpace(implementationId)
            ? throw new ArgumentException(
                "Maintainer implementation id cannot be empty.",
                nameof(implementationId)
            )
            : implementationId;
        MaintainerId = string.IsNullOrWhiteSpace(maintainerId)
            ? throw new ArgumentException(
                "Maintainer id cannot be empty.",
                nameof(maintainerId)
            )
            : maintainerId;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Family = family ?? throw new ArgumentNullException(nameof(family));
        TaskInstruction = string.IsNullOrWhiteSpace(taskInstruction)
            ? throw new ArgumentException(
                "Maintainer task instruction cannot be empty.",
                nameof(taskInstruction)
            )
            : taskInstruction;
        CapabilityFingerprint =
            RecapMaintainerCapabilityFingerprint.Compute(
                ImplementationId,
                MaintainerId,
                Target,
                Family.SemanticFingerprint,
                TaskInstruction
            );
    }

    public string ImplementationId { get; }

    public string MaintainerId { get; }

    public ContextHeaderBlockPath Target { get; }

    public RecapMaintainerFamilyDefinition Family { get; }

    public string TaskInstruction { get; }

    public string CapabilityFingerprint { get; }

    public IReadOnlyList<IHistoryMessage> CreateTaskTailMessages()
        => [new ObservationMessage(
            "Maintain this recap block.\n"
                + "Target: "
                + ContextHeaderCarrierTokens.ToStorageToken(Target.Carrier)
                + "/"
                + Target.BlockKey
                + "\n\nInstruction:\n"
                + TaskInstruction
        )];
}

public static class RecapMaintainerImplementationIds {
    public const string StructuredRewrite =
        "atelia.session-journal.recap-maintainer.rewrite.v2";
}

public static class RecapMaintainerCapabilityFingerprint {
    public const string Schema =
        "atelia.session-journal.recap-maintainer-capability.v2";

    public static string Compute(
        string implementationId,
        string maintainerId,
        ContextHeaderBlockPath target,
        string familyFingerprint,
        string taskInstruction
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(implementationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(maintainerId);
        ArgumentNullException.ThrowIfNull(target);
        RecapMaintainerSemanticFingerprint.Require(
            familyFingerprint,
            nameof(familyFingerprint)
        );
        ArgumentNullException.ThrowIfNull(taskInstruction);
        return RecapMaintainerSemanticFingerprint.Hash(writer => {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("implementationId", implementationId);
            writer.WriteString("maintainerId", maintainerId);
            writer.WriteStartObject("target");
            writer.WriteString(
                "carrier",
                ContextHeaderCarrierTokens.ToStorageToken(target.Carrier)
            );
            writer.WriteString("blockKey", target.BlockKey);
            writer.WriteEndObject();
            writer.WriteString("family", familyFingerprint);
            writer.WriteString("taskInstruction", taskInstruction);
            writer.WriteEndObject();
        });
    }
}

internal static class RecapMaintainerSemanticFingerprint {
    private static readonly JsonWriterOptions WriterOptions = new() {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false
    };

    internal static string Hash(Action<Utf8JsonWriter> write) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            write(writer);
        }
        return "sha256:"
            + Convert.ToHexStringLower(
                SHA256.HashData(buffer.WrittenSpan)
            );
    }

    internal static void Require(
        string value,
        string parameterName
    ) {
        const string Prefix = "sha256:";
        if (value is null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length != Prefix.Length + 64
            || value.AsSpan(Prefix.Length).ContainsAnyExcept(
                "0123456789abcdef"
            )) {
            throw new ArgumentException(
                "Fingerprint must be sha256: followed by lowercase SHA-256 hex.",
                parameterName
            );
        }
    }
}

internal static class RecapSharedContextProjector {
    internal static IReadOnlyList<IHistoryMessage> Project(
        RecapMaintenanceEpochInput input
    ) {
        var messages = new List<IHistoryMessage>(
            input.HistoryMessages.Count + 6
        );
        AddPriorContext(messages, input.PriorContext);
        AddHistory(messages, input.HistoryMessages);
        return messages;
    }

    private static void AddPriorContext(
        List<IHistoryMessage> destination,
        ContextHeaderSnapshot prior
    ) {
        if (!string.IsNullOrWhiteSpace(prior.SystemPromptFragment)) {
            destination.Add(
                new ObservationMessage(prior.SystemPromptFragment)
            );
        }
        if (!string.IsNullOrWhiteSpace(prior.ObservationMessage)) {
            destination.Add(
                new ObservationMessage(prior.ObservationMessage)
            );
        }
        if (!string.IsNullOrWhiteSpace(prior.ActionMessage)) {
            destination.Add(
                new ActionMessage([
                    new ActionBlock.Text(prior.ActionMessage)
                ])
            );
        }
    }

    private static void AddHistory(
        List<IHistoryMessage> destination,
        IReadOnlyList<IHistoryMessage> messages
    ) {
        foreach (IHistoryMessage original in messages) {
            switch (original.Kind) {
                case HistoryMessageKind.ContextHeader:
                    var header = original as SessionContextHeader
                        ?? throw new InvalidOperationException(
                            $"Recap family received unsupported context header type '{original.GetType().FullName}'."
                        );
                    AddPriorContext(
                        destination,
                        ContextHeaderSnapshot.FromSessionContextHeader(
                            header
                        )
                    );
                    break;
                case HistoryMessageKind.Action:
                    destination.Add(
                        StripReasoningBlocks((ActionMessage)original)
                    );
                    break;
                case HistoryMessageKind.Observation:
                case HistoryMessageKind.ToolResults:
                    destination.Add(original);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Recap family received unknown history kind '{original.Kind}'."
                    );
            }
        }
    }

    private static ActionMessage StripReasoningBlocks(
        ActionMessage action
    ) {
        var filtered = new List<ActionBlock>(action.Blocks.Count);
        foreach (ActionBlock block in action.Blocks) {
            switch (block) {
                case ActionBlock.Text text:
                    string visibleText = InlineThinkTextFilter
                        .StripInlineThinkBlocks(text.Content);
                    if (!string.IsNullOrEmpty(visibleText)) {
                        filtered.Add(new ActionBlock.Text(visibleText));
                    }
                    break;
                case ActionBlock.ToolCall:
                    filtered.Add(block);
                    break;
                case ActionBlock.ReasoningBlock:
                    break;
            }
        }
        return new ActionMessage(filtered);
    }
}
