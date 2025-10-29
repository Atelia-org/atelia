using System.Collections.Immutable;
using System.Diagnostics;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

internal sealed class ToolExecutor {
    private const string DebugCategory = "Tools";
    private readonly IReadOnlyDictionary<string, ITool> _tools;

    public ToolExecutor(IEnumerable<ITool> tools, Func<ITool, ImmutableDictionary<string, object?>>? environmentFactory = null) {
        if (tools is null) { throw new ArgumentNullException(nameof(tools)); }

        var dictionary = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools) {
            if (tool is null) { continue; }

            if (dictionary.ContainsKey(tool.Name)) { throw new InvalidOperationException($"Duplicate tool registration detected for '{tool.Name}'."); }

            dictionary[tool.Name] = tool;
        }

        _tools = dictionary;
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
        var normalizedRequest = request;

        if (normalizedRequest.Arguments is null && string.IsNullOrWhiteSpace(normalizedRequest.ParseError)) {
            normalizedRequest = normalizedRequest with {
                ParseError = "arguments_missing_from_provider"
            };
        }

        if (!string.IsNullOrWhiteSpace(normalizedRequest.ParseError)) {
            stopwatch.Stop();
            DebugUtil.Print(
                DebugCategory,
                $"[Executor] Argument parse failed toolName={request.ToolName} toolCallId={request.ToolCallId} error={normalizedRequest.ParseError}"
            );

            return CreateParseFailureResult(normalizedRequest);
        }

        try {
            var arguments = normalizedRequest.Arguments ?? ImmutableDictionary<string, object?>.Empty;
            var executeResult = await tool.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Tool '{tool.Name}' returned null result.");
            stopwatch.Stop();

            DebugUtil.Print(
                DebugCategory,
                $"[Executor] Completed toolName={request.ToolName} toolCallId={request.ToolCallId} status={executeResult.Status} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}"
            );

            var callResult = new LodToolCallResult(executeResult.Status, executeResult.Result)
                .WithContext(request.ToolName, request.ToolCallId, stopwatch.Elapsed);

            return AttachParseWarning(callResult, normalizedRequest.ParseWarning);
        }
        catch (OperationCanceledException) {
            stopwatch.Stop();
            DebugUtil.Print(DebugCategory, $"[Executor] Cancelled toolName={request.ToolName} toolCallId={request.ToolCallId}");

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

    private static LodToolCallResult CreateParseFailureResult(ToolCallRequest request) {
        var basic = "工具参数解析失败。";
        var detail = string.IsNullOrWhiteSpace(request.ParseError)
            ? basic
            : $"解析错误: {request.ParseError}";

        if (request.RawArguments is { Count: > 0 }) {
            var snapshot = string.Join(", ", request.RawArguments.Select(pair => $"{pair.Key}={pair.Value}"));
            detail = string.Concat(detail, "\nraw_arguments: ", snapshot);
        }

        return new LodToolCallResult(
            status: ToolExecutionStatus.Failed,
            result: new LevelOfDetailContent(basic, detail),
            toolName: request.ToolName,
            toolCallId: request.ToolCallId
        );
    }

    private static LodToolCallResult AttachParseWarning(LodToolCallResult result, string? parseWarning) {
        if (string.IsNullOrWhiteSpace(parseWarning)) { return result; }

        var detail = string.Concat(result.Result.Detail, "\n[ParseWarning] ", parseWarning);
        var enrichedContent = new LevelOfDetailContent(result.Result.Basic, detail);
        return result with { Result = enrichedContent };
    }

}
