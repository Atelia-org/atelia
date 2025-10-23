using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

internal sealed class ToolExecutor {
    private const string DebugCategory = "Tools";
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly Func<ITool, ImmutableDictionary<string, object?>>? _environmentFactory;

    public ToolExecutor(IEnumerable<ITool> tools, Func<ITool, ImmutableDictionary<string, object?>>? environmentFactory = null) {
        if (tools is null) { throw new ArgumentNullException(nameof(tools)); }

        var dictionary = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools) {
            if (tool is null) { continue; }

            if (dictionary.ContainsKey(tool.Name)) { throw new InvalidOperationException($"Duplicate tool registration detected for '{tool.Name}'."); }

            dictionary[tool.Name] = tool;
        }

        _tools = dictionary;
        _environmentFactory = environmentFactory;
        DebugUtil.Print(DebugCategory, $"ToolExecutor initialized toolCount={_tools.Count}");
    }

    public IEnumerable<ITool> Tools => _tools.Values;

    public bool TryGetTool(string name, out ITool tool) {
        if (string.IsNullOrWhiteSpace(name)) {
            tool = null!;
            return false;
        }

        return _tools.TryGetValue(name, out tool!);
    }

    public async ValueTask<IReadOnlyList<LodToolCallResult>> ExecuteBatchAsync(
        IReadOnlyList<ToolCallRequest> requests,
        CancellationToken cancellationToken
    ) {
        if (requests.Count == 0) { return Array.Empty<LodToolCallResult>(); }

        var results = new List<LodToolCallResult>(requests.Count);

        foreach (var request in requests) {
            var result = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            results.Add(result);
        }

        return results;
    }

    public async ValueTask<LodToolCallResult> ExecuteAsync(
        ToolCallRequest request,
        CancellationToken cancellationToken
    ) {
        DebugUtil.Print(DebugCategory, $"[Executor] Dispatch toolName={request.ToolName} toolCallId={request.ToolCallId}");

        if (!_tools.TryGetValue(request.ToolName, out var tool)) {
            DebugUtil.Print(DebugCategory, $"[Executor] Missing tool toolName={request.ToolName}");

            var message = $"未找到工具: {request.ToolName}";
            return new LodToolCallResult(
                status: ToolExecutionStatus.Failed,
                result: new LevelOfDetailContent(message, message),
                toolName: request.ToolName,
                toolCallId: request.ToolCallId
            );
        }

        var stopwatch = Stopwatch.StartNew();
        var normalizedRequest = EnsureArguments(tool, request);
        var environment = _environmentFactory?.Invoke(tool) ?? ImmutableDictionary<string, object?>.Empty;
        var context = new ToolExecutionContext(normalizedRequest, environment);

        try {
            var result = await tool.ExecuteAsync(context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Tool '{tool.Name}' returned null result.");
            stopwatch.Stop();

            DebugUtil.Print(
                DebugCategory,
                $"[Executor] Completed toolName={request.ToolName} toolCallId={request.ToolCallId} status={result.Status} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}"
            );

            var elapsed_ms = stopwatch.Elapsed.TotalMilliseconds;

            return result.WithContext(request.ToolName, request.ToolCallId, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) {
            stopwatch.Stop();
            DebugUtil.Print(DebugCategory, $"[Executor] Cancelled toolName={request.ToolName} toolCallId={request.ToolCallId}");
            var metadata = ImmutableDictionary<string, object?>.Empty
                .SetItem("elapsed_ms", stopwatch.Elapsed.TotalMilliseconds)
                .SetItem("error", "cancelled");

            var message = "工具执行被取消";
            return new LodToolCallResult(
                status: ToolExecutionStatus.Skipped,
                result: new LevelOfDetailContent(message, message),
                toolName: request.ToolName,
                toolCallId: request.ToolCallId,
                elapsed: stopwatch.Elapsed
            );
        }
        catch (Exception ex) {
            stopwatch.Stop();
            DebugUtil.Print(DebugCategory, $"[Executor] Failed toolName={request.ToolName} toolCallId={request.ToolCallId} error={ex.Message}");
            var metadata = ImmutableDictionary<string, object?>.Empty
                .SetItem("elapsed_ms", stopwatch.Elapsed.TotalMilliseconds)
                .SetItem("error", ex.GetType().Name);

            var message = $"工具执行异常: {ex.Message}";
            return new LodToolCallResult(
                status: ToolExecutionStatus.Failed,
                result: new LevelOfDetailContent(message, message),
                toolName: request.ToolName,
                toolCallId: request.ToolCallId,
                elapsed: stopwatch.Elapsed
            );
        }
    }

    private static ToolCallRequest EnsureArguments(ITool tool, ToolCallRequest request) {
        if (request.Arguments is not null) { return request; }

        var parsed = ToolArgumentParser.ParseArguments(tool, request.RawArguments);
        return request with {
            Arguments = parsed.Arguments,
            ParseError = CombineMessages(request.ParseError, parsed.ParseError),
            ParseWarning = CombineMessages(request.ParseWarning, parsed.ParseWarning)
        };
    }

    private static string? CombineMessages(string? primary, string? secondary) {
        if (string.IsNullOrWhiteSpace(primary)) { return secondary; }
        if (string.IsNullOrWhiteSpace(secondary)) { return primary; }
        return string.Concat(primary, "; ", secondary);
    }

}
