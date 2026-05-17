using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.MutableContextAgentProto.Protocol;

public sealed record ToolCallRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] JsonElement Arguments
);
