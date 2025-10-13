using System.Text.Json.Serialization;

namespace MemoFileProto.Models.OpenAI;

/// <summary>
/// OpenAI 特定的请求格式
/// </summary>
public class OpenAIRequest {
    [JsonPropertyName("model")]
    public string Model { get; set; } = "vscode-lm-proxy";

    [JsonPropertyName("messages")]
    public List<OpenAIMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAITool>? Tools { get; set; }
}

public class OpenAITool {
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAIFunctionDefinition Function { get; set; } = new();
}

public class OpenAIFunctionDefinition {
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public object Parameters { get; set; } = new { };
}
