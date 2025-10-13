using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Tools;

internal sealed class SampleMemorySearchTool : ITool {
    private static readonly ImmutableArray<ToolParameter> ParameterList = ImmutableArray.Create(
        new ToolParameter(
            name: "query",
            valueKind: ToolParameterValueKind.String,
            cardinality: ToolParameterCardinality.Single,
            isRequired: true,
            description: "搜索记忆库时使用的关键词",
            example: "phase 2 outline"
        ),
        new ToolParameter(
            name: "limit",
            valueKind: ToolParameterValueKind.Integer,
            cardinality: ToolParameterCardinality.Optional,
            isRequired: false,
            description: "要返回的最大片段数量（默认 2 条，最大 5 条）",
            example: "3"
        )
    );

    public string Name => "memory.search";

    public string Description => "在记忆库中根据关键词查找相关片段，并返回简要摘要。";

    public IReadOnlyList<ToolParameter> Parameters => ParameterList;

    public ValueTask<ToolHandlerResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
        var request = context.Request;
        var query = ExtractQuery(request);
        var limit = ExtractLimit(request);

        var matchCount = limit ?? 2;
        if (matchCount < 1) { matchCount = 1; }
        if (matchCount > 5) { matchCount = 5; }

        var metadata = ImmutableDictionary<string, object?>.Empty
            .SetItem("match_count", matchCount)
            .SetItem("query", query)
            .SetItem("source", "sample-memory-index");

        var result = $"[Memory] 命中 {matchCount} 条片段，关键词: {query}";
        return ValueTask.FromResult(new ToolHandlerResult(ToolExecutionStatus.Success, result, metadata));
    }

    private static string ExtractQuery(ToolCallRequest request) {
        if (request.Arguments is { Count: > 0 } arguments && arguments.TryGetValue("query", out var value)) {
            var normalized = SampleToolArgumentNormalizer.Normalize(value);
            if (!string.IsNullOrWhiteSpace(normalized)) { return normalized; }
        }

        if (!string.IsNullOrWhiteSpace(request.RawArguments)) {
            try {
                using var doc = JsonDocument.Parse(request.RawArguments);
                if (doc.RootElement.TryGetProperty("query", out var element) && element.ValueKind == JsonValueKind.String) { return element.GetString() ?? "(empty)"; }
            }
            catch (JsonException) {
                // ignore parse error, fall back to raw text
            }
        }

        return string.IsNullOrWhiteSpace(request.RawArguments)
            ? "(unknown)"
            : request.RawArguments;
    }

    private static int? ExtractLimit(ToolCallRequest request) {
        if (request.Arguments is not { Count: > 0 }) { return null; }

        if (!request.Arguments.TryGetValue("limit", out var value)) { return null; }

        return value switch {
            null => null,
            int i => i,
            long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
            double d when d >= int.MinValue && d <= int.MaxValue => (int)Math.Round(d),
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}

internal sealed class SampleDiagnosticsTool : ITool {
    private static readonly ImmutableArray<ToolParameter> ParameterList = ImmutableArray.Create(
        new ToolParameter(
            name: "reason",
            valueKind: ToolParameterValueKind.String,
            cardinality: ToolParameterCardinality.Optional,
            isRequired: false,
            description: "触发诊断失败的原因描述",
            example: "Console trigger"
        )
    );

    public string Name => "diagnostics.raise";

    public string Description => "触发一条诊断错误，用于验证工具执行失败时的展示与处理逻辑。";

    public IReadOnlyList<ToolParameter> Parameters => ParameterList;

    public ValueTask<ToolHandlerResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
        var request = context.Request;
        var reason = ExtractReason(request) ?? "模拟失败";

        var metadata = ImmutableDictionary<string, object?>.Empty
            .SetItem("reason", reason)
            .SetItem("category", "sample");

        var message = $"模拟失败：{reason}";
        return ValueTask.FromResult(new ToolHandlerResult(ToolExecutionStatus.Failed, message, metadata));
    }

    private static string? ExtractReason(ToolCallRequest request) {
        if (request.Arguments is { Count: > 0 } arguments && arguments.TryGetValue("reason", out var value)) {
            var normalized = SampleToolArgumentNormalizer.Normalize(value);
            if (!string.IsNullOrWhiteSpace(normalized)) { return normalized; }
        }

        return null;
    }
}

internal static class SampleToolArgumentNormalizer {
    public static string? Normalize(object? value) {
        switch (value) {
            case null:
                return null;
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case int or long or double or decimal or float:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            default:
                try {
                    return JsonSerializer.Serialize(value);
                }
                catch {
                    return value?.ToString();
                }
        }
    }
}
