namespace Atelia.MutableContextAgentProto.Protocol;

public sealed record ToolResult(
    string CallId,
    string ToolName,
    bool Succeeded,
    string? Content,
    string? Error
) {
    public static ToolResult Success(string callId, string toolName, string content)
        => new(callId, toolName, true, content, null);

    public static ToolResult Failure(string callId, string toolName, string error)
        => new(callId, toolName, false, null, error);
}
