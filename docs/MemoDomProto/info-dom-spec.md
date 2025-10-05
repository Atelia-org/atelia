# Info DOM: 通用层级化信息容器

## 设计目标

Info DOM 是一个**格式无关的层级化信息容器**，用于 LLM Agent 研究中的以下场景：

- **永续会话**：通过工具调用自主编辑上下文中的固定区域
- **精炼记忆**：主动遗忘过时信息和次要细节
- **结构化组织**：以树状层级管理信息，而非线性追加

### 核心原则

1. **格式无关**：不锚定任何特定文本格式（Markdown、JSON、XML 等）
2. **最小建模**：仅提供层级和标题，内容为自由格式字符串
3. **内存优先**：独立的内存对象，支持多种格式导入/导出

## InfoSection：唯一的节点类型

Info DOM 由单一类型的节点构成：`InfoSection`。所有信息以树状结构组织。

```csharp
public enum ContentFormat:ushort {
    Plain = 0,
    Markdown = 1,
    Json = 2
}

public class InfoSection {
    /// <summary>
    /// 节点的唯一标识符，用于编辑操作的定位
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// 子节点列表，形成树状结构
    /// </summary>
    public List<InfoSection> Children { get; }

    /// <summary>
    /// 计算值，在树中的绝对深度
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// 节点的标题（可选）
    /// 约束：不能包含换行符（导入时自动移除）
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 节点的内容（可选）
    /// 格式完全自由：可以是 Markdown、JSON、纯文本等任意字符串
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// 节点的内容的格式
    /// </summary>
    public ContentFormat ContentFormat { get; set; }

    /// <summary>
    /// 缓存的解析成内存对象后的Content内容
    /// </summary>
    public object? ParsedContent { get; set; }
}
```

### 设计说明

#### 1. **单一节点类型的优势**

- **避免类型歧义**：无需区分"容器节点"和"叶子节点"
- **灵活转换**：任何节点都可以添加子节点或内容
- **简化编辑 API**：所有操作针对统一类型

#### 2. **Title 的用途**

- **导航索引**：快速定位和理解节点语义
- **人类可读**：提供节点的简短描述
- **约束**：不能包含换行（`\n`、`\r`），导入时自动替换为空格或移除

#### 3. **Content 的自由度**

`Content` 是完全自由格式的字符串，可以包含：

- **Markdown 片段**：
  ```markdown
  这是一个段落。

  - 列表项 1
  - 列表项 2
  ```

- **JSON 数据**：
  ```json
  {
    "status": "active",
    "metadata": {"author": "AI", "tags": ["important"]}
  }
  ```

- **纯文本**：
  ```
  这是一段纯文本内容，
  可以包含多行。
  ```

- **其他格式**：代码块、YAML、表格等任意字符串

**关键点**：Info DOM 本身不解析或约束 `Content` 的格式，具体格式由导入/导出适配器和使用者决定。

#### 4. **根节点约定**

Info DOM 的根通常是一个特殊的 `InfoSection`：

```csharp
var root = new InfoSection {
    Id = Guid.NewGuid(),
    Title = null,  // 或空字符串，表示整个文档
    Content = null,
    Children = [/* 顶级节点 */]
};
```

## 导入Content时解析
导入Content时，根据ContentFormat自动选择相应的Importer解析受支持的结构化格式，自动创建InfoSection子树。

## 导出时内联
导出时，若目标导出格式与ContentFormat相同，则作为子对象内联而非普通字符串，目的是尽量减少转义的使用和格式一致性。

## 核心操作

Info DOM 支持的基本操作（具体 API 设计见后续文档）：

### 创建
```csharp
var section = new InfoSection {
    Id = Guid.NewGuid(),
    Title = "Introduction",
    Content = "This is the intro content.",
    ContentFormat = ContentFormat.Plain,
    Children = []
};
```

### 导航
```csharp
var child = section.Children.FirstOrDefault(s => s.Title == "Background");
```

### 编辑
```csharp
section.Title = "Updated Title";
section.Content += "\nAppended content.";
section.Children.Add(newSection);
```

## 导入与导出

Info DOM 本身不包含导入/导出逻辑，这些功能由独立的适配器提供：

- **Markdown 导入器**：将 Markdown 文档转换为 InfoSection 树（详见 [markdown-import.md](./markdown-import.md)）
- **JSON 导入器**：从结构化 JSON 构建 InfoSection 树
- **其他格式**：AsciiDoc、reStructuredText 等（未来扩展）

### 示例：多格式并存

```csharp
var root = new InfoSection {
    Title = "Project Documentation",
    Children = [
        new InfoSection {
            Title = "Overview",
            Content = "# Overview\n\nThis is **Markdown** content.",
            ContentFormat = ContentFormat.Markdown
        },
        new InfoSection {
            Title = "API Metadata",
            Content = "{\"version\": \"1.0\", \"endpoints\": [...]}",
            ContentFormat = ContentFormat.Json
        },
        new InfoSection {
            Title = "Plain Notes",
            Content = "Just some plain text notes here.",
            ContentFormat = ContentFormat.Plain
        }
    ]
};
```

## 与 Markdown 的关系

**Info DOM 不是 Markdown DOM**：

- ❌ 不追求 Markdown 的精确表示（不区分 ATX/Setext 标题、列表样式等）
- ❌ 不处理 Inline 级别的结构（链接、粗体、斜体等）
- ✅ 可以将 Markdown 作为众多输入格式之一导入
- ✅ 可以将 InfoSection 树导出为 Markdown 文档

Markdown 只是 Info DOM 支持的格式之一，不是核心依赖。

## 设计权衡

### 为什么不细化内容结构？

**问题**：为什么不像 Markdown AST 那样区分段落、列表、代码块等？

**回答**：

1. **格式中立性**：细化结构会锚定特定格式（Markdown）
2. **转换复杂性**：不同格式间的转换（Markdown ↔ JSON）会遇到无法映射的情况
3. **核心目标**：本原型验证的是"LLM 可编辑的层级化上下文"，而非结构化文本格式解析

### 为什么只有 Title 和 Content？

**问题**：为什么不添加更多元数据（作者、时间戳、标签等）？

**回答**：

1. **MVP 原则**：最小化模型，专注核心功能验证
2. **扩展性**：元数据可以编码在 `Content` 中（如 JSON Front Matter）
3. **简单性**：减少 LLM Agent 需要理解的概念

## 下一步

- **编辑 API 设计**：定义 CRUD 操作接口
- **查询 API 设计**：按 ID、Title、路径查找节点
- **ID 编码方案**：Base4096 或其他 LLM 友好的编码
- **序列化格式**：定义 Info DOM 的原生持久化格式

# TODO
Id生成与渲染设计
编辑API设计
查询API设计
