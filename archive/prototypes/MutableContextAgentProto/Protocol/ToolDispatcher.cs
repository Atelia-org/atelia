namespace Atelia.MutableContextAgentProto.Protocol;

public sealed class ToolDispatcher {
    private readonly IReadOnlyDictionary<string, ITool> _toolsByName;

    public ToolDispatcher(IEnumerable<ITool> tools) {
        ArgumentNullException.ThrowIfNull(tools);
        _toolsByName = tools.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);
    }

    public async ValueTask<IReadOnlyList<ToolResult>> DispatchAsync(
        IEnumerable<ToolCallRequest> toolCalls,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(toolCalls);

        var results = new List<ToolResult>();
        foreach (var call in toolCalls) {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_toolsByName.TryGetValue(call.Name, out var tool)) {
                results.Add(ToolResult.Failure(call.Id, call.Name, $"Unknown tool: {call.Name}"));
                continue;
            }

            try {
                results.Add(await tool.ExecuteAsync(call, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                results.Add(ToolResult.Failure(call.Id, call.Name, ex.ToString()));
            }
        }

        return results;
    }
}
