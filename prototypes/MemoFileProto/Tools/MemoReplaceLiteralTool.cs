using System.Text.Json;
using System.Text.Json.Serialization;
using MemoFileProto.Models;

namespace MemoFileProto.Tools;

/// <summary>
/// MemoReplaceLiteral 工具
/// 支持唯一匹配与锚点定位的字面文本替换工具
/// </summary>
public class MemoReplaceLiteral : ITool {
    private readonly Func<string> _getMemory;
    private readonly Action<string> _setMemory;

    public MemoReplaceLiteral(Func<string> getMemory, Action<string> setMemory) {
        _getMemory = getMemory;
        _setMemory = setMemory;
    }

    public string Name => "memo_replace_literal";

    public string Description => @"在记忆中查找并替换字面文本。支持唯一性检查和锚点定位两种模式。

参数：
- old_text: 要替换的旧文本（必须精确匹配）。如果为空，则将 new_text 追加到记忆末尾。
- new_text: 替换后的新文本（可以为空字符串表示删除）
- search_after: (可选) 定位锚点，用于在多个匹配中精确定位目标
  * 未提供/null: 要求 old_text 在记忆中唯一存在，否则报错
  * 空字符串 '''': 从文档开头取第一个 old_text 匹配项
  * 非空字符串: 先找到该锚点，再在其后取第一个 old_text 匹配项

使用场景：
- 简单场景：old_text 在记忆中唯一 → 直接调用，无需 search_after
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
- 如果需要替换一个大区域，考虑使用 memo_replace_span 工具";

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
                            description = "要替换的旧文本（精确匹配）。如果为空字符串，则追加 new_text 到记忆末尾。"
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
            var args = JsonSerializer.Deserialize<EditMemoryArgs>(arguments);
            if (args == null) { return "Error: Invalid arguments"; }

            var oldText = TextToolUtilities.NormalizeLineEndings(args.OldText ?? string.Empty);
            var newText = TextToolUtilities.NormalizeLineEndings(args.NewText ?? string.Empty);
            var rawSearchAfter = args.SearchAfter;
            var searchAfter = rawSearchAfter != null ? TextToolUtilities.NormalizeLineEndings(rawSearchAfter) : null;
            var currentMemory = TextToolUtilities.NormalizeLineEndings(_getMemory() ?? string.Empty);
            var anchor = TextToolUtilities.ResolveAnchor(currentMemory, searchAfter);

            // 特殊情况：old_text 为空，表示追加
            if (string.IsNullOrEmpty(oldText)) {
                var hasExistingMemory = !string.IsNullOrEmpty(currentMemory);
                var hasNewContent = !string.IsNullOrEmpty(newText);
                var needsSeparator = hasExistingMemory && hasNewContent
                    && !currentMemory.EndsWith("\n", StringComparison.Ordinal)
                    && !newText.StartsWith("\n", StringComparison.Ordinal);

                var appendedMemory = hasNewContent
                    ? currentMemory + (needsSeparator ? "\n" : string.Empty) + newText
                    : currentMemory;

                _setMemory(appendedMemory);
                return $"已追加内容到记忆末尾。当前记忆长度: {appendedMemory.Length} 字符";
            }

            if (!anchor.Success) { return anchor.ErrorMessage!; }

            // 根据 search_after 的值选择模式
            if (!anchor.IsRequested) {
                // 模式 1: 全局唯一性检查
                return ReplaceUnique(currentMemory, oldText, newText);
            }

            // 模式 2 & 3: 锚点定位模式
            return ReplaceAfterAnchor(currentMemory, oldText, newText, anchor.SearchStart, rawSearchAfter);
        }
        catch (Exception ex) {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// 模式 1: 全局唯一性检查和替换
    /// </summary>
    private string ReplaceUnique(string memory, string oldText, string newText) {
        // 查找所有匹配项
        var matches = FindAllMatches(memory, oldText);

        if (matches.Count == 0) {
            return $"Error: 找不到要替换的文本。请确保 old_text 精确匹配记忆中的内容。\n\n" +
                   $"当前记忆内容：\n{memory}";
        }

        if (matches.Count > 1) {
            // 展示所有匹配的上下文，帮助 LLM 选择 search_after
            var contextInfo = TextToolUtilities.FormatMatchesForError(matches, memory, oldText.Length, 80);

            return $"Error: 找到 {matches.Count} 个匹配项。\n\n" +
                   $"{contextInfo}\n\n" +
                   "请使用 'search_after' 参数来定位目标匹配项：\n" +
                   "- 从文档开头取第一个：设置 search_after=''\n" +
                   "- 在特定锚点后取第一个：设置 search_after='某个锚点文本'\n\n" +
                   "或者考虑使用 memo_replace_span 工具通过起止标记来定位区域。";
        }

        // 执行唯一匹配的替换
        var position = matches[0];
        var updatedMemory = memory.Remove(position, oldText.Length)
                                  .Insert(position, newText);
        _setMemory(updatedMemory);

        return $"记忆已更新。当前记忆长度: {updatedMemory.Length} 字符\n\n更新后的记忆：\n{updatedMemory}";
    }

    /// <summary>
    /// 模式 2 & 3: 锚点定位后替换第一个匹配
    /// </summary>
    private string ReplaceAfterAnchor(string memory, string oldText, string newText, int searchStart, string? rawSearchAfter) {
        // 从 searchStart 位置开始查找 old_text
        var targetIndex = memory.IndexOf(oldText, searchStart, StringComparison.Ordinal);
        if (targetIndex < 0) {
            var modeDesc = string.IsNullOrEmpty(rawSearchAfter)
                ? "从文档开头"
                : $"在锚点 '{rawSearchAfter}' 之后";
            return $"Error: {modeDesc}找不到要替换的文本: '{oldText}'\n\n" +
                   $"搜索起始位置: {searchStart}\n" +
                   $"该位置附近的内容：\n{TextToolUtilities.GetContext(memory, searchStart, 0, 100)}";
        }

        // 执行替换
        var updatedMemory = memory.Remove(targetIndex, oldText.Length)
                                  .Insert(targetIndex, newText);
        _setMemory(updatedMemory);

        return $"记忆已更新。当前记忆长度: {updatedMemory.Length} 字符\n\n更新后的记忆：\n{updatedMemory}";
    }

    /// <summary>
    /// 查找所有匹配位置
    /// </summary>
    private static List<int> FindAllMatches(string text, string pattern) {
        var matches = new List<int>();
        var index = 0;

        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0) {
            matches.Add(index);
            index += pattern.Length;
        }

        return matches;
    }

    private class EditMemoryArgs {
        [JsonPropertyName("old_text")]
        public string OldText { get; set; } = string.Empty;

        [JsonPropertyName("new_text")]
        public string NewText { get; set; } = string.Empty;

        [JsonPropertyName("search_after")]
        public string? SearchAfter { get; set; }
    }
}
