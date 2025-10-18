using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

internal sealed record ToolHandlerResult(
    ToolExecutionStatus Status,
    LevelOfDetailContent Result,
    ImmutableDictionary<string, object?> Metadata
) {
    public ToolHandlerResult(ToolExecutionStatus status, string result)
        : this(status, CreateUniformContent(result)) {
    }

    public ToolHandlerResult(ToolExecutionStatus status, LevelOfDetailContent result)
        : this(status, result ?? throw new ArgumentNullException(nameof(result)), ImmutableDictionary<string, object?>.Empty) {
    }

    private static LevelOfDetailContent CreateUniformContent(string result) {
        var value = result ?? string.Empty;
        return new LevelOfDetailContent(value, value, value);
    }
}

internal sealed record ToolExecutionRecord(
    string ToolName,
    string ToolCallId,
    ToolExecutionStatus Status,
    LevelOfDetailContent Result,
    TimeSpan? Elapsed,
    ImmutableDictionary<string, object?> Metadata
);

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

    public async ValueTask<IReadOnlyList<ToolExecutionRecord>> ExecuteBatchAsync(
        IReadOnlyList<ToolCallRequest> requests,
        CancellationToken cancellationToken
    ) {
        if (requests.Count == 0) { return Array.Empty<ToolExecutionRecord>(); }

        var results = new List<ToolExecutionRecord>(requests.Count);

        foreach (var request in requests) {
            var result = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            results.Add(result);
        }

        return results;
    }

    public async ValueTask<ToolExecutionRecord> ExecuteAsync(
        ToolCallRequest request,
        CancellationToken cancellationToken
    ) {
        DebugUtil.Print(DebugCategory, $"[Executor] Dispatch toolName={request.ToolName} toolCallId={request.ToolCallId}");

        if (!_tools.TryGetValue(request.ToolName, out var tool)) {
            DebugUtil.Print(DebugCategory, $"[Executor] Missing tool toolName={request.ToolName}");

            var metadata = ImmutableDictionary<string, object?>.Empty
                .Add("error", "tool_not_found");

            return new ToolExecutionRecord(
                request.ToolName,
                request.ToolCallId,
                ToolExecutionStatus.Failed,
                CreateUniformContent($"未找到工具: {request.ToolName}"),
                null,
                metadata
            );
        }

        var stopwatch = Stopwatch.StartNew();
        var normalizedRequest = EnsureArguments(tool, request);
        var environment = _environmentFactory?.Invoke(tool) ?? ImmutableDictionary<string, object?>.Empty;
        var context = new ToolExecutionContext(normalizedRequest, environment);

        try {
            var handlerResult = await tool.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            DebugUtil.Print(
                DebugCategory,
                $"[Executor] Completed toolName={request.ToolName} toolCallId={request.ToolCallId} status={handlerResult.Status} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}"
            );

            var metadata = handlerResult.Metadata ?? ImmutableDictionary<string, object?>.Empty;
            if (!metadata.ContainsKey("elapsed_ms")) {
                metadata = metadata.SetItem("elapsed_ms", stopwatch.Elapsed.TotalMilliseconds);
            }

            return new ToolExecutionRecord(
                request.ToolName,
                request.ToolCallId,
                handlerResult.Status,
                handlerResult.Result,
                stopwatch.Elapsed,
                metadata
            );
        }
        catch (OperationCanceledException) {
            stopwatch.Stop();
            DebugUtil.Print(DebugCategory, $"[Executor] Cancelled toolName={request.ToolName} toolCallId={request.ToolCallId}");
            var metadata = ImmutableDictionary<string, object?>.Empty
                .SetItem("elapsed_ms", stopwatch.Elapsed.TotalMilliseconds)
                .SetItem("error", "cancelled");

            return new ToolExecutionRecord(
                request.ToolName,
                request.ToolCallId,
                ToolExecutionStatus.Skipped,
                CreateUniformContent("工具执行被取消"),
                stopwatch.Elapsed,
                metadata
            );
        }
        catch (Exception ex) {
            stopwatch.Stop();
            DebugUtil.Print(DebugCategory, $"[Executor] Failed toolName={request.ToolName} toolCallId={request.ToolCallId} error={ex.Message}");
            var metadata = ImmutableDictionary<string, object?>.Empty
                .SetItem("elapsed_ms", stopwatch.Elapsed.TotalMilliseconds)
                .SetItem("error", ex.GetType().Name);

            return new ToolExecutionRecord(
                request.ToolName,
                request.ToolCallId,
                ToolExecutionStatus.Failed,
                CreateUniformContent($"工具执行异常: {ex.Message}"),
                stopwatch.Elapsed,
                metadata
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

    private static LevelOfDetailContent CreateUniformContent(string? content) {
        var value = content ?? string.Empty;
        return new LevelOfDetailContent(value, value, value);
    }
}
