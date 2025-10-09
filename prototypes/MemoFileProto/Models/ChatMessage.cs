using System.Text.Json.Serialization;

namespace MemoFileProto.Models;

public class ChatMessage {
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>
    /// 消息创建时间（元数据，不发送给 LLM）
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// 用户原始输入（仅本地使用，避免重复包装）
    /// </summary>
    [JsonIgnore]
    public string? RawInput { get; init; }
}

public class ToolCall {
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public FunctionCall Function { get; init; } = new();
}

public class FunctionCall {
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = string.Empty;
}
