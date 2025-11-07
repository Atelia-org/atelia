using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Agent.Core;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Agent.Text;

/// <summary>
/// 封装 LLM 面向文本的编辑入口，统一处理缓存与换行符归一化。
/// <para>
/// 典型调用流程：构造时从外部读取初始内容 → 对外暴露工具/快照 → 所有编辑操作在内部缓存上生效，
/// 并按需回写至持久层。这样可以保证 LLM 所见内容与底层真实状态之间始终保持同步。
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <strong>执行约束：</strong>调用方必须保证顺序化访问。本类型不提供内部线程安全；当 <see cref="TextChanged"/> 事件正在广播时，任何写入操作都会抛出
/// <see cref="InvalidOperationException"/>，以阻止事件处理器执行递归编辑。事件处理器应视内容为只读快照。
/// </para>
/// </remarks>
public sealed class TextEditorWidget {
    private const string ReplaceToolFormat = "{0}_replace";
    private const string ReplaceSpanToolFormat = "{0}_replace_span";
    private const string AppendToolFormat = "{0}_append";

    private const string DebugCategory = "TextEditorWidget";

    private readonly string _targetTextName;
    private readonly string _baseToolName;
    private readonly Func<string> _readContent;
    private readonly Action<string> _writeContent;
    private readonly TextReplacementEngine _replacementEngine;
    private readonly ImmutableArray<ITool> _tools;
    private readonly string _replaceToolName;
    private readonly string _replaceSpanToolName;
    private readonly string _appendToolName;
    private bool _isNotifying;
    private string _currentText;

    /// <summary>
    /// 当内部缓存被替换且需要通知外部时触发，参数为新的归一化文本内容。
    /// </summary>
    /// <remarks>订阅方抛出的异常会被捕获并记录，但不会阻断写入流程。</remarks>
    public event Action<string>? TextChanged;

    public TextEditorWidget(
        string targetTextName,
        string baseToolName,
        Func<string> readContent,
        Action<string> writeContent
    ) {
        if (string.IsNullOrWhiteSpace(targetTextName)) { throw new ArgumentException("Target text name cannot be null or whitespace.", nameof(targetTextName)); }
        if (string.IsNullOrWhiteSpace(baseToolName)) { throw new ArgumentException("Base tool name cannot be null or whitespace.", nameof(baseToolName)); }

        _targetTextName = targetTextName.Trim();
        _baseToolName = baseToolName.Trim();
        _readContent = readContent ?? throw new ArgumentNullException(nameof(readContent));
        _writeContent = writeContent ?? throw new ArgumentNullException(nameof(writeContent));
        _replaceToolName = string.Format(CultureInfo.InvariantCulture, ReplaceToolFormat, _baseToolName);
        _replaceSpanToolName = string.Format(CultureInfo.InvariantCulture, ReplaceSpanToolFormat, _baseToolName);
        _appendToolName = string.Format(CultureInfo.InvariantCulture, AppendToolFormat, _baseToolName);

        _currentText = ReadFromSource();

        _replacementEngine = new TextReplacementEngine(GetContentForEngine, SetContentFromEngine, _targetTextName);

        var builder = ImmutableArray.CreateBuilder<ITool>(3);
        var formatArgs = new object?[] { _baseToolName, _targetTextName };
        builder.Add(MethodToolWrapper.FromDelegate(AppendAsync, formatArgs));
        builder.Add(MethodToolWrapper.FromDelegate(ReplaceAsync, formatArgs));
        builder.Add(MethodToolWrapper.FromDelegate(ReplaceSpanAsync, formatArgs));
        _tools = builder.ToImmutable();
    }

    public ImmutableArray<ITool> Tools => _tools;

    /// <summary>
    /// 返回供 LLM 或 UI 呈现的快照，使用 Markdown 代码围栏包裹内容。
    /// </summary>
    public string RenderSnapshot() {
        return RenderAsCodeFence(_currentText);
    }

    /// <summary>
    /// 返回真实缓存内容，只做换行符归一化。
    /// </summary>
    public string GetRawSnapshot() {
        return _currentText;
    }

    /// <summary>
    /// 从底层数据源重新加载并覆盖内部缓存，不回写存储但会广播变更事件。
    /// </summary>
    public void Reload() {
        EnsureWritable();
        ApplyNewContent(ReadFromSource(), persistToStore: false, raiseEvent: true);
    }

    /// <summary>
    /// 当宿主（例如 GUI 或外部服务）主动更新文本时调用，确保缓存与行尾策略保持一致。
    /// </summary>
    public void UpdateFromHost(string content) {
        if (content is null) { throw new ArgumentNullException(nameof(content)); }

        var normalized = NormalizeLineEndings(content);

        EnsureWritable();
        ApplyNewContent(normalized, persistToStore: true, raiseEvent: true);
    }

    public static string NormalizeLineEndings(string content) {
        if (content is null) { throw new ArgumentNullException(nameof(content)); }

        return TextToolUtilities.NormalizeLineEndings(content);
    }

    [Tool(AppendToolFormat, "向 {1} 末尾追加新的文本段落；追加逻辑会自动根据现有内容插入所需换行。")]
    private ValueTask<LodToolExecuteResult> AppendAsync(
        [ToolParam("要追加的新文本；为空字符串表示不追加。")] string new_text,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        EnsureWritable();
        var result = ExecuteAppendInternal(new_text);
        return ValueTask.FromResult(result);
    }

    [Tool(ReplaceToolFormat, "在 {1} 中查找并替换文本；支持通过锚点限定搜索范围，执行结果会返回 operation/delta/new_length 等细节。")]
    private ValueTask<LodToolExecuteResult> ReplaceAsync(
        [ToolParam("要替换的旧文本；需与 {1} 内容精确匹配。")] string old_text,
        [ToolParam("替换后的新文本；为空字符串表示删除匹配到的旧文本。")] string new_text,
        [ToolParam("锚点定位文本；如果提供，会从该锚点之后开始搜索旧文本，以区分多次出现的情况。")] string? search_after = null,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        EnsureWritable();
        var result = ExecuteReplaceInternal(old_text, new_text, search_after);
        return ValueTask.FromResult(result);
    }

    [Tool(ReplaceSpanToolFormat, "通过起止标记精确定位 {1} 中的区块并替换；适合多段相似内容时依靠首尾锚点锁定唯一目标，可选 search_after 进一步约束搜索范围。")]
    private ValueTask<LodToolExecuteResult> ReplaceSpanAsync(
        [ToolParam("区块起始标记文本，需与 {1} 内容完全匹配。")] string old_span_start,
        [ToolParam("区块结束标记文本，需与 {1} 内容完全匹配。")] string old_span_end,
        [ToolParam("替换后的新文本，需包含希望保留的首尾标记。")] string new_text,
        [ToolParam("锚点定位文本；提供后，会从锚点之后搜索起始标记，避免多次出现时误替换。")] string? search_after = null,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        EnsureWritable();
        var result = ExecuteReplaceSpanInternal(old_span_start, old_span_end, new_text, search_after);
        return ValueTask.FromResult(result);
    }

    private LodToolExecuteResult ExecuteAppendInternal(string newText) {
        var request = new ReplacementRequest(
            OldText: string.Empty,
            NewText: newText,
            SearchAfter: null,
            IsAppend: true,
            OperationName: _appendToolName
        );

        return ExecuteReplacement(
            request,
            locator: null,
            (previous, updated, engineMessage) => {
                if (string.Equals(previous, updated, StringComparison.Ordinal)) {
                    const string summary = "未追加任何内容：new_text 为空。";
                    var detailMessage = string.IsNullOrEmpty(engineMessage) ? summary : engineMessage;
                    return LodToolExecuteResult.FromContent(
                        ToolExecutionStatus.Success,
                        CreateOperationContent(
                            summary,
                            detailMessage,
                            appended: true,
                            previous.Length,
                            updated.Length,
                            _appendToolName,
                            _targetTextName,
                            searchAfter: null
                        )
                    );
                }

                var summaryMessage = $"已追加新的{_targetTextName}段落。";
                var finalDetail = string.IsNullOrEmpty(engineMessage) ? summaryMessage : engineMessage;
                return LodToolExecuteResult.FromContent(
                    ToolExecutionStatus.Success,
                    CreateOperationContent(
                        summaryMessage,
                        finalDetail,
                        appended: true,
                        previous.Length,
                        updated.Length,
                        _appendToolName,
                        _targetTextName,
                        searchAfter: null
                    )
                );
            }
        );
    }

    private LodToolExecuteResult ExecuteReplaceInternal(string oldText, string newText, string? searchAfter) {
        var normalizedSearchAfter = searchAfter is null ? null : NormalizeLineEndings(searchAfter);
        if (string.IsNullOrEmpty(oldText)) {
            return EngineFailure(
                $"Error: old_text 不能为空；如需追加请改用 {_appendToolName} 工具。",
                _replaceToolName,
                _targetTextName,
                normalizedSearchAfter
            );
        }

        var normalizedOld = NormalizeLineEndings(oldText);

        var request = new ReplacementRequest(
            normalizedOld,
            newText,
            normalizedSearchAfter,
            IsAppend: false,
            _replaceToolName
        );

        var locator = new LiteralRegionLocator(normalizedOld);

        return ExecuteReplacement(
            request,
            locator,
            (previous, updated, engineMessage) => {
                if (string.Equals(previous, updated, StringComparison.Ordinal)) {
                    const string summary = "替换内容未发生变化。";
                    var detailMessage = string.IsNullOrEmpty(engineMessage) ? summary : engineMessage;
                    return LodToolExecuteResult.FromContent(
                        ToolExecutionStatus.Success,
                        CreateOperationContent(
                            summary,
                            detailMessage,
                            appended: false,
                            previous.Length,
                            updated.Length,
                            _replaceToolName,
                            _targetTextName,
                            normalizedSearchAfter
                        )
                    );
                }

                var summaryMessage = $"已完成{_targetTextName}文本替换。";
                var finalDetail = string.IsNullOrEmpty(engineMessage) ? summaryMessage : engineMessage;

                return LodToolExecuteResult.FromContent(
                    ToolExecutionStatus.Success,
                    CreateOperationContent(
                        summaryMessage,
                        finalDetail,
                        appended: false,
                        previous.Length,
                        updated.Length,
                        _replaceToolName,
                        _targetTextName,
                        normalizedSearchAfter
                    )
                );
            }
        );
    }

    private LodToolExecuteResult ExecuteReplaceSpanInternal(string oldSpanStart, string oldSpanEnd, string newText, string? searchAfter) {
        var normalizedStart = NormalizeLineEndings(oldSpanStart);
        var normalizedEnd = NormalizeLineEndings(oldSpanEnd);
        var normalizedSearchAfter = searchAfter is null ? null : NormalizeLineEndings(searchAfter);

        var request = new ReplacementRequest(
            normalizedStart,
            newText,
            normalizedSearchAfter,
            IsAppend: false,
            _replaceSpanToolName
        );

        var locator = new SpanRegionLocator(normalizedStart, normalizedEnd);

        return ExecuteReplacement(
            request,
            locator,
            (previous, updated, engineMessage) => {
                if (string.Equals(previous, updated, StringComparison.Ordinal)) {
                    const string summary = "替换内容未发生变化。";
                    var detailMessage = string.IsNullOrEmpty(engineMessage) ? summary : engineMessage;
                    return LodToolExecuteResult.FromContent(
                        ToolExecutionStatus.Success,
                        CreateOperationContent(
                            summary,
                            detailMessage,
                            appended: false,
                            previous.Length,
                            updated.Length,
                            _replaceSpanToolName,
                            _targetTextName,
                            normalizedSearchAfter
                        )
                    );
                }

                var summaryMessage = $"已完成{_targetTextName}区块替换。";
                var finalDetail = string.IsNullOrEmpty(engineMessage) ? summaryMessage : engineMessage;
                return LodToolExecuteResult.FromContent(
                    ToolExecutionStatus.Success,
                    CreateOperationContent(
                        summaryMessage,
                        finalDetail,
                        appended: false,
                        previous.Length,
                        updated.Length,
                        _replaceSpanToolName,
                        _targetTextName,
                        normalizedSearchAfter
                    )
                );
            }
        );
    }

    private LodToolExecuteResult ExecuteReplacement(
        ReplacementRequest request,
        IRegionLocator? locator,
        Func<string, string, string, LodToolExecuteResult> onSuccess
    ) {
        var previous = GetNormalizedContent();

        var outcome = _replacementEngine.Execute(request, locator);

        if (!outcome.Success) { return EngineFailure(outcome.Message, request.OperationName, _targetTextName, request.SearchAfter); }

        var updated = GetNormalizedContent();
        return onSuccess(previous, updated, outcome.Message);
    }

    private string GetContentForEngine() {
        return _currentText;
    }

    private string GetNormalizedContent() {
        return _currentText;
    }

    /// <summary>
    /// 将编辑引擎的输出来回写到缓存/持久层，并执行换行符归一化。
    /// </summary>
    private void SetContentFromEngine(string content) {
        if (content is null) { throw new ArgumentNullException(nameof(content)); }

        var normalized = NormalizeLineEndings(content);
        ApplyNewContent(normalized, persistToStore: true, raiseEvent: true);
        // 延迟到外层操作统一调度通知
    }

    /// <summary>
    /// 调用外部提供的读取委托，并完成行尾归一化。
    /// </summary>
    private string ReadFromSource() {
        var content = _readContent() ?? string.Empty;
        var normalized = NormalizeLineEndings(content);

        return normalized;
    }

    /// <summary>
    /// 更新内部缓存，可选择是否回写存储或广播事件，所有入口最终都会汇聚到此处。
    /// </summary>
    /// <param name="normalizedContent">已经过换行归一化的文本。</param>
    /// <param name="persistToStore">为 <c>true</c> 时调用写入委托。</param>
    /// <param name="raiseEvent">为 <c>true</c> 时触发 <see cref="TextChanged"/>。</param>
    private void ApplyNewContent(string normalizedContent, bool persistToStore, bool raiseEvent) {
        if (normalizedContent is null) { throw new ArgumentNullException(nameof(normalizedContent)); }

        EnsureWritable();

        var previous = _currentText;
        var contentChanged = !string.Equals(previous, normalizedContent, StringComparison.Ordinal);

        if (!contentChanged) { return; }

        if (persistToStore) {
            try {
                _writeContent(normalizedContent);
            }
            catch (Exception ex) {
                DebugUtil.Print(DebugCategory, $"[PersistFailed] target={_targetTextName} length={normalizedContent.Length} exception={ex.GetType().Name} message={ex.Message}");
                throw;
            }
        }

        _currentText = normalizedContent;

        if (raiseEvent) {
            NotifyTextChanged(_currentText);
        }
    }

    private void NotifyTextChanged(string content) {
        var handlers = TextChanged;
        if (handlers is null) { return; }

        if (_isNotifying) { throw new InvalidOperationException("Nested TextChanged notifications are not supported."); }

        _isNotifying = true;
        try {
            foreach (var handler in handlers.GetInvocationList()) {
                try {
                    ((Action<string>)handler).Invoke(content);
                }
                catch (Exception ex) {
                    DebugUtil.Print(DebugCategory, $"[EventError] target={_targetTextName} length={content.Length} exception={ex.GetType().Name} message={ex.Message}");
                }
            }
        }
        finally {
            _isNotifying = false;
        }
    }

    private void EnsureWritable() {
        if (_isNotifying) { throw new InvalidOperationException("TextEditorWidget is in read-only notification scope; write operations are not allowed."); }
    }

    private static LodToolExecuteResult EngineFailure(string message, string operationName, string targetName, string? searchAfter) {
        var detailBuilder = new StringBuilder();
        detailBuilder.Append(message.TrimEnd());

        var context = BuildContextDetails(operationName, targetName, searchAfter);
        if (!string.IsNullOrEmpty(context)) {
            detailBuilder.Append('\n').Append(context);
        }

        return LodToolExecuteResult.FromContent(
            ToolExecutionStatus.Failed,
            new LevelOfDetailContent(message, detailBuilder.ToString())
        );
    }

    private static LevelOfDetailContent CreateOperationContent(
        string basicMessage,
        string? engineMessage,
        bool appended,
        int previousLength,
        int newLength,
        string operationName,
        string targetName,
        string? searchAfter
    ) {
        var detailBuilder = new StringBuilder();

        if (!string.IsNullOrEmpty(engineMessage)) {
            detailBuilder.Append(engineMessage.TrimEnd());
        }

        var extra = BuildExtraDetails(appended, previousLength, newLength, searchAfter);
        if (!string.IsNullOrEmpty(extra)) {
            if (detailBuilder.Length > 0) {
                detailBuilder.Append('\n');
            }

            detailBuilder.Append(extra);
        }

        var context = BuildContextDetails(operationName, targetName, searchAfter);
        if (!string.IsNullOrEmpty(context)) {
            if (detailBuilder.Length > 0) {
                detailBuilder.Append('\n');
            }

            detailBuilder.Append(context);
        }

        var detail = detailBuilder.Length == 0 ? basicMessage : detailBuilder.ToString();
        return new LevelOfDetailContent(basicMessage, detail);
    }

    private static string BuildExtraDetails(bool appended, int previousLength, int newLength, string? searchAfter) {
        var delta = newLength - previousLength;
        var builder = new StringBuilder();
        builder.Append("- operation: ").Append(appended ? "append" : "replace").Append('\n');
        builder.Append("- delta: ");
        if (delta >= 0) { builder.Append('+'); }
        builder.Append(delta).Append('\n');
        builder.Append("- new_length: ").Append(newLength);

        var anchorText = FormatAnchor(searchAfter);
        if (anchorText is not null) {
            builder.Append('\n');
            builder.Append("- anchor: ").Append(anchorText);
        }

        return builder.ToString();
    }

    private static string BuildContextDetails(string operationName, string targetName, string? searchAfter) {
        var builder = new StringBuilder();
        builder.Append("- tool_operation: ").Append(operationName);
        builder.Append('\n');
        builder.Append("- target: ").Append(targetName);
        builder.Append('\n');
        builder.Append("- search_after: ");

        var anchorText = FormatAnchor(searchAfter);
        builder.Append(anchorText ?? "(none)");

        return builder.ToString();
    }

    private static string RenderAsCodeFence(string content) {
        var fenceLength = Math.Max(GetLongestBacktickRun(content) + 1, 3);
        var fence = new string('`', fenceLength);

        var builder = new StringBuilder();
        builder.Append(fence).Append('\n');
        builder.Append(content);
        if (!content.EndsWith("\n", StringComparison.Ordinal)) {
            builder.Append('\n');
        }
        builder.Append(fence);

        return builder.ToString();
    }

    private static int GetLongestBacktickRun(string content) {
        if (string.IsNullOrEmpty(content)) { return 0; }

        var longest = 0;
        var current = 0;

        foreach (var ch in content) {
            if (ch == '`') {
                current++;
                if (current > longest) {
                    longest = current;
                }
            }
            else {
                current = 0;
            }
        }

        return longest;
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
