namespace Atelia.MutableContextAgentProto.Core;

public sealed record ToolDescription(
    string Name,
    string Description,
    string? Usage = null
);
