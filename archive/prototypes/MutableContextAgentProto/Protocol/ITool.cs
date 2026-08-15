namespace Atelia.MutableContextAgentProto.Protocol;

public interface ITool {
    ToolDefinition Definition { get; }

    ValueTask<ToolResult> ExecuteAsync(ToolCallRequest request, CancellationToken cancellationToken);
}
