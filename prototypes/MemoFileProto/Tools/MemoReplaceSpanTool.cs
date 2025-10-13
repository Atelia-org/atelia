using System.Text.Json;
using System.Text.Json.Serialization;
using MemoFileProto.Models;

namespace MemoFileProto.Tools;

/// <summary>
/// MemoReplaceSpan 工具
/// 精确定位的区域替换工具，适用于需要上下文锚定的复杂编辑场景
/// </summary>
public class MemoReplaceSpan : ITool {
    private readonly Func<string> _getMemoryNotebook;
    private readonly Action<string> _setMemoryNotebook;
    private readonly TextReplacementEngine _engine;

    public MemoReplaceSpan(Func<string> getMemoryNotebook, Action<string> setMemoryNotebook) {
        _getMemoryNotebook = getMemoryNotebook;
        _setMemoryNotebook = setMemoryNotebook;
        _engine = new TextReplacementEngine(_getMemoryNotebook, _setMemoryNotebook);
    }

    public string Name => "memory_notebook_replace_span";

    public string Description => @"通过上下文精确定位并替换[Memory Notebook]中的区域，适合处理需要锚定前后文的复杂编辑任务。

参数：
- old_span_start: 要替换区域的起始标记（必填）。
- old_span_end: 要替换区域的结束标记（必填）。
- new_text: 替换后的新文本，应包含新的首尾标记。
- search_after: (可选) 用于从指定锚点之后开始搜索 old_span_start。
    * 省略此字段 → 直接按全文查找，要求 old_span_start/old_span_end 组合在文档中唯一。
    * 空字符串 "" → 从文档开头开始查找第一次出现的 old_span_start。
    * 非空字符串 → 先定位锚点，再在其后查找 old_span_start。

阅读顺序建议：
1. 先选定最能代表替换区域边界的 start/end 标记；
2. 检查这些标记是否唯一，若不唯一优先准备 search_after；
3. 预览 new_text 与原内容的缩进、空行保持是否符合预期。

示例 1 — 修改特定章节：
    old_span_start: ""### 技术栈""
    old_span_end: ""### 团队协作""
    new_text: ""### 技术栈
主要使用 Rust 和 TypeScript
### 团队协作""

示例 2 — 上下文锚定：
    search_after: ""## 2024年总结""
    old_span_start: ""### 技术栈""
    old_span_end: ""### 团队""
    new_text: ""### 技术栈
已全面切换到 Rust
### 团队""

示例 3 — 替换带标记的块：
    old_span_start: ""<!-- BEGIN OLD SECTION -->""
    old_span_end: ""<!-- END OLD SECTION -->""
    new_text: ""<!-- NEW SECTION -->
新内容
<!-- END NEW -->""

提示：
- 如果定位到多个 old_span_start，工具会返回候选上下文，按提示调整 search_after。
- 当 old_span_start/old_span_end 包含换行或缩进时，可先在 new_text 中草拟最终排版，避免出现多余缩进。";

    public UniversalTool GetToolDefinition() {
        return new UniversalTool {
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
                        description = "替换后的新文本，需要包含希望保留的首尾内容"
                    },
                    search_after = new {
                        type = "string",
                        description = "(可选) 锚点文本。省略=按全文查找；空字符串 \"\"=从文档开头；非空=从该锚点之后"
                    }
                },
                required = new[] { "old_span_start", "old_span_end", "new_text" }
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
