using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Tools;

namespace Atelia.LiveContextProto.Apps;

internal sealed class MemoryNotebookApp : IApp {
    internal const string ReplaceToolName = "memory_notebook_replace";
    private const string DebugCategory = "MemoryNotebookApp";
    internal const string DefaultSnapshot = "（暂无 Memory Notebook 内容）";

    private readonly ImmutableArray<ITool> _tools;

    private string? _notebookContent;

    public MemoryNotebookApp() {
        _tools = ImmutableArray.Create<ITool>(
            MethodToolWrapper.FromDelegate(
                (Func<string, string?, string?, CancellationToken, ValueTask<LodToolExecuteResult>>)ReplaceAsync
            )
        );
    }

    public string Name => "MemoryNotebook";

    public string Description => "封装管理 Memory Notebook 状态的 App，提供工具化替换与 Window 渲染能力。";

    public IReadOnlyList<ITool> Tools => _tools;

    public string? RenderWindow(AppRenderContext context) {
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

    [Tool(ReplaceToolName,
        "在 Memory Notebook 中查找并替换文本；支持通过锚点限定搜索范围，亦可在末尾追加；执行结果会返回 operation/delta/new_length 等细节。"
    )]
    private ValueTask<LodToolExecuteResult> ReplaceAsync(
        [ToolParam("替换后的新文本；为空字符串表示删除匹配到的 old_text，或在追加场景写入空段落。")] string new_text,
        [ToolParam("要替换的旧文本；传 null 或空字符串表示改为末尾追加；若提供文本但未找到匹配将返回错误。")] string? old_text = null,
        [ToolParam("锚点定位文本；如果提供，会从该锚点之后开始搜索 old_text，以区分多次出现的情况。")] string? search_after = null,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var result = ExecuteReplace(old_text, new_text, search_after);
        return ValueTask.FromResult(result);
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

    private LodToolExecuteResult ExecuteReplace(string? oldText, string? newText, string? searchAfter) {
        if (newText is null) { return Failure("缺少 new_text 参数。", "new_text_invalid"); }

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
            string message = appended ? "未追加任何内容：new_text 为空。" : "替换内容未发生变化。";
            return LodToolExecuteResult.FromContent(
                ToolExecutionStatus.Success,
                new LevelOfDetailContent(message, message)
            );
        }

        var normalizedUpdated = Normalize(updated);
        ApplyNotebookContent(normalizedUpdated, "tool_replace");

        var newContent = normalizedUpdated ?? string.Empty;
        var basicMessage = appended
            ? "已追加新的记忆段落。"
            : "已完成记忆文本替换。";

        return LodToolExecuteResult.FromContent(
            ToolExecutionStatus.Success,
            CreateOperationContent(basicMessage, appended, current.Length, newContent.Length, searchAfter)
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

    private static string? Normalize(string? content) {
        if (string.IsNullOrWhiteSpace(content)) { return null; }

        return content.TrimEnd();
    }

    private LodToolExecuteResult Failure(string message, string errorCode) {
        var detail = string.Concat(message, Environment.NewLine, "- error_code: ", errorCode);
        return LodToolExecuteResult.FromContent(
            ToolExecutionStatus.Failed,
            new LevelOfDetailContent(message, detail)
        );
    }

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

}
