using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

internal static class SessionRequestCanonicalizer {
    internal static readonly JsonWriterOptions WriterOptions = new() {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false
    };

    public static byte[] Canonicalize(CompletionRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteString("modelId", request.ModelId);
            writer.WriteString("systemPrompt", request.SystemPrompt);
            writer.WriteStartArray("context");
            foreach (IHistoryMessage message in request.Context) {
                WriteHistoryMessage(writer, message);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("tools");
            foreach (ToolDefinition definition in request.Tools) {
                WriteToolDefinition(writer, definition);
            }
            writer.WriteEndArray();
            if (request.MaxTokens is int maxTokens) {
                writer.WriteNumber("maxTokens", maxTokens);
            }
            else {
                writer.WriteNull("maxTokens");
            }
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    public static byte[] CanonicalizeTools(ImmutableArray<ToolDefinition> tools) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartArray();
            foreach (ToolDefinition definition in tools) {
                WriteToolDefinition(writer, definition);
            }
            writer.WriteEndArray();
        }

        return buffer.WrittenMemory.ToArray();
    }

    public static SessionRequestCommitment CreateCommitment(CompletionRequest request) {
        byte[] bytes = Canonicalize(request);
        return new SessionRequestCommitment(
            bytes.Length,
            Sha256Hex(bytes)
        );
    }

    public static string ComputeToolSetSha256(ImmutableArray<ToolDefinition> tools)
        => Sha256Hex(CanonicalizeTools(tools));

    internal static string Sha256Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void WriteToolDefinition(Utf8JsonWriter writer, ToolDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);
        writer.WriteStartObject();
        writer.WriteString("name", definition.Name);
        writer.WriteString("description", definition.Description);
        writer.WritePropertyName("inputSchema");
        WriteToolSchema(writer, definition.InputSchema);
        writer.WriteEndObject();
    }

    internal static ToolDefinition ReadToolDefinition(JsonElement element) {
        RequireExactProperties(element, "tool definition", "name", "description", "inputSchema");
        return new ToolDefinition(
            ReadRequiredString(element, "name"),
            ReadRequiredString(element, "description"),
            ReadToolSchema(ReadRequiredProperty(element, "inputSchema"))
        );
    }

    private static void WriteHistoryMessage(Utf8JsonWriter writer, IHistoryMessage message) {
        ArgumentNullException.ThrowIfNull(message);
        writer.WriteStartObject();
        switch (message) {
            case ToolResultsMessage toolResults when message.Kind == HistoryMessageKind.ToolResults:
                writer.WriteString("kind", "tool-results");
                WriteNullableString(writer, "content", toolResults.Content);
                writer.WriteStartArray("results");
                foreach (ToolResult result in toolResults.Results) {
                    WriteToolResult(writer, result);
                }
                writer.WriteEndArray();
                break;
            case ObservationMessage observation
                when message.Kind == HistoryMessageKind.Observation &&
                     observation.GetType() == typeof(ObservationMessage):
                writer.WriteString("kind", "observation");
                WriteNullableString(writer, "content", observation.Content);
                break;
            case ActionMessage action when message.Kind == HistoryMessageKind.Action:
                writer.WriteString("kind", "action");
                writer.WriteStartArray("blocks");
                foreach (SerializedActionBlock block in ActionMessageSerialization.ToSerializedBlocks(action.Blocks)) {
                    WriteActionBlock(writer, block);
                }
                writer.WriteEndArray();
                break;
            case { Kind: HistoryMessageKind.ContextHeader }:
                throw new InvalidOperationException("ContextHeader is not provider-facing and cannot be canonicalized.");
            default:
                throw new InvalidOperationException(
                    $"Unsupported history message type '{message.GetType().FullName}' with kind '{message.Kind}'."
                );
        }
        writer.WriteEndObject();
    }

    private static void WriteActionBlock(Utf8JsonWriter writer, SerializedActionBlock block) {
        writer.WriteStartObject();
        switch (block.Kind) {
            case ActionMessageSerialization.BlockKindText when block.Content is not null:
                writer.WriteString("kind", "text");
                writer.WriteString("content", block.Content);
                break;
            case ActionMessageSerialization.BlockKindToolCall
                when block.ToolName is not null && block.ToolCallId is not null:
                writer.WriteString("kind", "tool-call");
                writer.WriteString("toolName", block.ToolName);
                writer.WriteString("toolCallId", block.ToolCallId);
                writer.WriteString("rawArgumentsJson", block.RawArgumentsJson ?? "{}");
                break;
            case ActionMessageSerialization.BlockKindReasoning when block.Reasoning is not null:
                writer.WriteString("kind", "reasoning");
                writer.WriteString("codecId", block.Reasoning.CodecId);
                writer.WriteString("originProviderId", block.Reasoning.OriginProviderId);
                writer.WriteString("originApiSpecId", block.Reasoning.OriginApiSpecId);
                writer.WriteString("originModel", block.Reasoning.OriginModel);
                writer.WriteBase64String("payload", block.Reasoning.Payload);
                WriteNullableString(writer, "plainTextForDebug", block.Reasoning.PlainTextForDebug);
                break;
            default:
                throw new InvalidOperationException($"Unsupported serialized action block '{block.Kind}'.");
        }
        writer.WriteEndObject();
    }

    private static void WriteToolResult(Utf8JsonWriter writer, ToolResult result) {
        ArgumentNullException.ThrowIfNull(result);
        writer.WriteStartObject();
        writer.WriteString("toolName", result.ToolName);
        writer.WriteString("toolCallId", result.ToolCallId);
        writer.WriteString("status", WriteToolExecutionStatus(result.Status));
        writer.WriteStartArray("blocks");
        foreach (ToolResultBlock block in result.Blocks) {
            writer.WriteStartObject();
            switch (block) {
                case ToolResultBlock.Text text:
                    writer.WriteString("kind", "text");
                    writer.WriteString("content", text.Content);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported tool result block '{block.GetType().FullName}'.");
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string WriteToolExecutionStatus(ToolExecutionStatus status)
        => status switch {
            ToolExecutionStatus.Success => "success",
            ToolExecutionStatus.Failed => "failed",
            ToolExecutionStatus.Skipped => "skipped",
            _ => throw new InvalidOperationException($"Unsupported tool execution status '{status}'.")
        };

    private static void WriteToolSchema(Utf8JsonWriter writer, ToolSchema schema) {
        ArgumentNullException.ThrowIfNull(schema);
        writer.WriteStartObject();
        switch (schema) {
            case ToolSchema.Object objectSchema:
                writer.WriteString("kind", "object");
                WriteNullableString(writer, "description", objectSchema.Description);
                WriteNullableString(writer, "example", objectSchema.Example);
                writer.WriteBoolean("additionalProperties", objectSchema.AdditionalProperties);
                writer.WriteStartArray("properties");
                foreach (ToolSchema.Property property in objectSchema.Properties) {
                    writer.WriteStartObject();
                    writer.WriteString("name", property.Name);
                    writer.WriteBoolean("required", property.IsRequired);
                    writer.WritePropertyName("schema");
                    WriteToolSchema(writer, property.Schema);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                break;
            case ToolSchema.Array arraySchema:
                writer.WriteString("kind", "array");
                WriteNullableString(writer, "description", arraySchema.Description);
                WriteNullableString(writer, "example", arraySchema.Example);
                writer.WriteBoolean("nullable", arraySchema.IsNullable);
                writer.WritePropertyName("items");
                WriteToolSchema(writer, arraySchema.ItemSchema);
                break;
            case ToolSchema.Value valueSchema:
                writer.WriteString("kind", "value");
                WriteNullableString(writer, "description", valueSchema.Description);
                WriteNullableString(writer, "example", valueSchema.Example);
                writer.WriteString("valueKind", WriteToolParamType(valueSchema.ValueKind));
                writer.WriteBoolean("nullable", valueSchema.IsNullable);
                if (valueSchema.Default.HasValue) {
                    writer.WritePropertyName("default");
                    WriteTypedValue(writer, valueSchema.ValueKind, valueSchema.Default.Value.Value);
                }
                writer.WriteStartArray("stringEnumValues");
                foreach (string item in valueSchema.StringEnumValues) {
                    writer.WriteStringValue(item);
                }
                writer.WriteEndArray();
                WriteNullableNumber(writer, "minLength", valueSchema.MinLength);
                WriteNullableNumber(writer, "maxLength", valueSchema.MaxLength);
                WriteNullableString(writer, "pattern", valueSchema.Pattern);
                if (valueSchema.Minimum is not null) {
                    writer.WritePropertyName("minimum");
                    WriteTypedValue(writer, valueSchema.ValueKind, valueSchema.Minimum);
                }
                if (valueSchema.Maximum is not null) {
                    writer.WritePropertyName("maximum");
                    WriteTypedValue(writer, valueSchema.ValueKind, valueSchema.Maximum);
                }
                break;
            default:
                throw new InvalidOperationException($"Unsupported tool schema type '{schema.GetType().FullName}'.");
        }
        writer.WriteEndObject();
    }

    private static ToolSchema ReadToolSchema(JsonElement element) {
        RequireObject(element, "tool schema");
        RequireNoDuplicateProperties(element, "tool schema");
        string kind = ReadRequiredString(element, "kind");
        switch (kind) {
            case "object":
                RequireExactProperties(
                    element,
                    "object tool schema",
                    "kind",
                    "description",
                    "example",
                    "additionalProperties",
                    "properties"
                );
                break;
            case "array":
                RequireExactProperties(
                    element,
                    "array tool schema",
                    "kind",
                    "description",
                    "example",
                    "nullable",
                    "items"
                );
                break;
            case "value":
                RequireExactProperties(
                    element,
                    "value tool schema",
                    "kind",
                    "description",
                    "example",
                    "valueKind",
                    "nullable",
                    "default",
                    "stringEnumValues",
                    "minLength",
                    "maxLength",
                    "pattern",
                    "minimum",
                    "maximum"
                );
                break;
            default:
                throw new InvalidDataException($"Unsupported tool schema kind '{kind}'.");
        }
        string? description = ReadNullableString(element, "description");
        string? example = ReadNullableString(element, "example");
        return kind switch {
            "object" => ReadObjectSchema(element, description, example),
            "array" => new ToolSchema.Array(
                ReadToolSchema(ReadRequiredProperty(element, "items")),
                ReadRequiredBoolean(element, "nullable"),
                description,
                example
            ),
            "value" => ReadValueSchema(element, description, example),
            string unsupportedKind => throw new InvalidDataException($"Unsupported tool schema kind '{unsupportedKind}'.")
        };
    }

    private static ToolSchema.Object ReadObjectSchema(JsonElement element, string? description, string? example) {
        JsonElement propertiesElement = ReadRequiredProperty(element, "properties");
        if (propertiesElement.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException("Tool object schema 'properties' must be an array.");
        }

        var properties = new List<ToolSchema.Property>();
        foreach (JsonElement property in propertiesElement.EnumerateArray()) {
            RequireExactProperties(property, "tool schema property", "name", "required", "schema");
            properties.Add(new ToolSchema.Property(
                ReadRequiredString(property, "name"),
                ReadToolSchema(ReadRequiredProperty(property, "schema")),
                ReadRequiredBoolean(property, "required")
            ));
        }

        return new ToolSchema.Object(
            properties,
            ReadRequiredBoolean(element, "additionalProperties"),
            description,
            example
        );
    }

    private static ToolSchema.Value ReadValueSchema(JsonElement element, string? description, string? example) {
        ToolParamType valueKind = ReadToolParamType(ReadRequiredString(element, "valueKind"));
        ParamDefault? defaultValue = element.TryGetProperty("default", out JsonElement defaultElement)
            ? new ParamDefault(ReadTypedValue(defaultElement, valueKind))
            : null;
        JsonElement enumElement = ReadRequiredProperty(element, "stringEnumValues");
        if (enumElement.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException("Tool value schema 'stringEnumValues' must be an array.");
        }
        string[] enumValues = enumElement.EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new InvalidDataException("Tool string enum values must be strings."))
            .ToArray();

        return new ToolSchema.Value(
            valueKind,
            ReadRequiredBoolean(element, "nullable"),
            defaultValue,
            description,
            example,
            enumValues.Length == 0 ? null : enumValues,
            ReadNullableInt32(element, "minLength"),
            ReadNullableInt32(element, "maxLength"),
            ReadNullableString(element, "pattern"),
            element.TryGetProperty("minimum", out JsonElement minElement) ? ReadTypedValue(minElement, valueKind) : null,
            element.TryGetProperty("maximum", out JsonElement maxElement) ? ReadTypedValue(maxElement, valueKind) : null
        );
    }

    private static void WriteTypedValue(Utf8JsonWriter writer, ToolParamType kind, object? value) {
        if (value is null) {
            writer.WriteNullValue();
            return;
        }

        switch (kind) {
            case ToolParamType.String:
                writer.WriteStringValue((string)value);
                break;
            case ToolParamType.Boolean:
                writer.WriteBooleanValue((bool)value);
                break;
            case ToolParamType.Int32:
                writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case ToolParamType.Int64:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ToolParamType.Float32:
                writer.WriteNumberValue((float)value);
                break;
            case ToolParamType.Float64:
                writer.WriteNumberValue((double)value);
                break;
            case ToolParamType.Decimal:
                writer.WriteNumberValue((decimal)value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported tool value kind '{kind}'.");
        }
    }

    private static object? ReadTypedValue(JsonElement element, ToolParamType kind) {
        if (element.ValueKind == JsonValueKind.Null) { return null; }
        return kind switch {
            ToolParamType.String when element.ValueKind == JsonValueKind.String => element.GetString()!,
            ToolParamType.Boolean when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            ToolParamType.Int32 when element.TryGetInt32(out int value) => value,
            ToolParamType.Int64 when element.TryGetInt64(out long value) => value,
            ToolParamType.Float32 when element.TryGetSingle(out float value) => value,
            ToolParamType.Float64 when element.TryGetDouble(out double value) => value,
            ToolParamType.Decimal when element.TryGetDecimal(out decimal value) => value,
            _ => throw new InvalidDataException($"JSON value does not match tool value kind '{kind}'.")
        };
    }

    private static string WriteToolParamType(ToolParamType kind)
        => kind switch {
            ToolParamType.String => "string",
            ToolParamType.Boolean => "boolean",
            ToolParamType.Int32 => "int32",
            ToolParamType.Int64 => "int64",
            ToolParamType.Float32 => "float32",
            ToolParamType.Float64 => "float64",
            ToolParamType.Decimal => "decimal",
            _ => throw new InvalidOperationException($"Unsupported tool parameter type '{kind}'.")
        };

    private static ToolParamType ReadToolParamType(string kind)
        => kind switch {
            "string" => ToolParamType.String,
            "boolean" => ToolParamType.Boolean,
            "int32" => ToolParamType.Int32,
            "int64" => ToolParamType.Int64,
            "float32" => ToolParamType.Float32,
            "float64" => ToolParamType.Float64,
            "decimal" => ToolParamType.Decimal,
            _ => throw new InvalidDataException($"Unsupported tool parameter type '{kind}'.")
        };

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value) {
        if (value is null) {
            writer.WriteNull(propertyName);
        }
        else {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string propertyName, int? value) {
        if (value is int number) {
            writer.WriteNumber(propertyName, number);
        }
        else {
            writer.WriteNull(propertyName);
        }
    }

    private static JsonElement ReadRequiredProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement property)
            ? property
            : throw new InvalidDataException($"Required property '{propertyName}' is missing.");

    private static string ReadRequiredString(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new InvalidDataException($"Required string property '{propertyName}' is invalid.");
    }

    private static string? ReadNullableString(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        return property.ValueKind switch {
            JsonValueKind.Null => null,
            JsonValueKind.String => property.GetString(),
            _ => throw new InvalidDataException($"Nullable string property '{propertyName}' is invalid.")
        };
    }

    private static bool ReadRequiredBoolean(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        return property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : throw new InvalidDataException($"Required boolean property '{propertyName}' is invalid.");
    }

    private static int? ReadNullableInt32(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        if (property.ValueKind == JsonValueKind.Null) { return null; }
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int value)
            ? value
            : throw new InvalidDataException($"Nullable integer property '{propertyName}' is invalid.");
    }

    private static void RequireObject(JsonElement element, string name) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException($"Expected {name} to be a JSON object.");
        }
    }

    private static void RequireNoDuplicateProperties(JsonElement element, string name) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (!seen.Add(property.Name)) {
                throw new InvalidDataException($"{name} contains duplicate property '{property.Name}'.");
            }
        }
    }

    private static void RequireExactProperties(JsonElement element, string name, params string[] allowedProperties) {
        RequireObject(element, name);
        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (!seen.Add(property.Name)) {
                throw new InvalidDataException($"{name} contains duplicate property '{property.Name}'.");
            }
            if (!allowed.Contains(property.Name)) {
                throw new InvalidDataException($"{name} contains unknown property '{property.Name}'.");
            }
        }
    }
}
