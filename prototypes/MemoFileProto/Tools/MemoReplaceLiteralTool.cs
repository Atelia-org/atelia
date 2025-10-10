using System.Text.Json;
using System.Text.Json.Serialization;
using MemoFileProto.Models;

namespace MemoFileProto.Tools;

/// <summary>
/// MemoReplaceLiteral 工具
/// 支持唯一匹配与锚点定位的字面文本替换工具
/// </summary>
public class MemoReplaceLiteral : ITool {
    private readonly Func<string> _getMemoryNotebook;
    private readonly Action<string> _setMemoryNotebook;
    private readonly TextReplacementEngine _engine;

    public MemoReplaceLiteral(Func<string> getMemoryNotebook, Action<string> setMemoryNotebook) {
        _getMemoryNotebook = getMemoryNotebook;
        _setMemoryNotebook = setMemoryNotebook;
        _engine = new TextReplacementEngine(_getMemoryNotebook, _setMemoryNotebook);
    }

    public string Name => "memory_notebook_replace";

    public string Description => @"在[Memory Notebook]中查找并替换字面文本。支持唯一性检查与锚点定位，适合处理短语或整行文本的精确替换。

参数：
- old_text: 要替换的旧文本（必须精确匹配）。
    * 空字符串 "" → 不做查找，直接把 new_text 追加到[Memory Notebook]末尾。
- new_text: 替换后的新文本（可为空字符串，表示删除 old_text）。
- search_after: (可选) 解决多重匹配时的定位问题。
    * 省略此字段 → old_text 必须在文档中唯一出现；否则返回冲突提示。
    * 空字符串 "" → 从文档开头寻找第一个 old_text 匹配项。
    * 非空字符串 → 先定位锚点，再在其后寻找第一个 old_text 匹配项。

阅读顺序建议：
1. 判断 old_text 是否唯一，必要时准备锚点；
2. 确认 new_text 是否需要保留原有换行或缩进；
3. 若 old_text 为空字符串，直接把新内容当作追加段落。

示例 1 — 唯一替换：
    old_text: ""项目状态：进行中""
    new_text: ""项目状态：已完成""

示例 2 — 使用锚点消歧：
    old_text: ""int result = 0;""
    new_text: ""int result = initialValue;""
    search_after: ""void ProcessData() {""

示例 3 — 从开头取第一项：
    old_text: ""__m256i vec = _mm256_loadu_si256(&data[0]);""
    new_text: ""__m256i vec = _mm256_load_si256(&data[0]);""
    search_after: ""

提示：
- 如果出现多匹配错误，工具会展示匹配上下文，方便挑选合适的 search_after。
- 当需要替换较大区域或跨多个段落时，考虑使用 memory_notebook_replace_span 工具。";

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
                            description = "要替换的旧文本（精确匹配）。空字符串 \"\" 表示直接将 new_text 追加到文档末尾。"
                        },
                        new_text = new {
                            type = "string",
                            description = "替换后的新文本。可以为空字符串表示删除 old_text。"
                        },
                        search_after = new {
                            type = "string",
                            description = "(可选) 定位锚点。省略=要求 old_text 唯一；空字符串 \"\"=从文档开头取第一个匹配；非空=从锚点后取第一个。"
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
            var args = JsonSerializer.Deserialize<EditTextArgs>(arguments);
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

    private class EditTextArgs {
        [JsonPropertyName("old_text")]
        public string OldText { get; set; } = string.Empty;

        [JsonPropertyName("new_text")]
        public string NewText { get; set; } = string.Empty;

        [JsonPropertyName("search_after")]
        public string? SearchAfter { get; set; }
    }
}
