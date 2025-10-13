namespace MemoFileProto.Models;

/// <summary>
/// Provider 无关的通用流式响应增量
/// </summary>
public class UniversalResponseDelta {
    /// <summary>
    /// 增量文本内容
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// 工具调用增量（流式累积）
    /// </summary>
    public List<UniversalToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// 完成原因（end_turn, tool_use, max_tokens 等）
    /// </summary>
    public string? FinishReason { get; init; }

    /// <summary>
    /// 是否是流的结束
    /// </summary>
    public bool IsStreamEnd => !string.IsNullOrEmpty(FinishReason);
}
