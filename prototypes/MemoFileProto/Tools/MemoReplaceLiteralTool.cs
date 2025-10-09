using System.Text.Json;
using System.Text.Json.Serialization;
using MemoFileProto.Models;

namespace MemoFileProto.Tools;

/// <summary>
/// MemoReplaceLiteral 工具
/// 支持唯一匹配与锚点定位的字面文本替换工具
/// </summary>
public class MemoReplaceLiteral : ITool {
    private readonly Func<string> _getNotes;
    private readonly Action<string> _setNotes;
    private readonly TextReplacementEngine _engine;

    public MemoReplaceLiteral(Func<string> getNotes, Action<string> setNotes) {
        _getNotes = getNotes;
        _setNotes = setNotes;
        _engine = new TextReplacementEngine(_getNotes, _setNotes);
    }

    public string Name => "memory_notebook_replace";

    public string Description => @"在[Memory Notebook]中查找并替换字面文本。支持唯一性检查和锚点定位两种模式。

参数：
- old_text: 要替换的旧文本（必须精确匹配）。如果为空，则将 new_text 追加到[Memory Notebook]末尾。
- new_text: 替换后的新文本（可以为空字符串表示删除）
- search_after: (可选) 定位锚点，用于在多个匹配中精确定位目标
  * 未提供/null: 要求 old_text 在[Memory Notebook]中唯一存在，否则报错
  * 空字符串 '''': 从文档开头取第一个 old_text 匹配项
  * 非空字符串: 先找到该锚点，再在其后取第一个 old_text 匹配项

使用场景：
- 简单场景：old_text 在[Memory Notebook]中唯一 → 直接调用，无需 search_after
- 消歧场景：old_text 有多个匹配 → 添加 search_after 锚点来定位
- 开头匹配：需要替换文档开头的重复文本 → 使用 search_after=''''

示例 1 - 唯一替换：
  old_text: '项目状态：进行中'
  new_text: '项目状态：已完成'

示例 2 - 使用锚点消歧：
  old_text: 'int result = 0;'
  new_text: 'int result = initialValue;'
  search_after: 'void ProcessData() {'

示例 3 - 从开头匹配：
  old_text: '__m256i vec = _mm256_loadu_si256(&data[0]);'
  new_text: '__m256i vec = _mm256_load_si256(&data[0]);'
  search_after: ''''

提示：
- 如果遇到多匹配错误，工具会展示所有匹配位置的上下文，帮助你选择合适的 search_after
- 如果需要替换一个大区域，考虑使用 memory_notebook_replace_span 工具";

    public Tool GetToolDefinition() {
        return new Tool {
            Type = "function",
            Function = new FunctionDefinition {
                Name = Name,
                Description = Description,
                Parameters = new {
                    type = "object",
                    properties = new {
                        old_text = new {
                            type = "string",
                            description = "要替换的旧文本（精确匹配）。如果为空字符串，则追加 new_text 到[Memory Notebook]末尾。"
                        },
                        new_text = new {
                            type = "string",
                            description = "替换后的新文本。可以为空字符串表示删除 old_text。"
                        },
                        search_after = new {
                            type = "string",
                            description = "(可选) 定位锚点。未提供=要求唯一；空字符串=从开头取第一个；非空=从锚点后取第一个。"
                        }
                    },
                    required = new[] { "old_text", "new_text" }
                }
            }
        };
    }

    public async Task<string> ExecuteAsync(string arguments) {
        await Task.CompletedTask; // 异步占位

        try {
            var args = JsonSerializer.Deserialize<EditNotesArgs>(arguments);
            if (args == null) { return "Error: Invalid arguments"; }
            var rawOldText = args.OldText ?? string.Empty;
            var normalizedOldText = TextToolUtilities.NormalizeLineEndings(rawOldText);
            var request = new ReplacementRequest(
                rawOldText,
                args.NewText ?? string.Empty,
                args.SearchAfter,
                string.IsNullOrEmpty(normalizedOldText),
                "memory_notebook_replace"
            );

            var locator = string.IsNullOrEmpty(normalizedOldText)
                ? null
                : new LiteralRegionLocator(normalizedOldText);

            var outcome = _engine.Execute(request, locator);
            return outcome.Message;
        }
        catch (Exception ex) {
            return $"Error: {ex.Message}";
        }
    }

    private class EditNotesArgs {
        [JsonPropertyName("old_text")]
        public string OldText { get; set; } = string.Empty;

        [JsonPropertyName("new_text")]
        public string NewText { get; set; } = string.Empty;

        [JsonPropertyName("search_after")]
        public string? SearchAfter { get; set; }
    }
}
