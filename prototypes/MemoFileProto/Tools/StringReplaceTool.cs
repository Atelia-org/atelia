using System.Text.Json;
using System.Text.Json.Serialization;
using MemoFileProto.Models;

namespace MemoFileProto.Tools;

/// <summary>
/// string_replace 工具（示例实现）
/// 用于替换字符串中的内容
/// </summary>
public class StringReplaceTool : ITool {
    public string Name => "string_replace";

    public string Description => "替换字符串中的文本。接受三个参数：text（原始文本），old_value（要替换的文本），new_value（替换后的文本）。";

    public Tool GetToolDefinition() {
        return new Tool {
            Type = "function",
            Function = new FunctionDefinition {
                Name = Name,
                Description = Description,
                Parameters = new {
                    type = "object",
                    properties = new {
                        text = new {
                            type = "string",
                            description = "原始文本"
                        },
                        old_value = new {
                            type = "string",
                            description = "要替换的文本"
                        },
                        new_value = new {
                            type = "string",
                            description = "替换后的文本"
                        }
                    },
                    required = new[] { "text", "old_value", "new_value" }
                }
            }
        };
    }

    public async Task<string> ExecuteAsync(string arguments) {
        await Task.CompletedTask; // 异步占位

        try {
            var args = JsonSerializer.Deserialize<StringReplaceArgs>(arguments);
            if (args == null) { return "Error: Invalid arguments"; }

            if (string.IsNullOrWhiteSpace(args.OldValue)) { return "Error: 参数 old_value 不能为空"; }

            var result = args.Text.Replace(args.OldValue, args.NewValue);
            return $"替换成功。结果：{result}";
        }
        catch (Exception ex) {
            return $"Error: {ex.Message}";
        }
    }

    private class StringReplaceArgs {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("old_value")]
        public string OldValue { get; set; } = string.Empty;

        [JsonPropertyName("new_value")]
        public string NewValue { get; set; } = string.Empty;
    }
}
