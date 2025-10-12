using System;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Tools;

internal sealed class SampleMemorySearchToolHandler : IToolHandler {
    public string ToolName => "memory.search";

    public ValueTask<ToolHandlerResult> ExecuteAsync(ToolCallRequest request, CancellationToken cancellationToken) {
        var query = ExtractQuery(request);
        var result = $"[Memory] 命中 2 条片段，关键词: {query}";
        var metadata = ImmutableDictionary<string, object?>.Empty
            .SetItem("match_count", 2)
            .SetItem("query", query);

        return ValueTask.FromResult(new ToolHandlerResult(ToolExecutionStatus.Success, result, metadata));
    }

    private static string ExtractQuery(ToolCallRequest request) {
        if (request.Arguments is { Count: > 0 } arguments && arguments.TryGetValue("query", out var value)) { return value; }

        try {
            if (!string.IsNullOrWhiteSpace(request.RawArguments)) {
                using var doc = JsonDocument.Parse(request.RawArguments);
                if (doc.RootElement.TryGetProperty("query", out var element) && element.ValueKind == JsonValueKind.String) { return element.GetString() ?? "(empty)"; }
            }
        }
        catch (JsonException) {
            // ignore, fall back to raw text
        }

        return string.IsNullOrWhiteSpace(request.RawArguments)
            ? "(unknown)"
            : request.RawArguments;
    }
}

internal sealed class SampleFailingToolHandler : IToolHandler {
    public string ToolName => "diagnostics.raise";

    public ValueTask<ToolHandlerResult> ExecuteAsync(ToolCallRequest request, CancellationToken cancellationToken) {
        var message = request.Arguments is { Count: > 0 } args && args.TryGetValue("reason", out var reason)
            ? reason
            : "模拟失败";

        var metadata = ImmutableDictionary<string, object?>.Empty
            .SetItem("reason", message);

        return ValueTask.FromResult(new ToolHandlerResult(ToolExecutionStatus.Failed, $"模拟失败：{message}", metadata));
    }
}
