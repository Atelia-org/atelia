using System.Text.Json;

namespace Atelia.MutableContextAgentProto.Protocol;

public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement ParametersJsonSchema
);
