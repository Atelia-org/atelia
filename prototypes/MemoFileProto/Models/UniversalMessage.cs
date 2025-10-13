namespace MemoFileProto.Models;

/// <summary>
/// Provider 无关的通用消息模型
/// </summary>
public class UniversalMessage {
    /// <summary>
    /// 消息角色：user, assistant, system
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// 文本内容（可选，当有工具调用时可能为空）
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// 工具调用列表（assistant 角色可能包含）
    /// </summary>
    public List<UniversalToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// 工具调用结果列表（user 角色返回工具结果时使用，支持 Anthropic 的聚合格式）
    /// </summary>
    public List<UniversalToolResult>? ToolResults { get; init; }

    /// <summary>
    /// 消息创建时间（元数据，不发送给 LLM）
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// 用户原始输入（仅本地使用，避免重复包装）
    /// </summary>
    public string? RawInput { get; init; }
}

/// <summary>
/// 通用工具调用
/// </summary>
public class UniversalToolCall {
    /// <summary>
    /// 工具调用 ID（用于关联结果）
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 工具名称
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 工具参数（JSON 字符串）
    /// </summary>
    public required string Arguments { get; init; }
}

/// <summary>
/// 通用工具调用结果
/// </summary>
public class UniversalToolResult {
    /// <summary>
    /// 工具调用 ID（关联到对应的 ToolCall）
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>
    /// 工具名称（某些 Provider 需要）
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// 工具执行结果内容
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// 是否执行错误
    /// </summary>
    public bool IsError { get; init; }
}
