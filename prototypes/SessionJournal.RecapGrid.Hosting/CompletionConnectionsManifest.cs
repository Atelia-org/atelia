using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;

namespace Atelia.SessionJournal.RecapGrid.Hosting;

public static class RecapGridCompletionConnectionsLimits {
    public const int MaximumInputUtf8Bytes = 1024 * 1024;
    public const int MaximumConnectionCount = 4_096;
    public const int MaximumIdentifierUtf8Bytes = 128;
    public const int MaximumEndpointUtf8Bytes = 4 * 1024;
    public const int MaximumSecretUtf8Bytes = 64 * 1024;
}

/// <summary>
/// Strict, bounded V1 reader for the candidate operator connection manifest.
/// The returned Completion configuration retains every supported field and is
/// defensively frozen before it reaches the lazy client registry.
/// </summary>
public static class RecapGridCompletionConnectionsManifest {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    public static CompletionConnectionsFileConfig Decode(
        ReadOnlySpan<byte> bytes
    ) {
        if (bytes.Length is < 1
            or > RecapGridCompletionConnectionsLimits.MaximumInputUtf8Bytes) {
            throw new InvalidDataException(
                "Completion connections bytes are empty or exceed the V1 bound."
            );
        }
        if (bytes.Length >= 3
            && bytes[0] == 0xef
            && bytes[1] == 0xbb
            && bytes[2] == 0xbf) {
            throw new InvalidDataException(
                "Completion connections bytes must not contain a UTF-8 BOM."
            );
        }
        try {
            _ = StrictUtf8.GetString(bytes);
            using JsonDocument document = JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                }
            );
            JsonElement root = document.RootElement;
            RequireProperties(
                root,
                required: ["connections"],
                optional: ["defaultConnectionId"]
            );
            JsonElement array = root.GetProperty("connections");
            if (array.ValueKind is not JsonValueKind.Array
                || array.GetArrayLength() is < 1
                    or > RecapGridCompletionConnectionsLimits
                        .MaximumConnectionCount) {
                throw new InvalidDataException(
                    "Completion connections count is outside the V1 bound."
                );
            }
            var connections = new List<CompletionConnectionConfig>(
                array.GetArrayLength()
            );
            foreach (JsonElement item in array.EnumerateArray()) {
                RequireProperties(
                    item,
                    required: [
                        "id", "kind", "modelId", "completionSurfaceId",
                        "baseAddress"
                    ],
                    optional: [
                        "apiKey", "baseAddressEnv", "apiKeyEnv", "maxTokens",
                        "reasoningEffort", "anthropicPromptCacheTtl"
                    ]
                );
                string? baseAddressEnv = OptionalString(
                    item,
                    "baseAddressEnv",
                    RecapGridCompletionConnectionsLimits
                        .MaximumIdentifierUtf8Bytes
                );
                string baseAddress = RequireStringAllowEmpty(
                    item,
                    "baseAddress",
                    RecapGridCompletionConnectionsLimits
                        .MaximumEndpointUtf8Bytes
                );
                if (string.IsNullOrWhiteSpace(baseAddress)
                    && baseAddressEnv is null) {
                    throw new InvalidDataException(
                        "baseAddress may be empty only when baseAddressEnv is set."
                    );
                }
                connections.Add(new CompletionConnectionConfig(
                    RequireString(item, "id",
                        RecapGridCompletionConnectionsLimits
                            .MaximumIdentifierUtf8Bytes),
                    RequireString(item, "kind",
                        RecapGridCompletionConnectionsLimits
                            .MaximumIdentifierUtf8Bytes),
                    RequireString(item, "modelId",
                        RecapGridCompletionConnectionsLimits
                            .MaximumIdentifierUtf8Bytes),
                    RequireString(item, "completionSurfaceId",
                        RecapGridCompletionConnectionsLimits
                            .MaximumIdentifierUtf8Bytes),
                    baseAddress,
                    OptionalString(item, "apiKey",
                        RecapGridCompletionConnectionsLimits
                            .MaximumSecretUtf8Bytes),
                    baseAddressEnv,
                    OptionalString(item, "apiKeyEnv",
                        RecapGridCompletionConnectionsLimits
                            .MaximumIdentifierUtf8Bytes),
                    OptionalPositiveInt32(item, "maxTokens"),
                    OptionalReasoningEffort(item),
                    OptionalAnthropicPromptCacheTtl(item)
                ));
            }
            string? defaultConnectionId = OptionalString(
                root,
                "defaultConnectionId",
                RecapGridCompletionConnectionsLimits.MaximumIdentifierUtf8Bytes
            );
            return Freeze(new CompletionConnectionsFileConfig(
                Array.AsReadOnly(connections.ToArray()),
                defaultConnectionId
            ));
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or DecoderFallbackException
                or ArgumentException
                or FormatException
                or InvalidOperationException
                or OverflowException) {
            throw new InvalidDataException(
                "Completion connections are not a valid bounded V1 manifest.",
                exception
            );
        }
    }

    internal static CompletionConnectionsFileConfig Freeze(
        CompletionConnectionsFileConfig config
    ) {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Connections is null) {
            throw new InvalidDataException(
                "Completion connections are absent."
            );
        }
        CompletionConnectionConfig[] input = config.Connections
            .Take(RecapGridCompletionConnectionsLimits.MaximumConnectionCount
                + 1)
            .ToArray();
        if (input.Length is < 1
            or > RecapGridCompletionConnectionsLimits.MaximumConnectionCount
            || input.Any(static item => item is null)) {
            throw new InvalidDataException(
                "Completion connections count is outside the V1 bound."
            );
        }
        try {
            CompletionConnectionsFileConfig normalized =
                CompletionConnectionConfigLoader.NormalizeAndValidate(
                    new CompletionConnectionsFileConfig(
                        Array.AsReadOnly(input),
                        config.DefaultConnectionId
                    )
                );
            CompletionConnectionConfig[] frozen = normalized.Connections
                .Select(ValidateResolved)
                .ToArray();
            RequireOptionalBounded(
                normalized.DefaultConnectionId,
                RecapGridCompletionConnectionsLimits
                    .MaximumIdentifierUtf8Bytes
            );
            return new CompletionConnectionsFileConfig(
                Array.AsReadOnly(frozen),
                normalized.DefaultConnectionId
            );
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (
            exception is DecoderFallbackException
                or ArgumentException
                or InvalidOperationException) {
            throw new InvalidDataException(
                "Completion connections are not a valid bounded V1 value.",
                exception
            );
        }
    }

    private static CompletionConnectionConfig ValidateResolved(
        CompletionConnectionConfig value
    ) {
        RequireBounded(value.Id,
            RecapGridCompletionConnectionsLimits.MaximumIdentifierUtf8Bytes);
        RequireBounded(value.Kind,
            RecapGridCompletionConnectionsLimits.MaximumIdentifierUtf8Bytes);
        RequireBounded(value.ModelId,
            RecapGridCompletionConnectionsLimits.MaximumIdentifierUtf8Bytes);
        RequireBounded(value.CompletionSurfaceId,
            RecapGridCompletionConnectionsLimits.MaximumIdentifierUtf8Bytes);
        RequireBounded(value.BaseAddress,
            RecapGridCompletionConnectionsLimits.MaximumEndpointUtf8Bytes);
        RequireOptionalBounded(value.ApiKey,
            RecapGridCompletionConnectionsLimits.MaximumSecretUtf8Bytes);
        RequireOptionalBounded(value.BaseAddressEnv,
            RecapGridCompletionConnectionsLimits.MaximumIdentifierUtf8Bytes);
        RequireOptionalBounded(value.ApiKeyEnv,
            RecapGridCompletionConnectionsLimits.MaximumIdentifierUtf8Bytes);
        return value;
    }

    private static CompletionReasoningEffort OptionalReasoningEffort(
        JsonElement item
    ) => !item.TryGetProperty("reasoningEffort", out JsonElement value)
        ? CompletionReasoningEffort.ProviderDefault
        : RequireStringValue(value, "reasoningEffort") switch {
            "provider-default" => CompletionReasoningEffort.ProviderDefault,
            "disabled" => CompletionReasoningEffort.Disabled,
            "low" => CompletionReasoningEffort.Low,
            "medium" => CompletionReasoningEffort.Medium,
            "high" => CompletionReasoningEffort.High,
            "max" => CompletionReasoningEffort.Max,
            _ => throw new InvalidDataException(
                "reasoningEffort is unsupported."
            )
        };

    private static AnthropicPromptCacheTtl OptionalAnthropicPromptCacheTtl(
        JsonElement item
    ) => !item.TryGetProperty(
            "anthropicPromptCacheTtl",
            out JsonElement value)
        ? AnthropicPromptCacheTtl.ProviderDefault
        : RequireStringValue(value, "anthropicPromptCacheTtl") switch {
            "provider-default" => AnthropicPromptCacheTtl.ProviderDefault,
            "5m" => AnthropicPromptCacheTtl.FiveMinutes,
            "1h" => AnthropicPromptCacheTtl.OneHour,
            _ => throw new InvalidDataException(
                "anthropicPromptCacheTtl is unsupported."
            )
        };

    private static int? OptionalPositiveInt32(
        JsonElement item,
        string propertyName
    ) {
        if (!item.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind is JsonValueKind.Null) {
            return null;
        }
        if (value.ValueKind is not JsonValueKind.Number
            || !value.TryGetInt32(out int parsed)
            || parsed <= 0) {
            throw new InvalidDataException(
                $"{propertyName} must be null or a positive Int32."
            );
        }
        return parsed;
    }

    private static string RequireString(
        JsonElement item,
        string propertyName,
        int maximumUtf8Bytes
    ) {
        if (!item.TryGetProperty(propertyName, out JsonElement value)) {
            throw new InvalidDataException(
                $"Required property '{propertyName}' is absent."
            );
        }
        string parsed = RequireStringValue(value, propertyName);
        RequireBounded(parsed, maximumUtf8Bytes);
        return parsed;
    }

    private static string RequireStringAllowEmpty(
        JsonElement item,
        string propertyName,
        int maximumUtf8Bytes
    ) {
        if (!item.TryGetProperty(propertyName, out JsonElement value)) {
            throw new InvalidDataException(
                $"Required property '{propertyName}' is absent."
            );
        }
        string parsed = RequireStringValue(value, propertyName);
        if (StrictUtf8.GetByteCount(parsed) > maximumUtf8Bytes) {
            throw new InvalidDataException(
                $"{propertyName} exceeds its V1 UTF-8 bound."
            );
        }
        return parsed;
    }

    private static string? OptionalString(
        JsonElement item,
        string propertyName,
        int maximumUtf8Bytes
    ) {
        if (!item.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind is JsonValueKind.Null) {
            return null;
        }
        string parsed = RequireStringValue(value, propertyName);
        RequireBounded(parsed, maximumUtf8Bytes);
        return parsed;
    }

    private static string RequireStringValue(
        JsonElement value,
        string propertyName
    ) => value.ValueKind is JsonValueKind.String
        ? value.GetString() ?? throw new InvalidDataException(
            $"{propertyName} must not be null."
        )
        : throw new InvalidDataException(
            $"{propertyName} must be a string."
        );

    private static void RequireBounded(string value, int maximumUtf8Bytes) {
        if (string.IsNullOrWhiteSpace(value)
            || StrictUtf8.GetByteCount(value) > maximumUtf8Bytes) {
            throw new InvalidDataException(
                "A retained Completion connection field is empty or exceeds "
                + "its V1 UTF-8 bound."
            );
        }
    }

    private static void RequireOptionalBounded(
        string? value,
        int maximumUtf8Bytes
    ) {
        if (value is not null) {
            RequireBounded(value, maximumUtf8Bytes);
        }
    }

    private static void RequireProperties(
        JsonElement value,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional
    ) {
        if (value.ValueKind is not JsonValueKind.Object) {
            throw new InvalidDataException("A JSON object is required.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject()) {
            if (!seen.Add(property.Name)
                || (!required.Contains(property.Name, StringComparer.Ordinal)
                    && !optional.Contains(property.Name,
                        StringComparer.Ordinal))) {
                throw new InvalidDataException(
                    "Completion connections contain an unknown or duplicate "
                    + "property."
                );
            }
        }
        if (required.Any(property => !seen.Contains(property))) {
            throw new InvalidDataException(
                "Completion connections are missing a required property."
            );
        }
    }
}
