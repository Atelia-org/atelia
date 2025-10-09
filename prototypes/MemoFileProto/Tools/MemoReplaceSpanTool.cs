using System.Text.Json;
using System.Text.Json.Serialization;
using MemoFileProto.Models;

namespace MemoFileProto.Tools;

/// <summary>
/// MemoReplaceSpan 工具
/// 精确定位的区域替换工具，适用于需要上下文锚定的复杂编辑场景
/// </summary>
public class MemoReplaceSpan : ITool {
    private readonly Func<string> _getNotes;
    private readonly Action<string> _setNotes;
    private readonly TextReplacementEngine _engine;

    public MemoReplaceSpan(Func<string> getNotes, Action<string> setNotes) {
        _getNotes = getNotes;
        _setNotes = setNotes;
        _engine = new TextReplacementEngine(_getNotes, _setNotes);
    }

    public string Name => "memory_notebook_replace_span";

    public string Description => @"通过上下文精确定位并替换[Memory Notebook]中的区域。

使用场景：
- 要替换的内容在[Memory Notebook]中出现多次，需要通过上下文区分
- 需要修改一个大段落中的部分内容，避免完整复述整个段落
- 担心空格/Tab/标点符号导致精确匹配失败

参数：
- old_span_start: 要替换区域的起始标记（必需）
- old_span_end: 要替换区域的结束标记（必需）
- new_text: 替换后的新文本（需要包含替换后希望出现的首尾内容）
- search_after: (可选) 从此文本之后开始搜索 old_span_start，用于在多个匹配中精确定位

行为：
- 默认替换 old_span_start 和 old_span_end 之间的内容（**包含**这两个标记本身）
- 如果提供了 search_after，会先定位该文本，再在其后查找 old_span_start
- search_after=''（空字符串）表示从文档开头查找 old_span_start
- 如果在定位范围内找到多个 old_span_start，工具会返回这些位置的上下文，提示使用 search_after 精确定位

示例 1 - 修改特定章节：
    old_span_start: '### 技术栈'
    old_span_end: '### 团队协作'
    new_text: '### 技术栈\n主要使用 Rust 和 TypeScript\n### 团队协作'

示例 2 - 使用上下文精确定位：
    search_after: '## 2024年总结'
    old_span_start: '### 技术栈'
    old_span_end: '### 团队'
    new_text: '### 技术栈\n已全面切换到 Rust\n### 团队'

示例 3 - 替换包含标记的整个块：
    old_span_start: '<!-- BEGIN OLD SECTION -->'
    old_span_end: '<!-- END OLD SECTION -->'
    new_text: '<!-- NEW SECTION -->\n新内容\n<!-- END NEW -->'";

    public Tool GetToolDefinition() {
        return new Tool {
            Type = "function",
            Function = new FunctionDefinition {
                Name = Name,
                Description = Description,
                Parameters = new {
                    type = "object",
                    properties = new {
                        old_span_start = new {
                            type = "string",
                            description = "要替换区域的起始标记"
                        },
                        old_span_end = new {
                            type = "string",
                            description = "要替换区域的结束标记"
                        },
                        new_text = new {
                            type = "string",
                            description = "替换后的新文本（需要连同新的首尾内容一起提供）"
                        },
                        search_after = new {
                            type = "string",
                            description = "(可选) 从此文本之后开始搜索 old_span_start，用于在多个匹配中精确定位"
                        }
                    },
                    required = new[] { "old_span_start", "old_span_end", "new_text" }
                }
            }
        };
    }

    public async Task<string> ExecuteAsync(string arguments) {
        await Task.CompletedTask;

        try {
            var args = JsonSerializer.Deserialize<UpdateRegionArgs>(arguments);
            if (args == null) { return "Error: Invalid arguments"; }

            var rawStart = args.OldSpanStart ?? string.Empty;
            var rawEnd = args.OldSpanEnd ?? string.Empty;

            if (string.IsNullOrEmpty(rawStart) || string.IsNullOrEmpty(rawEnd)) { return "Error: old_span_start 和 old_span_end 不能为空"; }

            var normalizedStart = TextToolUtilities.NormalizeLineEndings(rawStart);
            var normalizedEnd = TextToolUtilities.NormalizeLineEndings(rawEnd);

            var request = new ReplacementRequest(
                normalizedStart,
                args.NewText ?? string.Empty,
                args.SearchAfter,
                false,
                "memory_notebook_replace_span"
            );

            var locator = new SpanRegionLocator(normalizedStart, normalizedEnd);
            var outcome = _engine.Execute(request, locator);
            return outcome.Message;
        }
        catch (Exception ex) {
            return $"Error: {ex.Message}";
        }
    }

    private class UpdateRegionArgs {
        [JsonPropertyName("old_span_start")]
        public string? OldSpanStart { get; set; }

        [JsonPropertyName("old_span_end")]
        public string? OldSpanEnd { get; set; }

        [JsonPropertyName("new_text")]
        public string? NewText { get; set; }

        [JsonPropertyName("search_after")]
        public string? SearchAfter { get; set; }
    }
}
