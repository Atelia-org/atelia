# Markdown 导入适配器

## 概述

本文档描述如何将 Markdown 文档导入为 Info DOM 的 `InfoSection` 树结构。

**重要说明**：Markdown 只是 Info DOM 支持的众多输入格式之一。Info DOM 本身是格式无关的层级化容器，不依赖或锚定于 Markdown。

## 设计目标

- **语义化**：将 Markdown Heading 理解为章节分隔符，构建层级结构
- **简化**：不追求 Markdown 的精确表示，忽略格式细节
- **实用性**：满足 LLM Agent 导航和编辑的核心需求

## 转换策略

### 核心思路

Markdown 是扁平的块序列，Info DOM 是层级树结构。转换的关键是：

1. **Heading → InfoSection**：每个 Heading 创建一个 InfoSection 节点
2. **Heading 层级 → 树结构**：根据 `#` 的数量确定父子关系
3. **非 Heading 内容 → Section.Content**：将 Heading 之间的所有块合并为字符串

### 转换流程

```
Markdown 文本
    ↓ (Markdig 解析)
Markdown AST (扁平的块序列)
    ↓ (构建章节层级)
InfoSection 树 (层级结构)
```

## 实现方案

### 1. 解析 Markdown AST

使用 Markdig 库解析 Markdown：

```csharp
using Markdig;
using Markdig.Syntax;

var markdown = File.ReadAllText("document.md");
var document = Markdown.Parse(markdown, new MarkdownPipelineBuilder()
    .UseAdvancedExtensions()  // 支持 GFM 扩展
    .Build());
```

### 2. 构建 InfoSection 树

基本算法：

```csharp
public InfoSection ImportFromMarkdown(MarkdownDocument doc) {
    var root = new InfoSection {
        Id = Guid.NewGuid(),
        Title = null,  // 根节点无标题
        Content = null,
        Children = []
    };

    var sectionStack = new Stack<(InfoSection section, int level)>();
    sectionStack.Push((root, 0));  // 根节点视为 0 级

    var contentBuffer = new StringBuilder();

    foreach (var block in doc) {
        if (block is HeadingBlock heading) {
            // 保存当前累积的内容到上一个 Section
            if (contentBuffer.Length > 0) {
                sectionStack.Peek().section.Content = contentBuffer.ToString().Trim();
                contentBuffer.Clear();
            }

            // 根据层级调整栈
            while (sectionStack.Count > 1 &&
                   sectionStack.Peek().level >= heading.Level) {
                sectionStack.Pop();
            }

            // 创建新 Section
            var newSection = new InfoSection {
                Id = Guid.NewGuid(),
                Title = ExtractText(heading),
                Content = null,
                Children = []
            };

            sectionStack.Peek().section.Children.Add(newSection);
            sectionStack.Push((newSection, heading.Level));
        }
        else {
            // 累积非 Heading 块的内容
            contentBuffer.AppendLine(RenderBlock(block));
        }
    }

    // 保存最后的内容
    if (contentBuffer.Length > 0) {
        sectionStack.Peek().section.Content = contentBuffer.ToString().Trim();
    }

    return root;
}
```

### 3. 提取 Heading 文本

```csharp
private string ExtractText(HeadingBlock heading) {
    if (heading.Inline == null) return "";

    var text = heading.Inline.ToString();

    // 移除换行符（InfoSection.Title 约束）
    return text.Replace("\n", " ").Replace("\r", " ");
}
```

### 4. 渲染块内容

将非 Heading 的 Markdown 块渲染回文本：

```csharp
private string RenderBlock(Block block) {
    // 简单方案：使用 Markdig 的 NormalizeRenderer
    using var writer = new StringWriter();
    var renderer = new Markdig.Renderers.Normalize.NormalizeRenderer(writer);
    renderer.Render(block);
    return writer.ToString();
}
```

## 转换规则

### Heading 层级映射

| Markdown | InfoSection 层级 |
|----------|----------------|
| `#`      | Level 1        |
| `##`     | Level 2        |
| `###`    | Level 3        |
| ...      | ...            |
| `######` | Level 6        |

**统一处理**：ATX 和 Setext 两种 Heading 风格都按 `#` 数量（或等效层级）转换。

### 内容块处理

所有非 Heading 的块（段落、列表、代码块、表格等）都**合并为字符串**存储在 `InfoSection.Content` 中：

```markdown
## Example Section

This is a paragraph.

- List item 1
- List item 2

```code
example
```
```

转换为：

```csharp
new InfoSection {
    Title = "Example Section",
    Content = @"This is a paragraph.

- List item 1
- List item 2

```code
example
```"
}
```

### 边界情况

#### 1. Document 开头的内容（无 Heading）

第一个 Heading 之前的内容存储在根节点的 `Content` 中：

```markdown
This is the preamble.

# First Heading
```

```csharp
root.Content = "This is the preamble.";
root.Children[0].Title = "First Heading";
```

#### 2. 跳级 Heading

```markdown
# H1
### H3  ← 跳过了 H2
```

**策略 A**（推荐）：直接按层级关系处理，不创建虚拟节点

```
InfoSection (root)
└─ InfoSection "H1" (level 1)
    └─ InfoSection "H3" (level 3)
```

**策略 B**：创建空的中间节点

```
InfoSection (root)
└─ InfoSection "H1" (level 1)
    └─ InfoSection null (level 2, 虚拟)
        └─ InfoSection "H3" (level 3)
```

MVP 阶段采用策略 A。

#### 3. 降级 Heading

```markdown
### H3
# H1  ← 降回 H1
```

自动关闭所有层级高于 H1 的 Section，H1 成为新的顶级节点。

#### 4. 缩进的 Heading

根据 GFM 规范：

- **2-3 空格缩进**：仍被解析为 Heading
  ```markdown
    ## Indented
  ```
  → 仍创建 InfoSection

- **4+ 空格缩进**：被解析为 Indented Code Block
  ```markdown
      ## Not a heading
  ```
  → 成为 Content 的一部分（代码块）

Markdig 会自动处理，导入器无需特殊逻辑。

#### 5. 容器内的 Heading

```markdown
- List item
  ## Heading in list?
```

根据 GFM，`## Heading in list?` 实际上会结束列表，成为独立的 Heading。Markdig 的解析结果决定最终行为。

**建议**：测试 Markdig 的实际行为，按其 AST 结构转换。

## 不转换的内容

以下 Markdown 特性**不会**在 InfoSection 结构中体现：

- ❌ **Inline 格式**：粗体、斜体、链接等（Content 中保留原始文本）
- ❌ **列表嵌套结构**：列表项之间的父子关系（扁平化为 Content 字符串）
- ❌ **表格结构**：单元格关系（保留原始 Markdown 表格文本）
- ❌ **代码块语言标记**：保留文本但不单独提取
- ❌ **元数据**：YAML Front Matter、HTML 注释等（可选择性处理）

## 示例转换

### 输入 Markdown

```markdown
# Introduction

This is the introduction paragraph.

## Background

Some background information.

### History

Historical context.

## Goals

- Goal 1
- Goal 2

# API Reference

API documentation.
```

### 输出 InfoSection 树

```csharp
InfoSection (root, id=...)
├─ Children[0]: InfoSection (id=..., Title="Introduction")
│   ├─ Content: "This is the introduction paragraph."
│   └─ Children:
│       ├─ [0]: InfoSection (id=..., Title="Background")
│       │   ├─ Content: "Some background information."
│       │   └─ Children:
│       │       └─ [0]: InfoSection (id=..., Title="History")
│       │           └─ Content: "Historical context."
│       └─ [1]: InfoSection (id=..., Title="Goals")
│           └─ Content: "- Goal 1\n- Goal 2"
└─ Children[1]: InfoSection (id=..., Title="API Reference")
    └─ Content: "API documentation."
```

## 导出回 Markdown

反向转换（InfoSection 树 → Markdown 文本）：

```csharp
public string ExportToMarkdown(InfoSection section, int level = 0) {
    var sb = new StringBuilder();

    // 渲染 Heading（根节点除外）
    if (level > 0 && !string.IsNullOrEmpty(section.Title)) {
        sb.Append(new string('#', level));
        sb.Append(' ');
        sb.AppendLine(section.Title);
        sb.AppendLine();
    }

    // 渲染 Content
    if (!string.IsNullOrEmpty(section.Content)) {
        sb.AppendLine(section.Content);
        sb.AppendLine();
    }

    // 递归渲染子节点
    foreach (var child in section.Children) {
        sb.Append(ExportToMarkdown(child, level + 1));
    }

    return sb.ToString();
}
```

## 实现建议

### MVP 阶段

1. **仅支持 ATX Heading**：简化处理，统一使用 `#` 风格
2. **内容扁平化**：所有非 Heading 块合并为字符串，不解析内部结构
3. **忽略 Inline 格式**：Title 提取纯文本，Content 保留原始 Markdown

### 未来扩展

- **保留源信息**：记录原始 Markdown 行号，用于调试和增量更新
- **元数据提取**：解析 YAML Front Matter，存储为 JSON 在 Content 中
- **智能合并**：识别"过短的 Section"，自动合并到父节点

## 测试用例

建议创建以下测试文件：

1. `basic-hierarchy.md`：简单的 1-3 级 Heading
2. `skip-level.md`：跳级 Heading（# → ###）
3. `downgrade.md`：降级 Heading（### → #）
4. `preamble.md`：Document 开头有内容但无 Heading
5. `complex-content.md`：包含列表、代码块、表格的内容
6. `edge-cases.md`：空 Heading、连续 Heading、深层嵌套等

## 相关工具

- **Markdig**：.NET Markdown 解析器（推荐）
- **CommonMark.NET**：替代方案
- **Markdown-it**：JavaScript 生态系统（如需跨语言）

## 总结

Markdown 导入器是 Info DOM 的一个可选适配器，核心原则是：

- **忠实于语义**：Heading 表示章节层级
- **简化细节**：不追求 Markdown 的完美往返
- **服务目标**：为 LLM Agent 提供可编辑的层级结构

Info DOM 的设计不依赖 Markdown，未来可以添加 JSON、XML、AsciiDoc 等其他导入器。
