using System.Buffers;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

internal static class SessionEventCodec {
    private const int BodySchemaVersion = 1;
    private const string ToolResultBlockKindText = "text";
    private static readonly JsonWriterOptions WriterOptions = new() {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        SkipValidation = false
    };

    public static byte[] Encode(SessionEventKind kind, object body)
        => kind switch {
            SessionEventKind.SessionCreated => EncodeSessionConfiguration((SessionConfiguration)body),
            SessionEventKind.ObservationAccepted => EncodeObservationAccepted((ObservationAcceptedBody)body),
            SessionEventKind.AssistantActionProduced => EncodeAssistantActionProduced((AssistantActionProducedBody)body),
            SessionEventKind.ToolExecutionStarted => EncodeToolExecutionStarted((ToolExecutionStartedBody)body),
            SessionEventKind.ToolResultObserved => EncodeToolResultObserved((ToolResultObservedBody)body),
            _ => throw new NotSupportedException($"Session event kind '{kind}' is not implemented in Slice C.")
        };

    public static object Decode(SessionEventKind kind, ReadOnlySpan<byte> payload, out int bodySchemaVersion) {
        using var document = JsonDocument.Parse(payload.ToArray());
        JsonElement root = document.RootElement;
        RequireObject(root, "envelope");
        bodySchemaVersion = ReadRequiredInt32(root, "v");
        if (bodySchemaVersion != BodySchemaVersion) {
            throw new InvalidDataException($"Unsupported schema version {bodySchemaVersion} for session event kind '{kind}'.");
        }

        if (!root.TryGetProperty("body", out JsonElement body)) {
            throw new InvalidDataException("Session event envelope is missing required property 'body'.");
        }

        return kind switch {
            SessionEventKind.SessionCreated => DecodeSessionConfiguration(body),
            SessionEventKind.ObservationAccepted => DecodeObservationAccepted(body),
            SessionEventKind.AssistantActionProduced => DecodeAssistantActionProduced(body),
            SessionEventKind.ToolExecutionStarted => DecodeToolExecutionStarted(body),
            SessionEventKind.ToolResultObserved => DecodeToolResultObserved(body),
            _ => throw new NotSupportedException($"Session event kind '{kind}' is not implemented in Slice C.")
        };
    }

    private static byte[] EncodeSessionConfiguration(SessionConfiguration body) {
        ArgumentNullException.ThrowIfNull(body);
        ValidateRequired(body.ModelId, nameof(body.ModelId));
        ValidateRequired(body.CompletionSurfaceId, nameof(body.CompletionSurfaceId));
        ValidateRequired(body.Schema, nameof(body.Schema));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer);
            writer.WriteStartObject("body");
            writer.WriteString("modelId", body.ModelId);
            writer.WriteString("systemPrompt", body.SystemPrompt ?? string.Empty);
            writer.WriteString("completionSurfaceId", body.CompletionSurfaceId);
            writer.WriteString("schema", body.Schema);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeObservationAccepted(ObservationAcceptedBody body) {
        ArgumentNullException.ThrowIfNull(body);
        ValidateRequired(body.Content, nameof(body.Content));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer);
            writer.WriteStartObject("body");
            writer.WriteString("content", body.Content);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeAssistantActionProduced(AssistantActionProducedBody body) {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(body.Action);
        ArgumentNullException.ThrowIfNull(body.Invocation);

        var blocks = ActionMessageSerialization.ToSerializedBlocks(body.Action.Blocks);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer);
            writer.WriteStartObject("body");
            writer.WriteStartArray("action");
            foreach (var block in blocks) {
                WriteSerializedActionBlock(writer, block);
            }
            writer.WriteEndArray();
            writer.WriteStartObject("invocation");
            writer.WriteString("providerId", body.Invocation.ProviderId);
            writer.WriteString("apiSpecId", body.Invocation.ApiSpecId);
            writer.WriteString("model", body.Invocation.Model);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeToolExecutionStarted(ToolExecutionStartedBody body) {
        ArgumentNullException.ThrowIfNull(body);
        ValidateRequired(body.ToolCallId, nameof(body.ToolCallId));
        ValidateRequired(body.ToolName, nameof(body.ToolName));
        ValidateRequired(body.RawArgumentsJson, nameof(body.RawArgumentsJson));
        ValidateRequired(body.OperationId, nameof(body.OperationId));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer);
            writer.WriteStartObject("body");
            writer.WriteString("toolCallId", body.ToolCallId);
            writer.WriteString("toolName", body.ToolName);
            writer.WriteString("rawArgumentsJson", body.RawArgumentsJson);
            writer.WriteString("operationId", body.OperationId);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeToolResultObserved(ToolResultObservedBody body) {
        ArgumentNullException.ThrowIfNull(body);
        ValidateRequired(body.ToolCallId, nameof(body.ToolCallId));
        ValidateRequired(body.ToolName, nameof(body.ToolName));
        ArgumentNullException.ThrowIfNull(body.Blocks);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer);
            writer.WriteStartObject("body");
            writer.WriteString("toolCallId", body.ToolCallId);
            writer.WriteString("toolName", body.ToolName);
            writer.WriteString("status", WriteStatus(body.Status));
            writer.WriteStartArray("blocks");
            foreach (var block in body.Blocks) {
                WriteToolResultBlock(writer, block);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static SessionConfiguration DecodeSessionConfiguration(JsonElement body) {
        RequireObject(body, "session-created body");
        return new SessionConfiguration(
            ReadRequiredString(body, "modelId"),
            ReadRequiredString(body, "systemPrompt"),
            ReadRequiredString(body, "completionSurfaceId"),
            ReadRequiredString(body, "schema")
        );
    }

    private static ObservationAcceptedBody DecodeObservationAccepted(JsonElement body) {
        RequireObject(body, "observation-accepted body");
        return new ObservationAcceptedBody(ReadRequiredString(body, "content"));
    }

    private static AssistantActionProducedBody DecodeAssistantActionProduced(JsonElement body) {
        RequireObject(body, "assistant-action-produced body");
        if (!body.TryGetProperty("action", out JsonElement actionElement) || actionElement.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException("assistant-action-produced body requires array property 'action'.");
        }

        var blocks = new List<SerializedActionBlock>();
        foreach (JsonElement blockElement in actionElement.EnumerateArray()) {
            blocks.Add(ReadSerializedActionBlock(blockElement));
        }

        if (!body.TryGetProperty("invocation", out JsonElement invocationElement)) {
            throw new InvalidDataException("assistant-action-produced body requires object property 'invocation'.");
        }

        RequireObject(invocationElement, "invocation");
        var action = new ActionMessage(ActionMessageSerialization.FromSerializedBlocks(blocks));
        var invocation = new CompletionDescriptor(
            ReadRequiredString(invocationElement, "providerId"),
            ReadRequiredString(invocationElement, "apiSpecId"),
            ReadRequiredString(invocationElement, "model")
        );
        return new AssistantActionProducedBody(action, invocation);
    }

    private static ToolExecutionStartedBody DecodeToolExecutionStarted(JsonElement body) {
        RequireObject(body, "tool-execution-started body");
        return new ToolExecutionStartedBody(
            ReadRequiredString(body, "toolCallId"),
            ReadRequiredString(body, "toolName"),
            ReadRequiredString(body, "rawArgumentsJson"),
            ReadRequiredString(body, "operationId")
        );
    }

    private static ToolResultObservedBody DecodeToolResultObserved(JsonElement body) {
        RequireObject(body, "tool-result-observed body");
        if (!body.TryGetProperty("blocks", out JsonElement blocksElement) || blocksElement.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException("tool-result-observed body requires array property 'blocks'.");
        }

        var blocks = new List<ToolResultBlock>();
        foreach (JsonElement blockElement in blocksElement.EnumerateArray()) {
            blocks.Add(ReadToolResultBlock(blockElement));
        }

        return new ToolResultObservedBody(
            ReadRequiredString(body, "toolCallId"),
            ReadRequiredString(body, "toolName"),
            ReadStatus(ReadRequiredString(body, "status")),
            blocks
        );
    }

    private static void WriteEnvelopeStart(Utf8JsonWriter writer) {
        writer.WriteStartObject();
        writer.WriteNumber("v", BodySchemaVersion);
    }

    private static void WriteSerializedActionBlock(Utf8JsonWriter writer, SerializedActionBlock block) {
        writer.WriteStartObject();
        writer.WriteString("kind", block.Kind);
        if (block.Content is not null) { writer.WriteString("content", block.Content); }
        if (block.ToolName is not null) { writer.WriteString("toolName", block.ToolName); }
        if (block.ToolCallId is not null) { writer.WriteString("toolCallId", block.ToolCallId); }
        if (block.RawArgumentsJson is not null) { writer.WriteString("rawArgumentsJson", block.RawArgumentsJson); }
        if (block.Reasoning is not null) {
            writer.WriteStartObject("reasoning");
            writer.WriteString("codecId", block.Reasoning.CodecId);
            writer.WriteString("originProviderId", block.Reasoning.OriginProviderId);
            writer.WriteString("originApiSpecId", block.Reasoning.OriginApiSpecId);
            writer.WriteString("originModel", block.Reasoning.OriginModel);
            writer.WriteBase64String("payload", block.Reasoning.Payload);
            if (block.Reasoning.PlainTextForDebug is not null) { writer.WriteString("plainTextForDebug", block.Reasoning.PlainTextForDebug); }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static SerializedActionBlock ReadSerializedActionBlock(JsonElement block) {
        RequireObject(block, "action block");
        SerializedReasoningBlock? reasoning = null;
        if (block.TryGetProperty("reasoning", out JsonElement reasoningElement)) {
            RequireObject(reasoningElement, "reasoning");
            reasoning = new SerializedReasoningBlock(
                ReadRequiredString(reasoningElement, "codecId"),
                ReadRequiredString(reasoningElement, "originProviderId"),
                ReadRequiredString(reasoningElement, "originApiSpecId"),
                ReadRequiredString(reasoningElement, "originModel"),
                ReadRequiredBytes(reasoningElement, "payload"),
                ReadOptionalString(reasoningElement, "plainTextForDebug")
            );
        }

        return new SerializedActionBlock(
            ReadRequiredString(block, "kind"),
            ReadOptionalString(block, "content"),
            ReadOptionalString(block, "toolName"),
            ReadOptionalString(block, "toolCallId"),
            ReadOptionalString(block, "rawArgumentsJson"),
            reasoning
        );
    }

    private static void WriteToolResultBlock(Utf8JsonWriter writer, ToolResultBlock block) {
        writer.WriteStartObject();
        switch (block) {
            case ToolResultBlock.Text text:
                writer.WriteString("kind", ToolResultBlockKindText);
                writer.WriteString("content", text.Content);
                break;
            default:
                throw new InvalidOperationException($"Unsupported tool result block type '{block.GetType().FullName}'.");
        }
        writer.WriteEndObject();
    }

    private static ToolResultBlock ReadToolResultBlock(JsonElement block) {
        RequireObject(block, "tool result block");
        string kind = ReadRequiredString(block, "kind");
        return kind switch {
            ToolResultBlockKindText => new ToolResultBlock.Text(ReadRequiredString(block, "content")),
            _ => throw new InvalidDataException($"Unsupported tool result block kind '{kind}'.")
        };
    }

    private static string WriteStatus(ToolExecutionStatus status)
        => status switch {
            ToolExecutionStatus.Success => "success",
            ToolExecutionStatus.Failed => "failed",
            ToolExecutionStatus.Skipped => "skipped",
            _ => throw new InvalidOperationException($"Unsupported tool execution status '{status}'.")
        };

    private static ToolExecutionStatus ReadStatus(string value)
        => value switch {
            "success" => ToolExecutionStatus.Success,
            "failed" => ToolExecutionStatus.Failed,
            "skipped" => ToolExecutionStatus.Skipped,
            _ => throw new InvalidDataException($"Unsupported tool execution status '{value}'.")
        };

    private static void RequireObject(JsonElement element, string name) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException($"Expected {name} to be a JSON object.");
        }
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int value)) {
            throw new InvalidDataException($"Required numeric property '{propertyName}' is missing or invalid.");
        }
        return value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException($"Required string property '{propertyName}' is missing or invalid.");
        }
        return property.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property)) { return null; }
        if (property.ValueKind == JsonValueKind.Null) { return null; }
        if (property.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException($"Optional string property '{propertyName}' is invalid.");
        }
        return property.GetString();
    }

    private static byte[] ReadRequiredBytes(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException($"Required base64 property '{propertyName}' is missing or invalid.");
        }
        return property.GetBytesFromBase64();
    }

    private static void ValidateRequired(string value, string name) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException("Value must not be null, empty, or whitespace.", name);
        }
    }

    public static string ToUtf8String(byte[] payload) => Encoding.UTF8.GetString(payload);
}
