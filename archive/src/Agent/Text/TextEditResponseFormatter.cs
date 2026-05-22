using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Atelia.Agent.Text;

/// <summary>
/// 为 TextEditor2Widget 工具生成符合规范的结构化 Markdown 响应。
/// </summary>
/// <remarks>
/// 响应格式固定包含四个段落（按顺序）：
/// 状态头部：status → state → flags（三行固定顺序）
/// 概览：summary 与 guidance（列表项键名固定）
/// 指标：delta、new_length、selection_count 表格
/// 候选选区：可选，存在虚拟选区时输出表格
/// 所有字段值使用内联代码格式，表头和键名固定为中文，以保持与文档规范一致。
/// </remarks>
public static class TextEditResponseFormatter {

    /// <summary>
    /// 生成完整的 Markdown 响应。
    /// </summary>
    /// <param name="status">操作状态。</param>
    /// <param name="workflowState">工作流状态。</param>
    /// <param name="flags">附加标志位。</param>
    /// <param name="summary">概览摘要（单行）。</param>
    /// <param name="guidance">下一步操作建议（可为 null）。</param>
    /// <param name="metrics">度量指标。</param>
    /// <param name="candidates">候选选区列表（可为 null）。</param>
    /// <returns>符合规范的 Markdown 文本。</returns>
    public static string FormatResponse(
        TextEditStatus status,
        TextEditWorkflowState workflowState,
        TextEditFlag flags,
        string summary,
        string? guidance,
        TextEditMetrics metrics,
        IReadOnlyList<TextEditCandidate>? candidates
    ) {
        var builder = new StringBuilder();

        // 1. 状态头部（三行固定顺序）
        builder.AppendLine($"status: `{status}`");
        builder.AppendLine($"state: `{workflowState}`");
        builder.AppendLine($"flags: {flags.FormatForMarkdown()}");
        builder.AppendLine();

        // 2. 概览（带视觉符号的三级标题）
        var statusIcon = GetStatusIcon(status);
        builder.AppendLine($"### [{statusIcon}] 概览");
        builder.AppendLine($"- summary: {summary}");
        builder.AppendLine($"- guidance: {guidance ?? "(留空)"}");
        builder.AppendLine();

        // 3. 指标表格
        builder.AppendLine("### [Metrics] 指标");
        builder.AppendLine("| 指标 | 值 |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| delta | {FormatDelta(metrics.Delta)} |");
        builder.AppendLine($"| new_length | {metrics.NewLength.ToString(CultureInfo.InvariantCulture)} |");
        builder.AppendLine($"| selection_count | {FormatSelectionCount(metrics.SelectionCount)} |");

        // 4. 候选选区表格（可选）
        if (candidates is not null && candidates.Count > 0) {
            builder.AppendLine();
            builder.AppendLine("### [Target] 候选选区");
            builder.AppendLine("| Id | MarkerStart | MarkerEnd | Preview | Occurrence | ContextStart | ContextEnd |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

            foreach (var candidate in candidates) {
                // 使用动态长度的反引号包装，确保候选内容中即便包含反引号也能生成合法的 Markdown。
                var markerStartCell = WrapInlineCode(candidate.MarkerStart);
                var markerEndCell = WrapInlineCode(candidate.MarkerEnd);
                var previewCell = WrapInlineCode(EscapeMarkdownTableCell(candidate.Preview));

                builder.AppendLine(
                    $"| {candidate.Id.ToString(CultureInfo.InvariantCulture)} " +
                    $"| {markerStartCell} " +
                    $"| {markerEndCell} " +
                    $"| {previewCell} " +
                    $"| {candidate.Occurrence.ToString(CultureInfo.InvariantCulture)} " +
                    $"| {candidate.ContextStart.ToString(CultureInfo.InvariantCulture)} " +
                    $"| {candidate.ContextEnd.ToString(CultureInfo.InvariantCulture)} |"
                );
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 根据状态返回对应的视觉符号。
    /// </summary>
    private static string GetStatusIcon(TextEditStatus status) {
        return status switch {
            TextEditStatus.Success => "OK",
            TextEditStatus.NoOp => "OK",
            TextEditStatus.MultiMatch => "Warning",
            TextEditStatus.NoMatch => "Fail",
            TextEditStatus.PersistFailure => "Fail",
            TextEditStatus.ExternalConflict => "Fail",
            TextEditStatus.Exception => "Fail",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// 格式化 delta 值，正数带 + 前缀。
    /// </summary>
    private static string FormatDelta(int delta) {
        if (delta >= 0) { return $"+{delta.ToString(CultureInfo.InvariantCulture)}"; }
        return delta.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 格式化 selection_count，null 时显示 "-"。
    /// </summary>
    private static string FormatSelectionCount(int? count) {
        return count.HasValue
            ? count.Value.ToString(CultureInfo.InvariantCulture)
            : "-";
    }

    /// <summary>
    /// 转义 Markdown 表格单元格中的特殊字符（主要是管道符和换行）。
    /// </summary>
    private static string EscapeMarkdownTableCell(string text) {
        if (string.IsNullOrEmpty(text)) { return string.Empty; }

        // 替换换行为 \n，替换管道符为转义形式
        return text
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n")
            .Replace("|", "\\|");
    }

    /// <summary>
    /// 根据内容动态选择反引号定界符，保证 Markdown 内联代码片段始终合法。
    /// </summary>
    private static string WrapInlineCode(string text) {
        var value = text ?? string.Empty;
        if (value.Length == 0) {
            // 参照 CommonMark 规范，空内容使用两条反引号可以兼容所有渲染器。
            return "``";
        }

        var longestRun = GetLongestBacktickRun(value);
        var delimiterLength = longestRun + 1;
        var delimiter = new string('`', delimiterLength);

        if (longestRun == 0) { return string.Concat(delimiter, value, delimiter); }

        // 当内容本身包含反引号时，需要在定界符内补空格，避免渲染器误判边界（CommonMark §6.5）。
        return string.Concat(delimiter, " ", value, " ", delimiter);
    }

    private static int GetLongestBacktickRun(string content) {
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
