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
    private readonly ITool _replaceTool;
    private readonly ITool _replaceSpanTool;
    private readonly ITool _appendTool;
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

        _currentText = ReadFromSource();

        _replacementEngine = new TextReplacementEngine(GetContentForEngine, SetContentFromEngine, _targetTextName);

        var builder = ImmutableArray.CreateBuilder<ITool>(3);
        var formatArgs = new object?[] { _baseToolName, _targetTextName };
        _appendTool = MethodToolWrapper.FromDelegate(AppendAsync, formatArgs);
        _replaceTool = MethodToolWrapper.FromDelegate(ReplaceAsync, formatArgs);
        _replaceSpanTool = MethodToolWrapper.FromDelegate(ReplaceSpanAsync, formatArgs);
        builder.Add(_appendTool);
        builder.Add(_replaceTool);
        builder.Add(_replaceSpanTool);
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

    [Tool(ReplaceToolFormat, "在 {1} 中查找并替换文本；需提供精确匹配的旧文本，执行结果会返回 operation/delta/new_length 等细节。")]
    private ValueTask<LodToolExecuteResult> ReplaceAsync(
        [ToolParam("要替换的旧文本；需与 {1} 内容精确匹配。")] string old_text,
        [ToolParam("替换后的新文本；为空字符串表示删除匹配到的旧文本。")] string new_text,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        EnsureWritable();
        var result = ExecuteReplaceInternal(old_text, new_text);
        return ValueTask.FromResult(result);
    }

    [Tool(ReplaceSpanToolFormat, "通过起止标记精确定位 {1} 中的区块并替换；适合多段相似内容时依靠首尾锚点锁定唯一目标。")]
    private ValueTask<LodToolExecuteResult> ReplaceSpanAsync(
        [ToolParam("区块起始标记文本，需与 {1} 内容完全匹配。")] string old_span_start,
        [ToolParam("区块结束标记文本，需与 {1} 内容完全匹配。")] string old_span_end,
        [ToolParam("替换后的新文本，需包含希望保留的首尾标记。")] string new_text,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        EnsureWritable();
        var result = ExecuteReplaceSpanInternal(old_span_start, old_span_end, new_text);
        return ValueTask.FromResult(result);
    }

    private LodToolExecuteResult ExecuteAppendInternal(string newText) {
        var request = new ReplacementRequest(
            OldText: string.Empty,
            NewText: newText,
            IsAppend: true,
            OperationName: _appendTool.Name
        );

        return ExecuteReplacement(
            request,
            locator: null,
            (previous, updated, engineMessage) => {
                var extraMessage = string.IsNullOrEmpty(engineMessage) ? null : engineMessage;

                if (string.Equals(previous, updated, StringComparison.Ordinal)) {
                    const string noChangeSummary = "未追加任何内容";
                    return new LodToolExecuteResult(
                        ToolExecutionStatus.Success,
                        CreateSuccessContent(
                            noChangeSummary,
                            operation: "append",
                            _targetTextName,
                            previous.Length,
                            updated.Length,
                            extraMessage
                        )
                    );
                }

                var summary = $"✓ 已向{_targetTextName}追加内容";
                return new LodToolExecuteResult(
                    ToolExecutionStatus.Success,
                    CreateSuccessContent(
                        summary,
                        operation: "append",
                        _targetTextName,
                        previous.Length,
                        updated.Length,
                        extraMessage
                    )
                );
            }
        );
    }

    private LodToolExecuteResult ExecuteReplaceInternal(string oldText, string newText) {
        if (string.IsNullOrEmpty(oldText)) {
            return EngineFailure(
                $"old_text 不能为空；如需追加请改用 {_appendTool.Name} 工具。",
                _replaceTool.Name,
                _targetTextName
            );
        }

        var normalizedOld = NormalizeLineEndings(oldText);

        var request = new ReplacementRequest(
            normalizedOld,
            newText,
            IsAppend: false,
            _replaceTool.Name
        );

        var locator = new LiteralRegionLocator(normalizedOld);

        return ExecuteReplacement(
            request,
            locator,
            (previous, updated, engineMessage) => {
                var extraMessage = string.IsNullOrEmpty(engineMessage) ? null : engineMessage;

                if (string.Equals(previous, updated, StringComparison.Ordinal)) {
                    const string noChangeSummary = "替换内容未发生变化";
                    return new LodToolExecuteResult(
                        ToolExecutionStatus.Success,
                        CreateSuccessContent(
                            noChangeSummary,
                            operation: "replace",
                            _targetTextName,
                            previous.Length,
                            updated.Length,
                            extraMessage
                        )
                    );
                }

                var summary = $"✓ 已完成{_targetTextName}文本替换";

                return new LodToolExecuteResult(
                    ToolExecutionStatus.Success,
                    CreateSuccessContent(
                        summary,
                        operation: "replace",
                        _targetTextName,
                        previous.Length,
                        updated.Length,
                        extraMessage
                    )
                );
            }
        );
    }

    private LodToolExecuteResult ExecuteReplaceSpanInternal(string oldSpanStart, string oldSpanEnd, string newText) {
        var normalizedStart = NormalizeLineEndings(oldSpanStart);
        var normalizedEnd = NormalizeLineEndings(oldSpanEnd);

        var request = new ReplacementRequest(
            normalizedStart,
            newText,
            IsAppend: false,
            _replaceSpanTool.Name
        );

        var locator = new SpanRegionLocator(normalizedStart, normalizedEnd);

        return ExecuteReplacement(
            request,
            locator,
            (previous, updated, engineMessage) => {
                var extraMessage = string.IsNullOrEmpty(engineMessage) ? null : engineMessage;

                if (string.Equals(previous, updated, StringComparison.Ordinal)) {
                    const string noChangeSummary = "替换内容未发生变化";
                    return new LodToolExecuteResult(
                        ToolExecutionStatus.Success,
                        CreateSuccessContent(
                            noChangeSummary,
                            operation: "replace_span",
                            _targetTextName,
                            previous.Length,
                            updated.Length,
                            extraMessage
                        )
                    );
                }

                var summary = $"✓ 已完成{_targetTextName}区块替换";
                return new LodToolExecuteResult(
                    ToolExecutionStatus.Success,
                    CreateSuccessContent(
                        summary,
                        operation: "replace_span",
                        _targetTextName,
                        previous.Length,
                        updated.Length,
                        extraMessage
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

        if (!outcome.Success) { return EngineFailure(outcome.Message, request.OperationName, _targetTextName); }

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

    private static LodToolExecuteResult EngineFailure(string message, string operationName, string targetName) {
        var summary = string.IsNullOrWhiteSpace(message) ? "✗ 操作失败" : $"✗ {message}";

        var detailBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(message)) {
            detailBuilder.Append(message.TrimEnd());
            detailBuilder.Append('\n');
        }

        detailBuilder.Append("- target: ").Append(targetName).Append('\n');
        detailBuilder.Append("- tool_operation: ").Append(operationName);

        return new LodToolExecuteResult(
            ToolExecutionStatus.Failed,
            new LevelOfDetailContent(summary, detailBuilder.ToString())
        );
    }

    private static LevelOfDetailContent CreateSuccessContent(
        string summary,
        string operation,
        string targetName,
        int previousLength,
        int newLength,
        string? extraMessage
    ) {
        var detailBuilder = new StringBuilder();

        if (!string.IsNullOrEmpty(extraMessage)) {
            detailBuilder.Append(extraMessage.TrimEnd());
            detailBuilder.Append('\n');
        }

        detailBuilder.Append("- target: ").Append(targetName).Append('\n');
        detailBuilder.Append("- operation: ").Append(operation).Append('\n');
        detailBuilder.Append("- delta: ").Append(FormatDelta(newLength - previousLength)).Append('\n');
        detailBuilder.Append("- new_length: ").Append(newLength);

        return new LevelOfDetailContent(summary, detailBuilder.ToString());
    }

    private static string FormatDelta(int delta) {
        return delta >= 0
            ? "+" + delta.ToString(CultureInfo.InvariantCulture)
            : delta.ToString(CultureInfo.InvariantCulture);
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

}
