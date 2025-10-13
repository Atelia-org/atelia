using System.Text.Json.Serialization;

namespace MemoFileProto.Models.Anthropic;

/// <summary>
/// Anthropic 特定的消息格式
/// Anthropic 使用 content 数组，支持文本和工具结果混合
/// </summary>
public class AnthropicMessage {
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public List<AnthropicContentBlock> Content { get; init; } = new();
}

/// <summary>
/// Anthropic 内容块（可以是文本或工具结果）
/// </summary>
public class AnthropicContentBlock {
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    // 文本类型的字段
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    // 工具使用类型的字段
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Input { get; init; }

    // 工具结果类型的字段
    [JsonPropertyName("tool_use_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolResultContent { get; init; }

    [JsonPropertyName("is_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsError { get; init; }
}
