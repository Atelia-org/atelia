using System.Collections.Immutable;
using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Agent.Core.Tool;

internal sealed class ToolExecutor {
    private const string DebugCategory = "Tools";
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly ImmutableArray<ToolDefinition> _allToolDefinitions;
    private readonly Dictionary<ITool, ToolDefinition> _definitionByInstance;

    public ToolExecutor(IEnumerable<ITool> tools) {
        if (tools is null) { throw new ArgumentNullException(nameof(tools)); }

        var dictionary = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools) {
            if (tool is null) { continue; }

            if (dictionary.ContainsKey(tool.Name)) { throw new InvalidOperationException($"Duplicate tool registration detected for '{tool.Name}'."); }

            dictionary[tool.Name] = tool;
        }

        _tools = dictionary;

        // 预先缓存每个工具实例对应的 ToolDefinition，以便后续按可见性筛选时避免重复构建。
        var definitionMap = new Dictionary<ITool, ToolDefinition>(ReferenceEqualityComparer.Instance);
        var definitionBuilder = ImmutableArray.CreateBuilder<ToolDefinition>(_tools.Count);

        foreach (var tool in _tools.Values) {
            if (tool is null) { continue; }

            var definition = ToolDefinitionBuilder.FromTool(tool);
            definitionMap[tool] = definition;
            definitionBuilder.Add(definition);
        }

        _definitionByInstance = definitionMap;
        _allToolDefinitions = definitionBuilder.Count == 0
            ? ImmutableArray<ToolDefinition>.Empty
            : definitionBuilder.ToImmutable();
        DebugUtil.Print(DebugCategory, $"ToolExecutor initialized toolCount={_tools.Count}");
    }

    public IEnumerable<ITool> Tools => _tools.Values;

    /// <summary>
    /// 获取所有已注册工具的定义（包含当前被隐藏的工具）。
    /// </summary>
    public ImmutableArray<ToolDefinition> AllToolDefinitions => _allToolDefinitions;

    /// <summary>
    /// 获取当前对模型可见的工具定义快照。
    /// </summary>
    /// <remarks>
    /// 每次调用都会根据工具的 <see cref="ITool.Visible"/> 状态重新构建结果，
    /// 以便调用方可以在运行时动态切换工具可见性。
    /// </remarks>
    public ImmutableArray<ToolDefinition> GetVisibleToolDefinitions() {
        if (_tools.Count == 0) { return ImmutableArray<ToolDefinition>.Empty; }

        var builder = ImmutableArray.CreateBuilder<ToolDefinition>();
        foreach (var tool in _tools.Values) {
            if (tool is null || !tool.Visible) { continue; }

            if (_definitionByInstance.TryGetValue(tool, out var definition)) {
                builder.Add(definition);
            }
        }

        return builder.Count == 0 ? ImmutableArray<ToolDefinition>.Empty : builder.ToImmutable();
    }

    public bool TryGetTool(string name, out ITool tool) {
        if (string.IsNullOrWhiteSpace(name)) {
            tool = null!;
            return false;
        }

        return _tools.TryGetValue(name, out tool!);
    }

    public async ValueTask<LodToolCallResult> ExecuteAsync(
        ParsedToolCall request,
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

    private static LodToolCallResult CreateParseFailureResult(ParsedToolCall request) {
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
