using System;
using System.Collections.Generic;
using Atelia.Agent.Core;

namespace Atelia.Agent.Text;

/// <summary>
/// 封装 TextEditor2Widget 的状态转换逻辑、工具可见性管理与 Flags 推断。
/// </summary>
/// <remarks>
/// 该控制器作为状态机的核心，负责：
/// 维护当前 WorkflowState 并根据操作事件执行状态转换
/// 根据 WorkflowState 和 PersistMode 推断 Flags 组合
/// 管理工具可见性矩阵，确保 LLM 只看到合法的操作选项
/// 提供状态查询接口，便于诊断与测试
/// </remarks>
public sealed class TextEditStateController {
    private TextEditWorkflowState _currentState;
    private readonly PersistMode _persistMode;
    private readonly ITool _replaceTool;
    private readonly ITool _replaceSelectionTool;

    // 保存进入 SelectionPending 前的状态，用于清除选区后恢复。
    // 这确保在 Manual 模式下，多匹配操作不会丢失 PersistPending 状态。
    private TextEditWorkflowState _stateBeforeSelection;

    public TextEditStateController(
        PersistMode persistMode,
        ITool replaceTool,
        ITool replaceSelectionTool
    ) {
        _persistMode = persistMode;
        _replaceTool = replaceTool ?? throw new ArgumentNullException(nameof(replaceTool));
        _replaceSelectionTool = replaceSelectionTool ?? throw new ArgumentNullException(nameof(replaceSelectionTool));
        _currentState = TextEditWorkflowState.Idle;
        _stateBeforeSelection = TextEditWorkflowState.Idle;

        // 初始化工具可见性
        UpdateToolVisibility();
    }

    /// <summary>
    /// 获取当前的工作流状态。
    /// </summary>
    public TextEditWorkflowState CurrentState => _currentState;

    /// <summary>
    /// 获取当前的持久化模式。
    /// </summary>
    public PersistMode PersistMode => _persistMode;

    /// <summary>
    /// 单匹配替换成功后的状态转换。
    /// </summary>
    public void OnSingleMatchSuccess() {
        _currentState = _persistMode switch {
            PersistMode.Immediate => TextEditWorkflowState.Idle,
            PersistMode.Manual => TextEditWorkflowState.PersistPending,
            PersistMode.Disabled => TextEditWorkflowState.Idle,
            _ => throw new InvalidOperationException($"Unknown PersistMode: {_persistMode}")
        };
        UpdateToolVisibility();
    }

    /// <summary>
    /// 检测到多匹配时的状态转换。
    /// </summary>
    public void OnMultiMatch() {
        // 保存当前状态，以便清除选区时恢复（例如 PersistPending → SelectionPending → PersistPending）
        _stateBeforeSelection = _currentState;
        _currentState = TextEditWorkflowState.SelectionPending;
        UpdateToolVisibility();
    }

    /// <summary>
    /// 选区确认成功后的状态转换。
    /// </summary>
    public void OnSelectionConfirmed() {
        _currentState = _persistMode switch {
            PersistMode.Immediate => TextEditWorkflowState.Idle,
            PersistMode.Manual => TextEditWorkflowState.PersistPending,
            PersistMode.Disabled => TextEditWorkflowState.Idle,
            _ => throw new InvalidOperationException($"Unknown PersistMode: {_persistMode}")
        };
        UpdateToolVisibility();
    }

    /// <summary>
    /// 检测到外部冲突时的状态转换。
    /// </summary>
    public void OnExternalConflict() {
        _currentState = TextEditWorkflowState.OutOfSync;
        UpdateToolVisibility();
    }

    /// <summary>
    /// 持久化失败时的状态转换。
    /// </summary>
    public void OnPersistFailure() {
        _currentState = TextEditWorkflowState.OutOfSync;
        UpdateToolVisibility();
    }

    /// <summary>
    /// 清除选区状态（用户调用 discard 或发起新的 replace）。
    /// </summary>
    public void OnClearSelection() {
        if (_currentState == TextEditWorkflowState.SelectionPending) {
            // 恢复进入 SelectionPending 前的状态，确保 Manual 模式下不丢失 PersistPending
            _currentState = _stateBeforeSelection;
            UpdateToolVisibility();
        }
    }

    /// <summary>
    /// 根据当前状态与持久化模式推断标志位。
    /// </summary>
    public TextEditFlag DeriveFlags() {
        var flags = _currentState switch {
            TextEditWorkflowState.Idle => TextEditFlag.None,
            TextEditWorkflowState.SelectionPending => TextEditFlag.SelectionPending,
            TextEditWorkflowState.PersistPending => TextEditFlag.PersistPending,
            TextEditWorkflowState.OutOfSync => TextEditFlag.OutOfSync,
            TextEditWorkflowState.Refreshing => TextEditFlag.PersistReadOnly | TextEditFlag.DiagnosticHint,
            _ => TextEditFlag.None
        };

        // Disabled 模式下始终附加 PersistReadOnly
        if (_persistMode == PersistMode.Disabled && _currentState != TextEditWorkflowState.Refreshing) {
            flags |= TextEditFlag.PersistReadOnly;
        }

        return flags;
    }

    /// <summary>
    /// 根据操作状态补充诊断标志位。
    /// </summary>
    public static TextEditFlag DeriveStatusFlags(TextEditStatus status) {
        return status switch {
            TextEditStatus.ExternalConflict => TextEditFlag.ExternalConflict | TextEditFlag.DiagnosticHint,
            TextEditStatus.PersistFailure => TextEditFlag.PersistPending | TextEditFlag.DiagnosticHint,
            TextEditStatus.Exception => TextEditFlag.DiagnosticHint,
            _ => TextEditFlag.None
        };
    }

    /// <summary>
    /// 检查当前状态下指定工具是否应该可见。
    /// </summary>
    public bool ShouldToolBeVisible(string toolName) {
        if (toolName == _replaceTool.Name) {
            // replace 工具在 Refreshing 状态下隐藏
            return _currentState != TextEditWorkflowState.Refreshing;
        }

        if (toolName == _replaceSelectionTool.Name) {
            // replace_selection 仅在 SelectionPending 状态可见
            return _currentState == TextEditWorkflowState.SelectionPending;
        }

        // 其他工具暂时默认可见（后续扩展 commit/discard/diff/refresh 时补充）
        return true;
    }

    /// <summary>
    /// 根据当前状态更新工具可见性。
    /// </summary>
    private void UpdateToolVisibility() {
        _replaceTool.Visible = ShouldToolBeVisible(_replaceTool.Name);
        _replaceSelectionTool.Visible = ShouldToolBeVisible(_replaceSelectionTool.Name);
    }

    /// <summary>
    /// 获取当前状态下推荐的 Guidance 提示文本。
    /// </summary>
    public string? GetRecommendedGuidance(TextEditStatus status, string replaceToolName, string replaceSelectionToolName) {
        // 根据状态生成通用 Guidance
        var stateGuidance = _currentState switch {
            TextEditWorkflowState.SelectionPending => $"请调用 {replaceSelectionToolName} 工具并指定 selection_id 完成替换。",
            TextEditWorkflowState.PersistPending => "调用 _commit 写回底层存储，或使用 _discard 放弃修改。",
            TextEditWorkflowState.OutOfSync => "建议先调用 _diff 查看缓存与底层的差异，再考虑 _refresh 或人工处理。",
            _ => null
        };

        // 根据操作状态补充诊断提示
        var statusGuidance = status switch {
            TextEditStatus.ExternalConflict => "优先使用 _diff 评估差异，避免直接 _commit。",
            TextEditStatus.PersistFailure => "检查权限或磁盘空间后重试，或使用 _persist_as 另存为新路径。",
            _ => null
        };

        // 只读模式提示
        string? readOnlyGuidance = null;
        if (_persistMode == PersistMode.Disabled && status == TextEditStatus.Success) {
            readOnlyGuidance = "注意：当前为只读模式，结果仅存于缓存，不会写回底层。";
        }

        // 组合所有 Guidance
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(stateGuidance)) { parts.Add(stateGuidance); }
        if (!string.IsNullOrEmpty(statusGuidance)) { parts.Add(statusGuidance); }
        if (!string.IsNullOrEmpty(readOnlyGuidance)) { parts.Add(readOnlyGuidance); }

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }
}
