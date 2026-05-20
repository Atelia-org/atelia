// DocGraph v0.1 - 验证结果模型
// 参考：api.md §2.3 验证结果 (ValidationResult)

namespace Atelia.DocGraph.Core;

/// <summary>
/// 文档关系验证结果。
/// </summary>
public class ValidationResult {
    /// <summary>
    /// 扫描统计。
    /// </summary>
    public ScanStatistics Statistics { get; }

    /// <summary>
    /// 验证问题列表（按严重度排序）。
    /// </summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>
    /// 修复结果（仅当启用修复模式时有值）。
    /// </summary>
    public IReadOnlyList<Fix.FixResult>? FixResults { get; }

    /// <summary>
    /// 是否通过验证（无Error/Fatal级别问题）。
    /// </summary>
    public bool IsValid => !Issues.Any(i => i.Severity is IssueSeverity.Error or IssueSeverity.Fatal);

    /// <summary>
    /// 是否有警告。
    /// </summary>
    public bool HasWarnings => Issues.Any(i => i.Severity == IssueSeverity.Warning);

    /// <summary>
    /// 创建验证结果。
    /// 遵循 [A-DOCGRAPH-006]：按严重度、错误码、源文件路径、目标文件路径、行号排序。
    /// </summary>
    public ValidationResult(
        ScanStatistics statistics,
        IEnumerable<ValidationIssue> issues,
        IEnumerable<Fix.FixResult>? fixResults = null
    ) {
        Statistics = statistics;
        Issues = issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.ErrorCode, StringComparer.Ordinal)
            .ThenBy(i => i.FilePath, StringComparer.Ordinal)
            .ThenBy(i => i.TargetFilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(i => i.LineNumber ?? 0)
            .ToList();
        FixResults = fixResults?.ToList();
    }
}

/// <summary>
/// 扫描统计信息。
/// </summary>
public class ScanStatistics {
    /// <summary>
    /// 扫描的总文件数。
    /// </summary>
    public int TotalFiles { get; init; }

    /// <summary>
    /// Wish 文档数量。
    /// </summary>
    public int WishDocuments { get; init; }

    /// <summary>
    /// 产物文档数量。
    /// </summary>
    public int ProductDocuments { get; init; }

    /// <summary>
    /// 总关系数量。
    /// </summary>
    public int TotalRelations { get; init; }

    /// <summary>
    /// 扫描耗时。
    /// </summary>
    public TimeSpan ElapsedTime { get; init; }
}

/// <summary>
/// 验证问题。
/// </summary>
public class ValidationIssue {
    /// <summary>
    /// 问题严重度。
    /// </summary>
    public IssueSeverity Severity { get; }

    /// <summary>
    /// 错误码（格式：DOCGRAPH_{CATEGORY}_{DESCRIPTION}）。
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// 问题描述。
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// 发生问题的文件路径（源文件）。
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// 目标文件路径（仅用于关系类问题，如悬空引用）。
    /// 遵循 [A-DOCGRAPH-006]：支持按目标文件路径排序。
    /// </summary>
    public string? TargetFilePath { get; }

    /// <summary>
    /// 行号（可选）。
    /// </summary>
    public int? LineNumber { get; }

    /// <summary>
    /// 列号（可选）。
    /// </summary>
    public int? ColumnNumber { get; }

    /// <summary>
    /// 代码片段（可选）。
    /// </summary>
    public string? CodeSnippet { get; }

    /// <summary>
    /// 快速建议（5秒能理解）。
    /// </summary>
    public string QuickSuggestion { get; }

    /// <summary>
    /// 详细建议（30秒能修复）。
    /// </summary>
    public string DetailedSuggestion { get; }

    /// <summary>
    /// 参考链接（可选，按需深入）。
    /// </summary>
    public string? ReferenceUrl { get; }

    /// <summary>
    /// 创建验证问题。
    /// </summary>
    public ValidationIssue(
        IssueSeverity severity,
        string errorCode,
        string message,
        string filePath,
        string quickSuggestion,
        string detailedSuggestion,
        string? targetFilePath = null,
        int? lineNumber = null,
        int? columnNumber = null,
        string? codeSnippet = null,
        string? referenceUrl = null
    ) {
        Severity = severity;
        ErrorCode = errorCode;
        Message = message;
        FilePath = filePath;
        TargetFilePath = targetFilePath;
        QuickSuggestion = quickSuggestion;
        DetailedSuggestion = detailedSuggestion;
        LineNumber = lineNumber;
        ColumnNumber = columnNumber;
        CodeSnippet = codeSnippet;
        ReferenceUrl = referenceUrl;
    }
}

/// <summary>
/// 问题严重度。
/// 参考：spec.md §5.3 错误聚合与退出码
/// </summary>
public enum IssueSeverity {
    /// <summary>
    /// 🔵 [FYI] 信息性提示。
    /// </summary>
    Info = 0,

    /// <summary>
    /// 🟡 [SHOULD FIX] 建议修复。
    /// </summary>
    Warning = 1,

    /// <summary>
    /// 🔴 [MUST FIX] 必须修复。
    /// </summary>
    Error = 2,

    /// <summary>
    /// ❌ [FATAL] 致命错误，无法继续。
    /// </summary>
    Fatal = 3
}
