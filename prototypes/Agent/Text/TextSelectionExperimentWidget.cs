using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Agent.Core;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Text;

/// <summary>
/// 实验性的文本编辑 Widget，提供基于选区的多匹配处理能力，方便 LLM 在没有真实光标的场景中继续交互。
/// </summary>
/// <remarks>
/// 本组件的目标仅是验证“在不引入特殊 token 的情况下，为 LLM 提供选区/光标/高亮 overlay UI”的可行性。
/// 因此它独占维护内部文本缓存，不与外部存储交互，也不会尝试双向同步。
/// </remarks>
public sealed class TextSelectionExperimentWidget {
    private const string ReplaceToolFormat = "{0}_replace";
    private const string ReplaceSelectionToolFormat = "{0}_replace_selection";

    private const int MaxSelectableMatches = 5;

    private readonly string _targetTextName;
    private readonly string _baseToolName;
    private readonly ITool _replaceTool;
    private readonly ITool _replaceSelectionTool;
    private readonly ImmutableArray<ITool> _tools;
    // NOTE: 这个实验组件故意独占内部文本状态，不回写底层存储。
    // 目标是验证“不引入特殊 token 的情况下，通过快照和虚拟选区为 LLM 提供 overlay 交互”的可行性，
    // 因此把其他复杂度全部屏蔽，仅在内存中维护文本。
    private string _currentText;
    private SelectionState? _activeSelectionState;
    private bool _isNotifying;

    public TextSelectionExperimentWidget(
        string targetTextName,
        string baseToolName,
        string? initialContent = null
    ) {
        if (string.IsNullOrWhiteSpace(targetTextName)) { throw new ArgumentException("Target text name cannot be null or whitespace.", nameof(targetTextName)); }
        if (string.IsNullOrWhiteSpace(baseToolName)) { throw new ArgumentException("Base tool name cannot be null or whitespace.", nameof(baseToolName)); }

        _targetTextName = targetTextName.Trim();
        _baseToolName = baseToolName.Trim();
        _currentText = TextToolUtilities.NormalizeLineEndings(initialContent);

        var formatArgs = new object?[] { _baseToolName, _targetTextName };
        _replaceTool = MethodToolWrapper.FromDelegate(ReplaceAsync, formatArgs);
        _replaceSelectionTool = MethodToolWrapper.FromDelegate(ReplaceSelectionAsync, formatArgs);
        _tools = [_replaceTool, _replaceSelectionTool];
    }

    public ImmutableArray<ITool> Tools => _tools;

    /// <summary>
    /// 当内部文本被修改时广播通知。
    /// </summary>
    public event Action<string>? TextChanged;

    /// <summary>
    /// 返回供呈现用的快照；若存在待确认选区，会附带 overlay。
    /// </summary>
    public string RenderSnapshot() {
        if (TryGetActiveSelectionState(out var state)) {
            var legend = BuildLegend(state);
            var contentWithMarkers = InsertMarkers(state.ContentSnapshot, state.Entries);

            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(legend)) {
                builder.AppendLine(legend);
                builder.AppendLine();
            }

            builder.Append(RenderAsCodeFence(contentWithMarkers));
            return builder.ToString();
        }

        return RenderAsCodeFence(_currentText);
    }

    /// <summary>
    /// 返回内部缓存的真实内容（也是当前唯一的权威状态）。
    /// </summary>
    public string GetRawSnapshot() => _currentText;

    [Tool(ReplaceToolFormat, "替换 {1} 中的文本；若命中多处，将自动生成虚拟选区并在快照中高亮。")]
    private ValueTask<LodToolExecuteResult> ReplaceAsync(
        [ToolParam("要替换的旧文本；需与 {1} 内容精确匹配。")] string old_text,
        [ToolParam("替换后的新文本；为空字符串表示删除匹配到的旧文本。")] string new_text,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritable();

        // replace 会使上一轮虚拟选区失效，以保持 selection_id 与当前快照一致。
        ClearSelectionState();

        if (string.IsNullOrEmpty(old_text)) {
            var message = "Error: old_text 不能为空。";
            return ValueTask.FromResult(Failure(message));
        }

        var normalizedOld = TextToolUtilities.NormalizeLineEndings(old_text);
        if (normalizedOld.Length == 0) {
            var message = "Error: old_text 不能为空白字符。";
            return ValueTask.FromResult(Failure(message));
        }

        var normalizedNew = TextToolUtilities.NormalizeLineEndings(new_text ?? string.Empty);

        var positions = FindOccurrences(_currentText, normalizedOld);
        if (positions.Count == 0) {
            var message = "未找到要替换的文本。";
            return ValueTask.FromResult(Failure(message));
        }

        if (positions.Count == 1) {
            var previousLength = _currentText.Length;
            var updated = ReplaceSegment(_currentText, positions[0], normalizedOld.Length, normalizedNew);
            var newLength = updated.Length;

            ClearSelectionState();
            ApplyNewContent(updated, raiseEvent: true);

            var summary = $"已在 {_targetTextName} 中完成替换。";
            var detailText = CreateDeltaDetail(summary, previousLength, newLength);
            return ValueTask.FromResult(Success(summary, detailText));
        }
        var hasMoreMatches = positions.Count > MaxSelectableMatches;
        var positionsForSelection = hasMoreMatches
            ? positions.GetRange(0, MaxSelectableMatches)
            : positions;

        var selectionState = BuildSelectionState(normalizedOld, normalizedNew, positionsForSelection);
        _activeSelectionState = selectionState;

        var overlayDetail = BuildOverlayDetail(selectionState);
        var summaryMessage = hasMoreMatches
            ? $"检测到 {_targetTextName} 中多处匹配，已展示前 {MaxSelectableMatches} 个选区。"
            : $"检测到 {_targetTextName} 中的多处匹配，已生成选区。";

        var detailBuilder = new StringBuilder();
        detailBuilder.AppendLine(summaryMessage);
        if (hasMoreMatches) {
            var remainingMatches = positions.Count - MaxSelectableMatches;
            detailBuilder.AppendLine($"提示：共有 {positions.Count} 处匹配，剩余 {remainingMatches} 处未展示，可尝试提供更具体的 old_text 以缩小范围。");
            detailBuilder.AppendLine();
        }
        detailBuilder.AppendLine($"请调用 {_replaceSelectionTool.Name} 工具并指定 selection_id 完成替换。");
        detailBuilder.AppendLine();
        detailBuilder.Append(overlayDetail);

        if (hasMoreMatches) {
            var hiddenPositions = positions.GetRange(MaxSelectableMatches, positions.Count - MaxSelectableMatches);
            var hiddenPreview = TextToolUtilities.FormatMatchesForError(hiddenPositions, _currentText, normalizedOld.Length);
            if (!string.IsNullOrEmpty(hiddenPreview)) {
                detailBuilder.AppendLine();
                detailBuilder.AppendLine("未展示匹配的上下文：");
                detailBuilder.AppendLine(hiddenPreview);
            }
        }

        return ValueTask.FromResult(Success(summaryMessage, detailBuilder.ToString()));
    }

    [Tool(ReplaceSelectionToolFormat, "选定某个虚拟选区并执行替换；new_text 可省略以沿用上一次输入。")]
    private ValueTask<LodToolExecuteResult> ReplaceSelectionAsync(
        [ToolParam("要替换的选区编号。")] int selection_id,
        [ToolParam("新的替换文本；省略时沿用上一轮 replace 输入的 new_text。")] string? new_text = null,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritable();

        if (!TryGetActiveSelectionState(out var state)) {
            var message = $"当前没有待处理的选区，请先调用 {_replaceTool.Name} 工具生成选区。";
            return ValueTask.FromResult(Failure(message));
        }

        if (!TryFindEntry(state, selection_id, out var entry)) {
            var message = $"未找到编号为 #{selection_id} 的选区，或选区已失效。";
            return ValueTask.FromResult(Failure(message));
        }

        var replacement = new_text is not null
            ? TextToolUtilities.NormalizeLineEndings(new_text)
            : state.DefaultReplacement;

        if (replacement is null) {
            var message = $"未提供 new_text，且上一轮 {_replaceTool.Name} 未记录默认替换文本。";
            return ValueTask.FromResult(Failure(message));
        }

        if (entry.StartIndex < 0 || entry.StartIndex > _currentText.Length) {
            var message = "选区对应的文本已发生变化，请重新生成选区。";
            ClearSelectionState();
            return ValueTask.FromResult(Failure(message));
        }

        int currentIndex;
        if (SubstringEquals(_currentText, entry.StartIndex, state.Needle)) {
            currentIndex = entry.StartIndex;
        }
        else {
            currentIndex = FindNthOccurrence(_currentText, state.Needle, entry.OccurrenceNumber);
            if (currentIndex < 0) {
                var message = "选区对应的文本已发生变化，请重新生成选区。";
                ClearSelectionState();
                return ValueTask.FromResult(Failure(message));
            }

            if (!SubstringEquals(_currentText, currentIndex, state.Needle)) {
                var message = "选区对应的文本已发生变化，请重新生成选区。";
                ClearSelectionState();
                return ValueTask.FromResult(Failure(message));
            }
        }

        var previousLength = _currentText.Length;
        var updated = ReplaceSegment(_currentText, currentIndex, state.Needle.Length, replacement);
        var newLength = updated.Length;

        ClearSelectionState();
        ApplyNewContent(updated, raiseEvent: true);

        var summary = $"已替换选区 #{selection_id}。";
        var detailBuilder = new StringBuilder(CreateDeltaDetail(summary, previousLength, newLength));
        if (currentIndex != entry.StartIndex) {
            detailBuilder.AppendLine();
            detailBuilder.Append("- selection_offset: ");
            detailBuilder.Append((currentIndex - entry.StartIndex).ToString(CultureInfo.InvariantCulture));
        }

        return ValueTask.FromResult(Success(summary, detailBuilder.ToString()));
    }

    private void ApplyNewContent(string normalizedContent, bool raiseEvent) {
        if (normalizedContent is null) { throw new ArgumentNullException(nameof(normalizedContent)); }

        var normalized = TextToolUtilities.NormalizeLineEndings(normalizedContent);
        if (string.Equals(_currentText, normalized, StringComparison.Ordinal)) { return; }

        _currentText = normalized;

        if (raiseEvent) {
            NotifyTextChanged(_currentText);
        }
    }

    private void NotifyTextChanged(string content) {
        var handlers = TextChanged;
        if (handlers is null) { return; }

        if (_isNotifying) { throw new InvalidOperationException("Nested TextChanged notifications are not supported."); }

        try {
            _isNotifying = true;
            handlers.Invoke(content);
        }
        finally {
            _isNotifying = false;
        }
    }

    private void EnsureWritable() {
        if (_isNotifying) { throw new InvalidOperationException("Widget is in notification scope; write operations are not allowed."); }
    }

    private void ClearSelectionState() {
        _activeSelectionState = null;
    }

    private bool TryGetActiveSelectionState(out SelectionState state) {
        var current = _activeSelectionState;
        if (current is null) {
            state = default!;
            return false;
        }

        if (!string.Equals(current.ContentSnapshot, _currentText, StringComparison.Ordinal)) {
            _activeSelectionState = null;
            state = default!;
            return false;
        }

        state = current;
        return true;
    }

    private SelectionState BuildSelectionState(string needle, string defaultReplacement, IReadOnlyList<int> positions) {
        var entriesBuilder = ImmutableArray.CreateBuilder<SelectionEntry>(positions.Count);
        var usedMarkers = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < positions.Count; i++) {
            var id = i + 1;
            var (startMarker, endMarker) = CreateMarkerPair(_currentText, id, usedMarkers);
            entriesBuilder.Add(
                new SelectionEntry(
                    PublicId: id,
                    OccurrenceNumber: i,
                    StartIndex: positions[i],
                    Length: needle.Length,
                    StartMarker: startMarker,
                    EndMarker: endMarker
                )
            );
        }

        return new SelectionState(_currentText, needle, defaultReplacement, entriesBuilder.ToImmutable());
    }

    private static (string StartMarker, string EndMarker) CreateMarkerPair(string content, int baseId, HashSet<string> usedMarkers) {
        var suffixCounter = 0;

        while (true) {
            var suffix = suffixCounter == 0
                ? baseId.ToString(CultureInfo.InvariantCulture)
                : string.Concat(
                    baseId.ToString(CultureInfo.InvariantCulture),
                    "_",
                    suffixCounter.ToString(CultureInfo.InvariantCulture)
                );

            var startMarker = $"[[SEL#{suffix}]]";
            var endMarker = $"[[/SEL#{suffix}]]";

            if (!content.Contains(startMarker, StringComparison.Ordinal)
                && !content.Contains(endMarker, StringComparison.Ordinal)
                && !usedMarkers.Contains(startMarker)
                && !usedMarkers.Contains(endMarker)) {
                usedMarkers.Add(startMarker);
                usedMarkers.Add(endMarker);
                return (startMarker, endMarker);
            }

            suffixCounter++;
        }
    }

    private static string BuildOverlayDetail(SelectionState state) {
        var legend = BuildLegend(state);
        var markedContent = InsertMarkers(state.ContentSnapshot, state.Entries);

        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(legend)) {
            builder.AppendLine(legend);
            builder.AppendLine();
        }

        builder.Append(RenderAsCodeFence(markedContent));
        return builder.ToString();
    }

    private static string BuildLegend(SelectionState state) {
        if (state.Entries.Length == 0) { return string.Empty; }

        var builder = new StringBuilder();
        builder.AppendLine("选区图例（匹配按非重叠语义计算，行为与大多数正则替换一致）");

        foreach (var entry in state.Entries) {
            var preview = TextToolUtilities.GetContext(state.ContentSnapshot, entry.StartIndex, entry.Length, contextSize: 40)
                .Replace("\n", "\\n");

            builder.Append("- #");
            builder.Append(entry.PublicId.ToString(CultureInfo.InvariantCulture));
            builder.Append(": start=");
            builder.Append(entry.StartIndex.ToString(CultureInfo.InvariantCulture));
            builder.Append(", length=");
            builder.Append(entry.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(", preview=\"");
            builder.Append(preview);
            builder.Append('"');
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string InsertMarkers(string content, ImmutableArray<SelectionEntry> entries) {
        if (entries.Length == 0) { return content; }

        var builder = new StringBuilder(content.Length + entries.Length * 20);
        var cursor = 0;

        foreach (var entry in entries) {
            builder.Append(content, cursor, entry.StartIndex - cursor);
            builder.Append(entry.StartMarker);
            builder.Append(content, entry.StartIndex, entry.Length);
            builder.Append(entry.EndMarker);
            cursor = entry.StartIndex + entry.Length;
        }

        builder.Append(content, cursor, content.Length - cursor);
        return builder.ToString();
    }

    private static bool TryFindEntry(SelectionState state, int selectionId, out SelectionEntry entry) {
        foreach (var candidate in state.Entries) {
            if (candidate.PublicId == selectionId) {
                entry = candidate;
                return true;
            }
        }

        entry = default;
        return false;
    }

    // 使用与主流正则引擎一致的非重叠匹配语义：命中后从匹配尾部继续搜索，忽略潜在的重叠片段。
    private static List<int> FindOccurrences(string text, string pattern) {
        var positions = new List<int>();
        if (string.IsNullOrEmpty(pattern)) { return positions; }

        var index = 0;
        while (true) {
            index = text.IndexOf(pattern, index, StringComparison.Ordinal);
            if (index < 0) { break; }

            positions.Add(index);
            index += pattern.Length;
        }

        return positions;
    }

    private static int FindNthOccurrence(string text, string pattern, int occurrenceNumber) {
        if (occurrenceNumber < 0) { return -1; }

        var index = 0;
        var found = 0;

        while (true) {
            index = text.IndexOf(pattern, index, StringComparison.Ordinal);
            if (index < 0) { return -1; }

            if (found == occurrenceNumber) { return index; }

            found++;
            index += pattern.Length;
        }
    }

    private static string ReplaceSegment(string text, int start, int length, string replacement) {
        var builder = new StringBuilder(text.Length - length + replacement.Length);
        builder.Append(text, 0, start);
        builder.Append(replacement);
        builder.Append(text, start + length, text.Length - (start + length));
        return builder.ToString();
    }

    private static bool SubstringEquals(string text, int startIndex, string value) {
        if (startIndex < 0 || startIndex + value.Length > text.Length) { return false; }
        for (var i = 0; i < value.Length; i++) {
            if (text[startIndex + i] != value[i]) { return false; }
        }
        return true;
    }

    private static string RenderAsCodeFence(string content) {
        var fenceLength = Math.Max(GetLongestBacktickRun(content) + 1, 3);
        var fence = new string('`', fenceLength);

        var builder = new StringBuilder(content.Length + fenceLength * 2 + 3);
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

    private static string CreateDeltaDetail(string summary, int previousLength, int newLength) {
        var delta = newLength - previousLength;
        var builder = new StringBuilder();
        builder.AppendLine(summary);
        builder.Append("- delta: ");
        if (delta >= 0) {
            builder.Append('+');
        }
        builder.Append(delta.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append("- new_length: ");
        builder.Append(newLength.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static LodToolExecuteResult Success(string summary, string detail) {
        return LodToolExecuteResult.FromContent(
            ToolExecutionStatus.Success,
            new LevelOfDetailContent(summary, detail)
        );
    }

    private static LodToolExecuteResult Failure(string message) {
        return LodToolExecuteResult.FromContent(
            ToolExecutionStatus.Failed,
            new LevelOfDetailContent(message, message)
        );
    }

    private sealed class SelectionState {
        public SelectionState(string contentSnapshot, string needle, string defaultReplacement, ImmutableArray<SelectionEntry> entries) {
            ContentSnapshot = contentSnapshot;
            Needle = needle;
            DefaultReplacement = defaultReplacement;
            Entries = entries;
        }

        public string ContentSnapshot { get; }
        public string Needle { get; }
        public string DefaultReplacement { get; }
        public ImmutableArray<SelectionEntry> Entries { get; }
    }

    private readonly record struct SelectionEntry(
        int PublicId,
        int OccurrenceNumber,
        int StartIndex,
        int Length,
        string StartMarker,
        string EndMarker
    );
}
