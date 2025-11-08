using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Atelia.Agent.Text;

/// <summary>
/// 单次文本编辑操作的即时结果状态。
/// </summary>
public enum TextEditStatus {
    /// <summary>
    /// 操作成功完成。
    /// </summary>
    Success,

    /// <summary>
    /// 未找到要替换的文本。
    /// </summary>
    NoMatch,

    /// <summary>
    /// 检测到多处匹配，需要选择具体选区。
    /// </summary>
    MultiMatch,

    /// <summary>
    /// 操作未产生实际变更。
    /// </summary>
    NoOp,

    /// <summary>
    /// 持久化到底层存储时失败。
    /// </summary>
    PersistFailure,

    /// <summary>
    /// 检测到外部冲突（底层数据已变更）。
    /// </summary>
    ExternalConflict,

    /// <summary>
    /// 操作过程中发生异常。
    /// </summary>
    Exception
}

/// <summary>
/// TextEditor2Widget 的工作流状态，表示当前的持久状态。
/// </summary>
public enum TextEditWorkflowState {
    /// <summary>
    /// 缓存与底层文本同步，无挂起操作。
    /// </summary>
    Idle,

    /// <summary>
    /// 多匹配待确认，等待 replace_selection 或 discard。
    /// </summary>
    SelectionPending,

    /// <summary>
    /// 缓存已修改但尚未提交（Manual 模式）。
    /// </summary>
    PersistPending,

    /// <summary>
    /// 缓存与底层不一致（提交失败或外部写入）。
    /// </summary>
    OutOfSync,

    /// <summary>
    /// 正在刷新底层快照，临时禁止写入操作。
    /// </summary>
    Refreshing
}

/// <summary>
/// 持久化模式，控制内存缓存与底层存储的同步策略。
/// </summary>
public enum PersistMode {
    /// <summary>
    /// 每次编辑成功后立即写回底层存储。
    /// </summary>
    Immediate,

    /// <summary>
    /// 编辑成功后进入 PersistPending 状态，需显式调用 commit 或 discard。
    /// </summary>
    Manual,

    /// <summary>
    /// 所有编辑仅更新缓存，不会写回底层存储（只读模式）。
    /// </summary>
    Disabled
}

/// <summary>
/// 文本编辑操作的附加标志位，从 WorkflowState 派生或由特定条件触发。
/// </summary>
[Flags]
public enum TextEditFlag {
    /// <summary>
    /// 无标志。
    /// </summary>
    None = 0,

    /// <summary>
    /// 等待多匹配选区确认。
    /// </summary>
    SelectionPending = 1 << 0,

    /// <summary>
    /// 等待持久化提交。
    /// </summary>
    PersistPending = 1 << 1,

    /// <summary>
    /// 缓存与底层数据不同步。
    /// </summary>
    OutOfSync = 1 << 2,

    /// <summary>
    /// 响应格式不符合规范。
    /// </summary>
    SchemaViolation = 1 << 3,

    /// <summary>
    /// 持久化被禁用（只读模式）。
    /// </summary>
    PersistReadOnly = 1 << 4,

    /// <summary>
    /// 外部冲突检测。
    /// </summary>
    ExternalConflict = 1 << 5,

    /// <summary>
    /// 包含诊断提示信息。
    /// </summary>
    DiagnosticHint = 1 << 6
}

/// <summary>
/// 文本编辑操作的度量指标。
/// </summary>
public readonly record struct TextEditMetrics {
    /// <summary>
    /// 字符变化量（正数表示增加，负数表示减少）。
    /// </summary>
    public int Delta { get; init; }

    /// <summary>
    /// 编辑后的文本总长度。
    /// </summary>
    public int NewLength { get; init; }

    /// <summary>
    /// 检测到的选区数量（仅多匹配时有效）。
    /// </summary>
    public int? SelectionCount { get; init; }

    public TextEditMetrics(int delta, int newLength, int? selectionCount = null) {
        Delta = delta;
        NewLength = newLength;
        SelectionCount = selectionCount;
    }
}

/// <summary>
/// 多匹配场景下的候选选区信息。
/// </summary>
public sealed record TextEditCandidate {
    /// <summary>
    /// 选区的公开编号（从 1 开始）。
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// 匹配文本的上下文预览。
    /// </summary>
    public string Preview { get; init; }

    /// <summary>
    /// 选区开始标记（如 [[SEL#1]]）。
    /// </summary>
    public string MarkerStart { get; init; }

    /// <summary>
    /// 选区结束标记（如 [[/SEL#1]]）。
    /// </summary>
    public string MarkerEnd { get; init; }

    /// <summary>
    /// 该匹配是第几次出现（从 0 开始）。
    /// </summary>
    public int Occurrence { get; init; }

    /// <summary>
    /// 上下文在文本中的起始位置。
    /// </summary>
    public int ContextStart { get; init; }

    /// <summary>
    /// 上下文在文本中的结束位置。
    /// </summary>
    public int ContextEnd { get; init; }

    public TextEditCandidate(
        int id,
        string preview,
        string markerStart,
        string markerEnd,
        int occurrence,
        int contextStart,
        int contextEnd
    ) {
        Id = id;
        Preview = preview ?? string.Empty;
        MarkerStart = markerStart ?? string.Empty;
        MarkerEnd = markerEnd ?? string.Empty;
        Occurrence = occurrence;
        ContextStart = contextStart;
        ContextEnd = contextEnd;
    }
}

/// <summary>
/// 提供 TextEditFlag 枚举的辅助扩展方法。
/// </summary>
public static class TextEditFlagExtensions {
    /// <summary>
    /// 枚举 Flags 中的所有非 None 标志。
    /// </summary>
    public static IEnumerable<TextEditFlag> EnumerateFlags(this TextEditFlag flags) {
        if (flags == TextEditFlag.None) {
            yield break;
        }

        foreach (TextEditFlag value in Enum.GetValues(typeof(TextEditFlag))) {
            if (value != TextEditFlag.None && flags.HasFlag(value)) {
                yield return value;
            }
        }
    }

    /// <summary>
    /// 格式化 Flags 为 Markdown 内联代码列表（如 `PersistPending`, `DiagnosticHint`）。
    /// </summary>
    public static string FormatForMarkdown(this TextEditFlag flags) {
        if (flags == TextEditFlag.None) { return "-"; }

        return string.Join(", ", flags.EnumerateFlags().Select(f => $"`{f}`"));
    }
}
