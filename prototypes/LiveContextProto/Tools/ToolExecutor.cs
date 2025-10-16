using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

internal interface IToolHandler {
    string ToolName { get; }
    ValueTask<ToolHandlerResult> ExecuteAsync(ToolCallRequest request, CancellationToken cancellationToken);
}

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
    private readonly IReadOnlyDictionary<string, IToolHandler> _handlers;

    public ToolExecutor(IEnumerable<IToolHandler> handlers) {
        var dictionary = new Dictionary<string, IToolHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers) {
            dictionary[handler.ToolName] = handler;
        }

        _handlers = dictionary;
        DebugUtil.Print(DebugCategory, $"ToolExecutor initialized handlerCount={_handlers.Count}");
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

        if (!_handlers.TryGetValue(request.ToolName, out var handler)) {
            DebugUtil.Print(DebugCategory, $"[Executor] Missing handler toolName={request.ToolName}");

            var metadata = ImmutableDictionary<string, object?>.Empty
                .Add("error", "handler_not_found");

            return new ToolExecutionRecord(
                request.ToolName,
                request.ToolCallId,
                ToolExecutionStatus.Failed,
                CreateUniformContent($"未找到工具处理器: {request.ToolName}"),
                null,
                metadata
            );
        }

        var stopwatch = Stopwatch.StartNew();

        try {
            var handlerResult = await handler.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
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

    private static LevelOfDetailContent CreateUniformContent(string? content) {
        var value = content ?? string.Empty;
        return new LevelOfDetailContent(value, value, value);
    }
}
