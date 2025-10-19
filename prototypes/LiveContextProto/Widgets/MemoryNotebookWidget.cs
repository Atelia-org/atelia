using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Tools;

namespace Atelia.LiveContextProto.Widgets;

internal sealed class MemoryNotebookWidget : IWidget {
    internal const string ToolNameReplace = "memory_notebook_replace";
    private const string DebugCategory = "MemoryNotebookWidget";
    internal const string DefaultSnapshot = "（暂无 Memory Notebook 内容）";

    private readonly ImmutableArray<ITool> _tools;

    private string? _notebookContent;

    public MemoryNotebookWidget() {
        _tools = ImmutableArray.Create<ITool>(new MemoryNotebookReplaceTool(this));
    }

    public string Name => "MemoryNotebook";

    public string Description => "封装管理 Memory Notebook 状态的 Widget，提供工具化替换与 LiveScreen 渲染能力。";

    public IReadOnlyList<ITool> Tools => _tools;

    public string? RenderLiveScreen(WidgetRenderContext context) {
        var builder = new StringBuilder();
        builder.AppendLine("## Memory Notebook");
        builder.AppendLine();

        var content = _notebookContent;
        if (string.IsNullOrWhiteSpace(content)) {
            builder.AppendLine(DefaultSnapshot);
        }
        else {
            builder.AppendLine(content);
        }

        return builder.ToString().TrimEnd();
    }

    internal void ReplaceNotebookFromHost(string? content) {
        var normalized = Normalize(content);
        DebugUtil.Print(DebugCategory, $"[HostUpdate] length={(normalized?.Length ?? 0)}");
        ApplyNotebookContent(normalized, "host_update");
    }

    internal void Reset() {
        DebugUtil.Print(DebugCategory, "[Reset] resetting notebook content");
        ApplyNotebookContent(null, "reset");
    }

    internal string GetSnapshot()
        => string.IsNullOrWhiteSpace(_notebookContent)
            ? DefaultSnapshot
            : _notebookContent;

    private LodToolCallResult ExecuteReplace(ToolExecutionContext executionContext) {
        var arguments = executionContext.Request.Arguments;
        if (arguments is null) { return Failure("工具参数尚未解析。", "arguments_missing"); }

        if (!TryGetString(arguments, "old_text", required: true, out var oldText, out var oldTextError)) { return Failure(oldTextError ?? "缺少 old_text 参数。", "old_text_invalid"); }

        if (!TryGetString(arguments, "new_text", required: true, out var newText, out var newTextError)) { return Failure(newTextError ?? "缺少 new_text 参数。", "new_text_invalid"); }

        TryGetString(arguments, "search_after", required: false, out var searchAfter, out _);

        oldText ??= string.Empty;
        newText ??= string.Empty;

        var current = _notebookContent ?? string.Empty;
        string updated;
        bool appended = false;

        if (string.IsNullOrEmpty(oldText)) {
            updated = AppendContent(current, newText);
            appended = true;
        }
        else {
            var searchStart = 0;

            if (searchAfter is not null) {
                if (searchAfter.Length == 0) {
                    searchStart = 0;
                }
                else {
                    var anchorIndex = current.IndexOf(searchAfter, StringComparison.Ordinal);
                    if (anchorIndex < 0) { return Failure("未找到指定的 search_after 锚点。", "search_after_not_found"); }

                    searchStart = anchorIndex + searchAfter.Length;
                }
            }

            var matchIndex = current.IndexOf(oldText, searchStart, StringComparison.Ordinal);
            if (matchIndex < 0) { return Failure("未找到匹配的 old_text。", "old_text_not_found"); }

            if (searchAfter is null) {
                var secondIndex = current.IndexOf(oldText, matchIndex + oldText.Length, StringComparison.Ordinal);
                if (secondIndex >= 0) { return Failure("old_text 在文档中出现多次，请提供 search_after 以定位。", "old_text_not_unique"); }
            }

            updated = string.Concat(
                current.AsSpan(0, matchIndex),
                newText,
                current.AsSpan(matchIndex + oldText.Length)
            );
        }

        if (ReferenceEquals(updated, current) || string.Equals(updated, current, StringComparison.Ordinal)) {
            return LodToolCallResult.FromContent(
                ToolExecutionStatus.Success,
                CreateUniformContent(appended ? "未追加任何内容：new_text 为空。" : "替换内容未发生变化。"),
                BuildMetadata(current, updated, appended, searchAfter)
            );
        }

        var normalizedUpdated = Normalize(updated);
        ApplyNotebookContent(normalizedUpdated, "tool_replace");

        var newContent = normalizedUpdated ?? string.Empty;
        var basicMessage = appended
            ? "已追加新的记忆段落。"
            : "已完成记忆文本替换。";

        return LodToolCallResult.FromContent(
            ToolExecutionStatus.Success,
            CreateOperationContent(basicMessage, appended, current.Length, newContent.Length, searchAfter),
            BuildMetadata(current, newContent, appended, searchAfter)
        );
    }

    private void ApplyNotebookContent(string? content, string source) {
        _notebookContent = content;
        DebugUtil.Print(DebugCategory, $"[State] notebook updated source={source} length={(content?.Length ?? 0)}");
    }

    private static string AppendContent(string current, string addition) {
        if (string.IsNullOrEmpty(addition)) { return current; }

        if (string.IsNullOrEmpty(current)) { return addition; }

        if (!current.EndsWith(Environment.NewLine, StringComparison.Ordinal) &&
            !addition.StartsWith(Environment.NewLine, StringComparison.Ordinal) &&
            !addition.StartsWith("\n", StringComparison.Ordinal)) { return string.Concat(current, Environment.NewLine, addition); }

        return current + addition;
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, object?> arguments,
        string key,
        bool required,
        out string? value,
        out string? error
    ) {
        if (!arguments.TryGetValue(key, out var rawValue)) {
            if (required) {
                value = null;
                error = $"缺少参数 {key}";
                return false;
            }

            value = null;
            error = null;
            return true;
        }

        if (rawValue is null) {
            value = required ? null : string.Empty;
            error = required ? $"参数 {key} 不能为空" : null;
            return !required;
        }

        value = rawValue switch {
            string s => s,
            _ => rawValue.ToString() ?? string.Empty
        };

        error = null;
        return true;
    }

    private static ImmutableDictionary<string, object?> BuildMetadata(
        string previous,
        string current,
        bool appended,
        string? searchAfter
    ) {
        var metadata = ImmutableDictionary<string, object?>.Empty
            .SetItem("previous_length", previous.Length)
            .SetItem("new_length", current.Length)
            .SetItem("delta", current.Length - previous.Length)
            .SetItem("operation", appended ? "append" : "replace");

        if (!string.IsNullOrEmpty(searchAfter)) {
            metadata = metadata.SetItem("search_after", searchAfter);
        }

        return metadata;
    }

    private static string? Normalize(string? content) {
        if (string.IsNullOrWhiteSpace(content)) { return null; }

        return content.TrimEnd();
    }

    private LodToolCallResult Failure(string message, string errorCode) {
        var metadata = ImmutableDictionary<string, object?>.Empty.SetItem("error", errorCode);
        return LodToolCallResult.FromContent(ToolExecutionStatus.Failed, CreateUniformContent(message), metadata);
    }

    private static LevelOfDetailContent CreateUniformContent(string? text)
        => new(text ?? string.Empty);

    private static LevelOfDetailContent CreateOperationContent(
        string basicMessage,
        bool appended,
        int previousLength,
        int newLength,
        string? searchAfter
    ) {
        var extra = BuildExtraDetails(appended, previousLength, newLength, searchAfter);
        return new LevelOfDetailContent(basicMessage, extra);
    }

    private static string BuildExtraDetails(bool appended, int previousLength, int newLength, string? searchAfter) {
        var delta = newLength - previousLength;
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.Append("- operation: ").Append(appended ? "append" : "replace").AppendLine();
        builder.Append("- delta: ");
        if (delta >= 0) { builder.Append('+'); }
        builder.Append(delta).AppendLine();
        builder.Append("- new_length: ").Append(newLength);

        var anchorText = FormatAnchor(searchAfter);
        if (anchorText is not null) {
            builder.AppendLine();
            builder.Append("- anchor: ").Append(anchorText);
        }

        return builder.ToString().TrimEnd();
    }

    private static string? FormatAnchor(string? searchAfter) {
        if (string.IsNullOrWhiteSpace(searchAfter)) { return null; }

        var normalized = NormalizeAnchorWhitespace(searchAfter).Trim();
        if (normalized.Length == 0) { return null; }

        const int MaxLength = 80;
        const int PrefixLength = 40;
        const int SuffixLength = 35;

        if (normalized.Length <= MaxLength || normalized.Length <= PrefixLength + SuffixLength) { return normalized; }

        var prefix = normalized.Substring(0, PrefixLength);
        var suffix = normalized.Substring(normalized.Length - SuffixLength, SuffixLength);
        return string.Concat(prefix, "…", suffix);
    }

    private static string NormalizeAnchorWhitespace(string value) {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;

        foreach (var ch in value) {
            var normalizedChar = ch switch {
                '\r' or '\n' or '\t' => ' ',
                _ => ch
            };

            if (char.IsWhiteSpace(normalizedChar)) {
                if (previousWasSpace) { continue; }
                builder.Append(' ');
                previousWasSpace = true;
            }
            else {
                builder.Append(normalizedChar);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }

    private sealed class MemoryNotebookReplaceTool : ITool {
        private readonly MemoryNotebookWidget _owner;
        private readonly ImmutableArray<ToolParameter> _parameters;

        public MemoryNotebookReplaceTool(MemoryNotebookWidget owner) {
            _owner = owner;
            _parameters = ImmutableArray.Create(
                new ToolParameter(
                    name: "old_text",
                    valueKind: ToolParameterValueKind.String,
                    cardinality: ToolParameterCardinality.Single,
                    isRequired: true,
                    description: "要替换的旧文本；允许为空字符串以表示追加。"
                ),
                new ToolParameter(
                    name: "new_text",
                    valueKind: ToolParameterValueKind.String,
                    cardinality: ToolParameterCardinality.Single,
                    isRequired: true,
                    description: "替换后的新文本；为空字符串表示删除。"
                ),
                new ToolParameter(
                    name: "search_after",
                    valueKind: ToolParameterValueKind.String,
                    cardinality: ToolParameterCardinality.Optional,
                    isRequired: false,
                    description: "可选锚点；缺省时要求 old_text 在文档中唯一出现。"
                )
            );
        }

        public string Name => ToolNameReplace;

        public string Description => "在 Memory Notebook 中查找并替换文本，支持锚点定位与末尾追加。";

        public IReadOnlyList<ToolParameter> Parameters => _parameters;

        public ValueTask<LodToolCallResult> ExecuteAsync(ToolExecutionContext executionContext, CancellationToken cancellationToken) {
            var result = _owner.ExecuteReplace(executionContext);
            return ValueTask.FromResult(result);
        }
    }
}
