# Info DOM: ContextUI 的 MVP 实现

> **背景阅读**：本文档是 [ContextUI 愿景](./context-ui-vision.md) 的首个 MVP 实现。建议先阅读愿景文档以理解核心架构理念。

## MVP 目标

本 MVP 实现 ContextUI 的核心架构，专注于验证以下功能：

- ✅ **Section树 + Content层的两层分离架构**
- ✅ **GUID定位机制**：LLM通过稳定的GUID持有对象引用
- ✅ **Markdown格式支持**：作为第一个Content类型实现
- ✅ **文本操作**：字符串匹配与替换等基础操作
- ✅ **导入/导出**：Markdown文档与InfoSection树的互转

**MVP范围外**（未来扩展）：
- ❌ Content多态体系（IContent接口、继承）
- ❌ 多模态支持（图像、音频等）
- ❌ 代码执行、沙箱等高级工具
- ❌ 事务机制、并发编辑

## 核心架构：Section层 vs Content层

遵循 ContextUI 的两层分离设计：

### Section层：格式无关的容器管理
- 提供层级结构（树状组织）
- 提供GUID锚点（稳定引用）
- 提供导航能力（按ID、路径查找）
- **格式无关**：不关心Content是什么类型

### Content层：格式特定的内容操作
- **MVP阶段**：只实现文本类Content（Markdown、Plain）
- **未来扩展**：多态工具体系（见愿景文档）
- 每种Content类型有专属的操作集

**关键点**：Section层的API设计要为未来的Content多态预留扩展空间。

## InfoSection：Section层的核心数据结构

InfoSection 是 Section 层的核心节点类型，负责容器管理，不关心 Content 的具体格式。

```csharp
/// <summary>
/// ContentFormat标记Content的类型。
/// MVP阶段：简单枚举
/// 未来演化：配合IContent接口族实现多态操作
/// </summary>
public enum ContentFormat : ushort {
    Plain = 0,      // 纯文本
    Markdown = 1,   // Markdown格式文本
    Json = 2        // JSON格式文本（MVP阶段不支持结构化操作）
}

/// <summary>
/// InfoSection是Section层的核心节点，负责树状结构管理和GUID定位。
/// Content层的操作由专门的工具接口提供（见IInfoDomTools）。
/// </summary>
public class InfoSection {
    /// <summary>
    /// Section的唯一标识符，LLM通过GUID持有对象引用
    /// 设计意图：类似GUI窗口系统的窗口ID
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// 子Section列表，形成树状结构
    /// Section层关注点：容器的层级组织
    /// </summary>
    public List<InfoSection> Children { get; }

    /// <summary>
    /// 计算值，在树中的绝对深度（根节点为0）
    /// 实现：每次访问时向上遍历到根节点计算，不缓存
    /// 移动节点时需进行无环检查
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// Section的标题，用于导航和人类可读性
    /// 约束：不能包含换行符（导入时自动移除）
    /// null 表示根节点
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Section的内容，格式由ContentFormat指定
    /// MVP阶段：自由格式字符串
    /// 未来演化：可能引用IContent接口的实例
    /// </summary>
    public string Content { get; set; }

    /// <summary>
    /// Content的格式类型
    /// MVP阶段：用于选择导入/导出适配器
    /// 未来演化：用于动态派发到对应的IContent操作集
    /// </summary>
    public ContentFormat ContentFormat { get; set; }

    /// <summary>
    /// 缓存的解析后的Content对象
    /// 生命周期：设置Content时根据ContentFormat自动解析
    /// - 解析成功：ParsedContent = 对应格式的内存对象
    /// - 解析失败：ParsedContent = null
    /// 由对应格式的Importer负责解析（如MarkdownImporter）
    ///
    /// 设计意图：
    /// 1. 导出时可以内联为子对象而非转义字符串（提高格式一致性）
    /// 2. 未来可扩展为IContent接口的实例（支持多态操作）
    /// </summary>
    public object? ParsedContent { get; set; }
}
```

### 设计说明

#### 1. **Section层 vs Content层的分离**

**Section层**（InfoSection类）：
- 关注点：容器的层级结构、GUID定位、导航
- 格式无关：不关心Content是文本、图像还是代码
- 类比：GUI窗口管理器（提供窗口ID、位置、层级）

**Content层**（通过工具接口操作）：
- 关注点：格式特定的内容操作
- MVP实现：Markdown和文本操作
- 未来扩展：多态工具体系（代码执行、图像处理等，见愿景文档）
- 类比：GUI应用窗口内容（不同应用有不同交互方式）

#### 2. **GUID的核心作用**

- **稳定锚点**：LLM通过GUID持有对象引用，不受Title变化影响
- **精确定位**：无歧义地定位到特定Section
- **编码优化**：使用Base4096将GUID编码为11个字符（每字符1 Token）

#### 3. **Title的用途**

- **导航索引**：快速定位和理解节点语义
- **人类可读**：提供节点的简短描述
- **约束**：不能包含换行（`\n`、`\r`），导入时自动替换为空格或移除

#### 4. **ContentFormat的演化路径**

**MVP阶段**：
```csharp
public enum ContentFormat { Plain, Markdown, Json }
```
- 用于选择导入/导出适配器
- 简单枚举即可满足需求

**未来演化方向**（愿景）：
```csharp
// Content多态体系
public interface IContent {
    ContentFormat Format { get; }
    string RawText { get; }
}

public interface ITextContent : IContent {
    // 文本类共享操作
    string FindAndReplace(string pattern, string replacement);
    string Append(string text);
}

public interface ICodeContent : ITextContent {
    // 代码特定操作
    Task<ExecutionResult> RunInSandbox(string[] args);
    Task<LintResult> Lint();
}

public interface IImageContent : IContent {
    // 图像特定操作
    Task<ImageData> Zoom(float scale);
    Task<ImageData> Crop(Rectangle area);
}
```

**关键点**：MVP使用枚举+字符串，但结构上预留了向多态接口演化的路径。

#### 5. **ParsedContent的双重意图**

**当前用途**（MVP）：
- 导出时将Content内联为子对象而非转义字符串
- 例如：Markdown导出时，嵌套Markdown的Content不需要转义

**未来用途**（愿景）：
- 持有IContent接口实例，支持多态操作
- 例如：`(section.ParsedContent as ICodeContent)?.RunInSandbox(args)`

#### 6. **根节点约定**

InfoSection 树的根通常是一个特殊节点：

```csharp
var root = new InfoSection {
    Id = Guid.NewGuid(),
    Title = null,  // 或使用文件名等有意义的标题
    Content = "",
    ContentFormat = ContentFormat.Plain,
    Children = [/* 顶级Section */]
};
```

**导入规则**：
- 为了能整体作为一个节点添加到树中，导入操作需填入根节点的 `Title`，例如文件名。
- 文档自身的标题（如 Markdown 的 `# Project Documentation`）统一作为根节点的**第一个子节点**，避免特化逻辑。

## Content层的操作策略

### MVP阶段：Markdown和文本操作

**导入Content时的自动解析**：
- 根据 `ContentFormat` 自动选择相应的 Importer
- **Markdown**：按Heading创建子Section树，Heading下的内容作为该Section的 `Content` 保留
- **JSON**：MVP阶段不支持结构化操作，保留为字符串
- **Plain**：不进行结构化解析，直接保留

**导出时的内联策略**：
- 若目标导出格式与 `ContentFormat` 相同，则内联为子对象（减少转义）
- 例如：Markdown导出时，嵌套Markdown的 `Content` 不需要转义

### 未来扩展：多态工具体系（愿景）

Content层的操作将演化为多态工具集，每种Content类型有专属操作：

- **文本类Content**（Markdown, Plain, Code）：
  - `FindAndReplace(pattern, replacement)` - 字符串匹配替换
  - `Append(text)` - 追加内容

- **代码类Content**（继承自文本类）：
  - `RunInSandbox(args)` - 沙箱执行
  - `Lint()` - 代码检查
  - `Format()` - 代码格式化

- **图像类Content**：
  - `Zoom(scale)` - 缩放
  - `Crop(area)` - 裁剪
  - `Annotate(annotations)` - 添加标注

- **音频类Content**：
  - `Transcribe()` - 语音转文本
  - `ExtractFeatures()` - 提取声纹特征

详见 [ContextUI 愿景文档](./context-ui-vision.md)。

## Section层的基本操作（MVP）

以下是 MVP 阶段支持的 Section 层操作：

### 创建Section
```csharp
var section = new InfoSection {
    Id = Guid.NewGuid(),
    Title = "Introduction",
    Content = "This is the intro content.",
    ContentFormat = ContentFormat.Plain,
    Children = []
};
```

### 导航Section树
```csharp
// 按Title查找
var child = section.Children.FirstOrDefault(s => s.Title == "Background");

// 按GUID查找（未来通过工具接口提供）
var target = FindSectionById(root, targetGuid);
```

### 编辑Section
```csharp
// Section层操作：修改结构和元数据
section.Title = "Updated Title";
section.Children.Add(newSection);
section.Children.Remove(oldSection);

// Content层操作：通过工具接口修改内容（见后文）
// 不直接操作 section.Content 字符串
```

## 导入与导出（格式适配器）

InfoSection 树本身不包含导入/导出逻辑，这些功能由独立的**格式适配器**提供：

- **Markdown 适配器**：InfoSection树 ↔ Markdown文档（详见 [markdown-import.md](./markdown-import.md)）
- **JSON 适配器**：InfoSection树 ↔ JSON结构（MVP阶段仅支持导入为字符串）
- **其他格式**：未来可扩展（AsciiDoc、reStructuredText、XML等）

**设计意图**：
- Section层与Content层分离，适配器作为桥梁
- 新增格式支持不影响核心数据结构
- 未来可支持双向转换（InfoSection ↔ 多种格式）

### Markdown 导入的层级映射规则

对于以下 Markdown 文档：

```markdown
# Title

## Subtitle

Content here.

- List item 1
- List item 2

### Sub-subtitle

More content.
```

导入后的 InfoSection 树结构：

```
InfoSection (Title)
  └─ InfoSection (Subtitle)
      ├─ Content: "Content here.\n\n- List item 1\n- List item 2"
      └─ InfoSection (Sub-subtitle)
          └─ Content: "More content."
```

**规则说明**：
- 标题创建新的 `InfoSection` 节点，标题文本作为 `Title`
- 标题下的内容（段落、列表、代码块等）作为该节点的 `Content` 属性，按原样保留为 Markdown 字符串
- 子标题创建嵌套的子节点

### 示例：多格式并存

```csharp
var root = new InfoSection {
    Title = "Project Documentation",
    Children = [
        new InfoSection {
            Title = "Overview",
            Content = "This is **Markdown** content.",
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

**InfoSection 不是 Markdown DOM**：

- ❌ 不追求 Markdown 的精确表示（不区分 ATX/Setext 标题、列表样式等）
- ❌ 不处理 Inline 级别的结构（链接、粗体、斜体等）
- ✅ 可以将 Markdown 作为众多格式之一导入
- ✅ 可以将 InfoSection 树导出为 Markdown 文档

**关键点**：Markdown 只是 ContextUI 支持的第一个 Content 类型，不是核心依赖。

## 设计权衡与FAQ

### 为什么不细化Content的内部结构？

**问题**：为什么不像 Markdown AST 那样区分段落、列表、代码块等？

**回答**：

1. **格式中立性**：细化结构会锚定特定格式（Markdown），违背 Section 层的格式无关性原则
2. **简化建模**：Markdown 的 Heading 天然分层级，我们只映射 Heading → Section，不进一步建模 Container Blocks（如列表项）
3. **扩展性**：未来若需要细粒度操作，可以在 Content 层通过多态工具实现，不影响 Section 层
4. **核心目标**：MVP 验证"LLM 可编辑的层级化上下文"，而非通用文本格式解析器

### 为什么只有 Title 和 Content？

**问题**：为什么不添加更多元数据（作者、时间戳、标签等）？

**回答**：

1. **MVP 原则**：最小化模型，专注核心功能验证
2. **扩展性**：元数据可以编码在 `Content` 中（如 JSON Front Matter）
3. **简单性**：减少 LLM Agent 需要理解的概念
4. **未来扩展**：可以通过继承或组合添加元数据支持

### 为什么分 Section 层和 Content 层？

**问题**：直接让 Section 包含多种 Content 类型不行吗？

**回答**：

1. **关注点分离**：Section 管结构，Content 管交互，各司其职
2. **稳定演化**：Section API 保持稳定，Content 可以灵活扩展新类型
3. **多态扩展**：未来 Content 的多态操作不会污染 Section 的核心接口
4. **窗口系统类比**：Section 是窗口管理器（稳定），Content 是应用内容（动态）

## 工具接口设计（LLM调用的API）

```csharp
/// <summary>
/// 面向LLM的ContextUI工具接口
/// 设计原则：
/// 1. 输入输出都是string（LLM友好）
/// 2. 区分Section层操作和Content层操作
/// 3. 为未来扩展预留空间（当前只实现Markdown和文本操作）
/// </summary>
interface IInfoDomTools {
    // === Section层操作（格式无关）===

    /// <summary>
    /// 创建新Section（不指定Content）
    /// </summary>
    /// <param name="parentSectionId">父Section的GUID，null表示根节点</param>
    /// <param name="title">新Section的标题</param>
    /// <returns>新Section的GUID</returns>
    string CreateSection(string? parentSectionId, string title);

    /// <summary>
    /// 移动Section到新父节点下
    /// </summary>
    string MoveSection(string sectionId, string newParentId);

    /// <summary>
    /// 删除Section（及其所有子Section）
    /// </summary>
    string DeleteSection(string sectionId);

    /// <summary>
    /// 查询Section信息（Title、深度、子节点数等）
    /// </summary>
    string QuerySection(string sectionId);

    // === Content层操作（格式特定）===
    // MVP阶段：Markdown + 文本操作

    /// <summary>
    /// 用Markdown格式创建新Section子树
    /// </summary>
    /// <param name="parentSectionId">导入到哪个父节点下，null表示根节点</param>
    /// <param name="heading">导入内容的总标题（不含ATX符号"#"）</param>
    /// <param name="content">Markdown格式的内容，子heading会被创建为子Section</param>
    /// <returns>操作结果描述</returns>
    string WriteNewMarkdown(string? parentSectionId, string heading, string content);

    /// <summary>
    /// 替换Section的标题
    /// </summary>
    string ReplaceSectionTitle(string sectionId, string newTitle);

    /// <summary>
    /// 替换Section的Content（不触发子Section创建）
    /// </summary>
    /// <param name="sectionId">目标Section的GUID</param>
    /// <param name="contentFormat">内容格式（Plain/Markdown/Json）</param>
    /// <param name="newContent">新内容字符串</param>
    /// <returns>操作结果描述</returns>
    string ReplaceSectionContent(string sectionId, string contentFormat, string newContent);

    /// <summary>
    /// 文本查找替换（适用于所有文本类Content）
    /// </summary>
    string FindAndReplace(string sectionId, string pattern, string replacement);

    // === 未来扩展示例（文档说明，暂不实现）===
    // string RunPythonContent(string sectionId, string[] args);  // 代码执行
    // string ZoomImageContent(string sectionId, float scale);    // 图像缩放
    // string TranscribeAudioContent(string sectionId);           // 音频转录
}
```

**设计要点**：
- **Section层操作**：格式无关，适用于所有Content类型
- **Content层操作**：格式特定，MVP只实现文本和Markdown
- **扩展路径**：未来添加新Content类型时，只需添加新的Content层操作，Section层API保持稳定

## 演化路径：从StringBuilder到ContentBuilder

ContextUI 的长期愿景是支持多模态内容，当前 MVP 采用渐进式演化策略：

### 当前（MVP）：纯文本渲染
```csharp
public string RenderToMarkdown(InfoSection root) {
    var builder = new StringBuilder();
    RenderSectionRecursive(root, builder, depth: 0);
    return builder.ToString();
}

void RenderSectionRecursive(InfoSection section, StringBuilder builder, int depth) {
    // 渲染标题
    if (section.Title != null) {
        builder.Append(new string('#', depth + 1))
               .Append(' ')
               .AppendLine(section.Title);
    }

    // 渲染内容
    if (!string.IsNullOrEmpty(section.Content)) {
        builder.AppendLine(section.Content);
    }

    // 递归渲染子节点
    foreach (var child in section.Children) {
        RenderSectionRecursive(child, builder, depth + 1);
    }
}
```

### 未来：多模态内容构建
```csharp
public Part[] RenderToMultimodal(InfoSection root) {
    var builder = new ContentBuilder();
    RenderSectionRecursive(root, builder, depth: 0);
    return builder.ToParts();
}

void RenderSectionRecursive(InfoSection section, ContentBuilder builder, int depth) {
    // 逻辑不变，只是输出目标从StringBuilder升级为ContentBuilder

    if (section.Title != null) {
        builder.AddText($"{"#".Repeat(depth + 1)} {section.Title}\n");
    }

    // 根据ContentFormat选择渲染方式
    switch (section.ContentFormat) {
        case ContentFormat.Markdown:
        case ContentFormat.Plain:
            builder.AddText(section.Content);
            break;
        case ContentFormat.Image:
            builder.AddImage((section.ParsedContent as IImageContent).Data);
            break;
        case ContentFormat.Audio:
            builder.AddAudio((section.ParsedContent as IAudioContent).Data);
            break;
    }

    foreach (var child in section.Children) {
        RenderSectionRecursive(child, builder, depth + 1);
    }
}
```

**关键点**：
- `RenderSectionRecursive` 的递归逻辑保持不变
- 只升级参数类型：`StringBuilder` → `ContentBuilder`
- 接口稳定，实现平滑演化

## 下一步

### MVP阶段的任务
- ✅ 定义 InfoSection 数据结构
- ✅ 定义 IInfoDomTools 工具接口
- 🔄 实现 Markdown 导入器（Heading → Section树）
- 🔄 实现 Markdown 导出器（Section树 → Markdown文档）
- 🔄 实现基础的Section层操作（Create, Move, Delete, Query）
- 🔄 实现基础的Content层操作（WriteMarkdown, ReplaceContent, FindAndReplace）

### 未来扩展方向
- **GUID编码优化**：Base4096编码（11字符，每字符1 Token）
- **路径查询**：支持类似文件系统的路径（如 `"Docs/Architecture/Overview"`）
- **Content多态**：IContent接口族，支持继承和多态操作
- **多模态支持**：图像、音频、代码等Content类型
- **ContentBuilder**：多模态内容构建器
- **序列化格式**：定义原生持久化格式（JSON/Binary）

## MVP阶段的简化

当前原型聚焦于核心功能验证，以下特性暂不实现：

- ❌ 事务和回滚机制（直接在内存对象上操作）
- ❌ 并发编辑和冲突解决
- ❌ JSON内容的自动子树展开
- ❌ 编辑预览、取消、Undo功能
- ❌ Content多态体系（IContent接口）
- ❌ 多模态支持（图像、音频等）

未来可通过新 Project 或 Feature 分支逐步增强功能。

---

**相关文档**：
- [ContextUI 愿景](./context-ui-vision.md) - 长期愿景和架构理念
- [Markdown 导入规则](./markdown-import.md) - Markdown格式的详细映射规则
