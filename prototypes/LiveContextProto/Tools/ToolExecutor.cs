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
    string Result,
    ImmutableDictionary<string, object?> Metadata
) {
    public ToolHandlerResult(ToolExecutionStatus status, string result)
        : this(status, result, ImmutableDictionary<string, object?>.Empty) {
    }
}

internal sealed record ToolExecutionRecord(
    ToolCallResult CallResult,
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
            var callResult = new ToolCallResult(
                request.ToolName,
                request.ToolCallId,
                ToolExecutionStatus.Failed,
                $"未找到工具处理器: {request.ToolName}",
                null
            );

            var metadata = ImmutableDictionary<string, object?>.Empty
                .Add("error", "handler_not_found");

            return new ToolExecutionRecord(callResult, metadata);
        }

        var stopwatch = Stopwatch.StartNew();

        try {
            var handlerResult = await handler.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            DebugUtil.Print(
                DebugCategory,
                $"[Executor] Completed toolName={request.ToolName} toolCallId={request.ToolCallId} status={handlerResult.Status} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}"
            );

            var callResult = new ToolCallResult(
                request.ToolName,
                request.ToolCallId,
                handlerResult.Status,
                handlerResult.Result,
                stopwatch.Elapsed
            );

            var metadata = handlerResult.Metadata;
            if (!metadata.ContainsKey("elapsed_ms")) {
                metadata = metadata.SetItem("elapsed_ms", stopwatch.Elapsed.TotalMilliseconds);
            }

            return new ToolExecutionRecord(callResult, metadata);
        }
        catch (OperationCanceledException) {
            stopwatch.Stop();
            DebugUtil.Print(DebugCategory, $"[Executor] Cancelled toolName={request.ToolName} toolCallId={request.ToolCallId}");
            var callResult = new ToolCallResult(
                request.ToolName,
                request.ToolCallId,
                ToolExecutionStatus.Skipped,
                "工具执行被取消",
                stopwatch.Elapsed
            );

            var metadata = ImmutableDictionary<string, object?>.Empty
                .SetItem("elapsed_ms", stopwatch.Elapsed.TotalMilliseconds)
                .SetItem("error", "cancelled");

            return new ToolExecutionRecord(callResult, metadata);
        }
        catch (Exception ex) {
            stopwatch.Stop();
            DebugUtil.Print(DebugCategory, $"[Executor] Failed toolName={request.ToolName} toolCallId={request.ToolCallId} error={ex.Message}");
            var callResult = new ToolCallResult(
                request.ToolName,
                request.ToolCallId,
                ToolExecutionStatus.Failed,
                $"工具执行异常: {ex.Message}",
                stopwatch.Elapsed
            );

            var metadata = ImmutableDictionary<string, object?>.Empty
                .SetItem("elapsed_ms", stopwatch.Elapsed.TotalMilliseconds)
                .SetItem("error", ex.GetType().Name);

            return new ToolExecutionRecord(callResult, metadata);
        }
    }
}
