using System.Text.Json.Serialization;

namespace Atelia.MutableContextAgentProto.Protocol;

public sealed record AgentModelResponse(
    [property: JsonPropertyName("thought")] string? Thought,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<ToolCallRequest> ToolCalls,
    [property: JsonPropertyName("final")] string? Final,
    string RawText
);
