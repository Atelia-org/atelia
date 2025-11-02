using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Tools;
using Atelia.LiveContextProto.Text;

namespace Atelia.LiveContextProto.Apps;

internal sealed class MemoryNotebookApp : IApp {
    internal const string ReplaceToolName = "memory_notebook_replace";
    internal const string ReplaceSpanToolName = "memory_notebook_replace_span";
    private const string DebugCategory = "MemoryNotebookApp";
    internal const string DefaultSnapshot = "（暂无 Memory Notebook 内容）";
    private static readonly string NotebookLabel = "Memory Notebook";

    private readonly ImmutableArray<ITool> _tools;
    private readonly TextReplacementEngine _replacementEngine;

    private string? _pendingEngineSource;
    private string? _notebookContent;

    public MemoryNotebookApp() {
        _replacementEngine = new TextReplacementEngine(GetNotebookForEngine, SetNotebookFromEngine, NotebookLabel);
        _tools = ImmutableArray.Create<ITool>(
            MethodToolWrapper.FromDelegate(
                (Func<string, string?, string?, CancellationToken, ValueTask<LodToolExecuteResult>>)ReplaceAsync
            ),
            MethodToolWrapper.FromDelegate(
                (Func<string, string, string, string?, CancellationToken, ValueTask<LodToolExecuteResult>>)ReplaceSpanAsync
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

    [Tool(ReplaceSpanToolName,
        "通过起止标记精确定位 Memory Notebook 中的区块并替换；适合多段相似内容时依靠首尾锚点锁定唯一目标，可选 search_after 进一步约束搜索范围。"
    )]
    private ValueTask<LodToolExecuteResult> ReplaceSpanAsync(
        [ToolParam("区块起始标记文本，需与 Memory Notebook 内容完全匹配。")] string old_span_start,
        [ToolParam("区块结束标记文本，需与 Memory Notebook 内容完全匹配。")] string old_span_end,
        [ToolParam("替换后的新文本，需包含希望保留的首尾标记。")] string new_text,
        [ToolParam("锚点定位文本；提供后，会从锚点之后搜索 old_span_start，避免多次出现时误替换。")] string? search_after = null,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var result = ExecuteReplaceSpan(old_span_start, old_span_end, new_text, search_after);
        return ValueTask.FromResult(result);
    }

    internal void ReplaceNotebookFromHost(string? content) {
        var normalized = Normalize(content);
        DebugUtil.Print(DebugCategory, $"[HostUpdate] length={(normalized?.Length ?? 0)}");
        ApplyNotebookContent(normalized, "host_update");
    }

    internal string GetSnapshot()
        => string.IsNullOrWhiteSpace(_notebookContent)
            ? DefaultSnapshot
            : _notebookContent;

    private LodToolExecuteResult ExecuteReplace(string? oldText, string newText, string? searchAfter) {
        var isAppend = string.IsNullOrEmpty(oldText);
        var normalizedOld = isAppend
            ? string.Empty
            : TextToolUtilities.NormalizeLineEndings(oldText!);

        var request = new ReplacementRequest(
            normalizedOld,
            newText,
            searchAfter,
            isAppend,
            ReplaceToolName
        );

        var locator = isAppend ? null : new LiteralRegionLocator(normalizedOld);

        return ExecuteReplacement(
            request,
            locator,
            (previous, updated) => {
                if (string.Equals(previous, updated, StringComparison.Ordinal)) {
                    var message = isAppend
                        ? "未追加任何内容：new_text 为空。"
                        : "替换内容未发生变化。";

                    return LodToolExecuteResult.FromContent(
                        ToolExecutionStatus.Success,
                        new LevelOfDetailContent(message, message)
                    );
                }

                var basicMessage = isAppend
                    ? "已追加新的记忆段落。"
                    : "已完成记忆文本替换。";

                return LodToolExecuteResult.FromContent(
                    ToolExecutionStatus.Success,
                    CreateOperationContent(basicMessage, isAppend, previous.Length, updated.Length, searchAfter)
                );
            }
        );
    }

    private LodToolExecuteResult ExecuteReplaceSpan(string oldSpanStart, string oldSpanEnd, string newText, string? searchAfter) {
        var normalizedStart = TextToolUtilities.NormalizeLineEndings(oldSpanStart);
        var normalizedEnd = TextToolUtilities.NormalizeLineEndings(oldSpanEnd);

        var request = new ReplacementRequest(
            normalizedStart,
            newText,
            searchAfter,
            IsAppend: false,
            ReplaceSpanToolName
        );

        var locator = new SpanRegionLocator(normalizedStart, normalizedEnd);

        return ExecuteReplacement(
            request,
            locator,
            (previous, updated) => {
                if (string.Equals(previous, updated, StringComparison.Ordinal)) {
                    const string message = "替换内容未发生变化。";
                    return LodToolExecuteResult.FromContent(
                        ToolExecutionStatus.Success,
                        new LevelOfDetailContent(message, message)
                    );
                }

                const string basicMessage = "已完成记忆区块替换。";
                return LodToolExecuteResult.FromContent(
                    ToolExecutionStatus.Success,
                    CreateOperationContent(basicMessage, appended: false, previous.Length, updated.Length, searchAfter)
                );
            }
        );
    }

    private LodToolExecuteResult ExecuteReplacement(
        ReplacementRequest request,
        IRegionLocator? locator,
        Func<string, string, LodToolExecuteResult> onSuccess
    ) {
        var previous = GetNotebookNormalized();

        ReplacementOutcome outcome;
        _pendingEngineSource = string.IsNullOrEmpty(request.OperationName)
            ? null
            : request.OperationName;
        try {
            outcome = _replacementEngine.Execute(request, locator);
        }
        finally {
            _pendingEngineSource = null;
        }

        if (!outcome.Success) { return EngineFailure(outcome.Message); }

        var updated = GetNotebookNormalized();
        return onSuccess(previous, updated);
    }

    private static LodToolExecuteResult EngineFailure(string message) {
        return LodToolExecuteResult.FromContent(
            ToolExecutionStatus.Failed,
            new LevelOfDetailContent(message, message)
        );
    }

    private string GetNotebookForEngine()
        => _notebookContent ?? string.Empty;

    private string GetNotebookNormalized()
        => TextToolUtilities.NormalizeLineEndings(GetNotebookForEngine());

    private void SetNotebookFromEngine(string content) {
        var normalized = content
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        var withEnvironmentLineEndings = normalized.Replace("\n", Environment.NewLine);
        var final = Normalize(withEnvironmentLineEndings);
        ApplyNotebookContent(final, _pendingEngineSource ?? "tool_engine");
    }

    private void ApplyNotebookContent(string? content, string source) {
        _notebookContent = content;
        DebugUtil.Print(DebugCategory, $"[State] notebook updated source={source} length={(content?.Length ?? 0)}");
    }

    private static string? Normalize(string? content) {
        if (string.IsNullOrWhiteSpace(content)) { return null; }

        return content.TrimEnd();
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
