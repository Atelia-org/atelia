using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.MutableContextAgentProto.Protocol;

public static class ToolCallParser {
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AgentModelResponse Parse(string modelText) {
        if (string.IsNullOrWhiteSpace(modelText)) { throw new ToolCallParseException("Model response is empty; expected a JSON object.", modelText); }

        var jsonText = ExtractJsonObject(modelText);
        JsonDocument document;
        try {
            document = JsonDocument.Parse(jsonText,
                new JsonDocumentOptions {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }
            );
        }
        catch (JsonException ex) {
            throw new ToolCallParseException(
                $"Model response is not valid tool JSON at path '{ex.Path ?? "<root>"}'. Expected shape: {{ \"thought\": \"...\", \"tool_calls\": [], \"final\": null }}.",
                modelText,
                ex
            );
        }

        using (document) {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new ToolCallParseException("Model response JSON must be an object.", modelText); }

            var thought = TryGetString(root, "thought");
            var final = TryGetString(root, "final");
            var toolCalls = ParseToolCalls(root, modelText);

            return new AgentModelResponse(thought, toolCalls, final, modelText);
        }
    }

    private static string ExtractJsonObject(string modelText) {
        var trimmed = modelText.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}')) { return trimmed; }

        const string jsonFence = "```json";
        int fenceStart = trimmed.IndexOf(jsonFence, StringComparison.OrdinalIgnoreCase);
        if (fenceStart >= 0) {
            int contentStart = fenceStart + jsonFence.Length;
            int fenceEnd = trimmed.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (fenceEnd > contentStart) { return trimmed[contentStart..fenceEnd].Trim(); }
        }

        int firstBrace = trimmed.IndexOf('{');
        int lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace) { return trimmed[firstBrace..(lastBrace + 1)]; }

        return trimmed;
    }

    private static IReadOnlyList<ToolCallRequest> ParseToolCalls(JsonElement root, string modelText) {
        if (!root.TryGetProperty("tool_calls", out var callsElement) || callsElement.ValueKind == JsonValueKind.Null) { return []; }

        if (callsElement.ValueKind != JsonValueKind.Array) { throw new ToolCallParseException("tool_calls must be an array.", modelText); }

        var calls = new List<ToolCallRequest>();
        var index = 0;
        foreach (var callElement in callsElement.EnumerateArray()) {
            if (callElement.ValueKind != JsonValueKind.Object) { throw new ToolCallParseException($"tool_calls[{index}] must be an object.", modelText); }

            var id = TryGetString(callElement, "id");
            if (string.IsNullOrWhiteSpace(id)) {
                id = $"call-{index + 1}";
            }

            var name = TryGetString(callElement, "name")
                ?? TryGetString(callElement, "tool")
                ?? TryGetString(callElement, "tool_name");
            if (string.IsNullOrWhiteSpace(name)) { throw new ToolCallParseException($"tool_calls[{index}].name is required. Accepted aliases: name, tool, tool_name.", modelText); }

            var arguments = TryGetProperty(callElement, "arguments")
                ?? TryGetProperty(callElement, "args")
                ?? JsonSerializer.SerializeToElement(new { });
            if (arguments.ValueKind is not JsonValueKind.Object) { throw new ToolCallParseException($"tool_calls[{index}].arguments must be a JSON object, but was {arguments.ValueKind}.", modelText); }

            calls.Add(new ToolCallRequest(id, name, arguments.Clone()));
            index++;
        }

        return calls;
    }

    private static string? TryGetString(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out var property)) { return null; }

        return property.ValueKind switch {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => property.ToString()
        };
    }

    private static JsonElement? TryGetProperty(JsonElement element, string propertyName) {
        return element.TryGetProperty(propertyName, out var property)
            ? property
            : null;
    }
}
