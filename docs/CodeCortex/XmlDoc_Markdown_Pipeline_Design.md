# C# XmlDoc → Markdown 自动化渲染流水线设计（统一规范）

目标：自顶向下定义“从 C# XML 文档注释生成高可读 Markdown”的完整流水线，统一术语、阶段、数据模型与职责边界，形成一份单一、内聚、可演进的设计文档。

不做的事：与具体实现/代码库耦合；强行约束最终文档的结构（ATX 级别与位置由装配阶段决定）。


## 术语表（简）
- ATX：Markdown 标题的 # 语法（例如 ##、### 等）。
- 符号（Symbol）：代码符号（类型、成员等），通常指 Roslyn 的 ISymbol。
- Block：语义块；与具体渲染无关的中立模型（见 §1 数据模型）。
- Section：带 header 的语义块，其 body 是一个 SequenceBlock。
- SequenceBlock：按顺序包含若干 Block 的复合容器。

---

## 0. 总览

分层阶段（输入 → 输出）：
1) CaptureXml：从符号抓取 XML 文本（含 fallback）→ RawXml
2) ParseXml：解析为 XDocument（保留空白）→ XmlDom
3) Normalize：HTML 实体解码、空白规范化、可选 include/inheritdoc 展开 → NormalizedXmlDom
4) ResolveRefs：解析 cref/langword/href、类型显示名映射（可选：System.Int32→int）→ ResolvedXmlDom
5) ElementMapping：Xml 元素到“语义块”的映射（不含布局）→ BlockTree（统一块模型）
6) Layout：对块树应用缩进、空行等版式策略 → LaidOutBlockTree
7) Render：将块树文本化为 Markdown → Markdown
8) Assemble（可选）：将多成员/多类型拼装成章/节，统一确定 ATX 深度与 ToC

---

## 1. 数据模型（统一定义）

- RawXml：string
- XmlDom：XDocument（LoadOptions.PreserveWhitespace）
- Block（语义块，Renderer-agnostic）：
  - ParagraphBlock(text)
  - CodeBlock(text, language?)
  - ListBlock(kind: bullet|number, items: List<SequenceBlock>)
  - TableBlock(headers: List<ParagraphBlock>, rows: List<List<ParagraphBlock>>)
  - SequenceBlock(children: List<Block>)
  - SectionBlock(header: string, body: SequenceBlock)

关键约束：
- SequenceBlock 是唯一复合容器；任何“多个块组合”的语义均通过 SequenceBlock 表达。
- SectionBlock 是“标题 + 内容”的二元组合，标题风格（ATX/冒号）与层级由后续阶段决定。
- 列表项（ListBlock.items）的内容是 SequenceBlock，使其具备与普通正文同等的表达能力（可包含段落、子列表、代码块等）。
- 表格的表头（TableBlock.headers）与单元格（TableBlock.rows 的内容）为 ParagraphBlock，仅承载“单段内联富文本”（如链接、行内代码、cref 展示名等）；不承诺块级子内容（列表、代码块、多段）。若遇到块级内容，见 §2.5.5 的降级策略。

---

## 2. 阶段职责与契约

### 2.1 CaptureXml
- 输入：ISymbol
- 输出：RawXml
- 职责：优先 `ISymbol.GetDocumentationCommentXml()`；若为空，尝试从 LeadingTrivia 清洗出 XML。
- 错误策略：缺失时返回空字符串；不抛异常。

### 2.2 ParseXml
- 输入：RawXml
- 输出：XmlDom
- 职责：`XDocument.Parse(xml, LoadOptions.PreserveWhitespace)`。
- 错误策略：解析失败返回空的占位文档或直接短路后续阶段。

### 2.3 Normalize
- 输入：XmlDom
- 输出：NormalizedXmlDom
- 职责：
  - HTML 实体解码（例如 `&lt;` → `<`，`&amp;` → `&`）。
  - 空白折叠策略（仅在不影响 Markdown 语义的范围内，保留段落边界）。
  - 可选：`<include/>`、`<inheritdoc/>` 展开。
- 选项：`htmlDecode`, `expandInclude`, `expandInheritdoc`。

### 2.4 ResolveRefs
- 输入：NormalizedXmlDom
- 输出：ResolvedXmlDom
- 职责：
  - `cref` → DisplayName（符号短名、泛型参数替换、方法签名简化）。
  - `langword` → 关键字原样文本（`null`、`true`、`false`…）。
  - `href` → 正规化 URL/alt 文本。
  - 可选类型关键字映射：`System.Int32` → `int` 等。
- 选项：`mapPrimitivesToKeywords`, `displayFormat`。
- 错误策略：无法解析时保留原值或降级为文本。

### 2.5 ElementMapping（元素到块的映射）

#### 2.5.1 职责与接口
- **输入**：ResolvedXmlDom
- **输出**：BlockTree (根通常为 SequenceBlock)
- **核心职责**：将 XML 元素按语义映射为 §1 中定义的 Block 实例，构建一个不含布局信息的纯语义树。
- **约束**：不做缩进/空行决策；不输出 ATX 标题；仅保证语义层级正确。

#### 2.5.2 顶层块元素映射
这些 XML 元素在成员级别形成语义分节，统一映射为 `SectionBlock`，以保持语义纯度并由下游阶段决定是否渲染标题及其样式。

- `<summary>`, `<remarks>`, `<example>`, `<returns>`, `<value>`：均映射为 `SectionBlock`。
  - `header` 分别为 "Summary", "Remarks", "Example", "Returns", "Value"。
  - `body` 为其内容递归映射后形成的 `SequenceBlock`。
- `<param>`, `<typeparam>`, `<exception>`：组合映射为“Section of Sections”结构：
    1.  **外层**：一个 `SectionBlock`，`header` 分别为 "Parameters", "Type Parameters", "Exceptions"。
    2.  **内层**：其 `body` 是一个 `SequenceBlock`，包含每个条目对应的 `SectionBlock`。每个条目的 `header` 是参数名/类型参数名/异常类型显示名，`body` 是该条目描述内容映射后的 `SequenceBlock`。
- `<seealso>`：归并为 `SectionBlock("See Also", ...)`，其 `body` 通常是一个包含多个链接的 `ListBlock`。
  - 说明：顶层出现的多个 `<seealso …/>` 应归并为一个“See Also”语义集合；内部元素按本节 2.5.3/2.5.4 的规则生成行内链接或列表项，具体呈现（列表/行内序列）由布局层决定。


#### 2.5.3 内联元素与文本映射
这些元素嵌入在文本容器中，最终成为 `ParagraphBlock` 的一部分。

- **文本节点**, `<para>`, `<br/>`: 组合形成 `ParagraphBlock`。`<para>` 确保段落分隔。
- `<c>inline</c>`: 映射为 `ParagraphBlock` 内的 `` `inline` `` (行内代码)。
- `<code>block</code>`: 映射为独立的 `CodeBlock`。
- `<see langword="null"/>`: 映射为 `ParagraphBlock` 内的关键字原样文本 `null`。
- `<see cref="..."/>`: 映射为 `ParagraphBlock` 内的 `[DisplayName]` 文本。
- `<see href="..."/>`: 映射为 `ParagraphBlock` 内的 `[Text](URL)` Markdown 链接。
- `<paramref name="x"/>`, `<typeparamref name="T"/>`: 映射为 `ParagraphBlock` 内的原样文本 `x`, `T`。

#### 2.5.4 列表与表格映射
- `<list type="bullet|number">`: 映射为 `ListBlock`。每个 `<item>` 的内容递归映射为一个 `SequenceBlock`，成为 `ListBlock.items` 的一项。支持嵌套列表与块级内容（段落、子列表、代码块等）；具体紧凑/宽松排版见 §2.6。
- `<list type="table">`: 映射为 `TableBlock`（表格单元为 ParagraphBlock）。
  - 内联化规则：仅保留文本与内联元素（`<see/>`, `<paramref/>`, `<typeparamref/>`, `<c/>`, `langword` 等），并将其合成为一个 `ParagraphBlock`。遇到块级内容（如 `<code>`、子 `<list>`、多 `<para>` 段）触发 §2.5.5 的表格降级策略。
  - **表头**: 读取 `<listheader>` 的每个子元素（通常是 `<term>`），按内联化规则生成 `ParagraphBlock`，组成 `TableBlock.headers`。
  - **行**: 每个 `<item>` 对应一行，其所有子元素（通常是多个 `<term>`/`<description>`）按内联化规则各生成一个 `ParagraphBlock`，按出现顺序组成该行的单元格列表并加入 `TableBlock.rows`。

#### 2.5.5 回退与安全策略
- **未知元素**: 忽略其标签，递归映射其子节点，以最大化保留内容。
- **空负载清理**: 映射后产生的空块（如空段落）应被修剪。
- **转义与安全**:
    - 表格单元格中的 `|` 等特殊字符，建议由 Render 阶段处理转义。
    - XML 保留字符应在源码中以实体写入。本阶段默认输入已是合法的 XML。
- **错误处理**: 捕获异常为诊断注记，不中断整个流水线。
- 行内代码中的反引号冲突：
  - 当 `ParagraphBlock` 内出现无法安全包裹的反引号序列时，推荐由布局/渲染层采取策略：
    - 升级为围栏代码块（优先，确保可读性），或
    - 采用内联转义策略。
  - 映射层保持“内容最大化保留”，不主动改写原文。
- 标准产出：BlockTree，供下游 Layout/Render 使用。

【表格降级策略】当表头或单元格内检测到块级内容时，依据 `tableCellBlockPolicy` 采取以下之一（默认：markdownStrict）：
- markdownStrict（默认）：将块级内容“提升出表格”，在表格之后按出现顺序追加对应块（如代码块、列表），单元格内保留简短占位（可为空或“See below”）；同时通过 DebugUtil 记录一次降级事件（类别建议：XmlDocPipeline）。
- htmlFallback：将整张表降级为原生 HTML `<table>` 渲染，以保真为先（方言依赖较强）。
- inlineCoerce：尽量将块级内容压缩为单段内联形式（如代码块取第一行并用反引号包裹），信息可能丢失，谨慎使用。

### 2.6 Layout（版式策略应用）

#### 2.6.1 职责与接口
- **输入**: BlockTree
- **输出**: LaidOutBlockTree (一个附加了版式指令的树，或直接是渲染就绪的中间形态)
- **核心职责**: 遍历块树，根据配置应用缩进、空行等版式规则。不改变块的语义或内容。

#### 2.6.2 布局策略
- **缩进**: 子块相对父块（如列表项内容）缩进 `indentSize` 个空格，多层累积。
- **空行**:
    - **异质块间**: 在不同类型的块之间（如 Paragraph 后跟 List）插入一个空行 (`blankBetweenHeterogeneousBlocks`)。
    - **同类块间**: 连续的同类块（如多个 ListBlock）之间不插空行，以实现“粘连”效果。
    - **Section 标题**: 标题与其 `body` 内容之间是否空行由 `stickySectionHeading` 选项控制。
    - **表格前**: 是否在表格前强制插入空行由 `insertBlankBeforeTable` 选项控制。
- **紧凑模式**:
    - **列表项**: 当列表项的 `SequenceBlock` 中只有一个 `ParagraphBlock` 时，可采用无额外段落间距的紧凑模式 (`listItemTightWhenSingleParagraph`)。
    - **Section**: 当 Section 的 `body` 只有一个段落时，渲染时可采用标题与段落同行的紧凑预览模式 (`sectionTightWhenSingleParagraph`)。

#### 2.6.3 错误策略
- 无效或空表/空块应被过滤并记录诊断；保持排版稳定，不抛异常。

### 2.7 Render（渲染为 Markdown）

#### 2.7.1 职责与接口
- **输入**: LaidOutBlockTree
- **输出**: Markdown 字符串
- **核心职责**: 将带有版式信息的块树序列化为最终的 Markdown 文本。

#### 2.7.2 渲染策略
- **标题 (Header)**:
    - **Preview 模式**: `SectionBlock.header` 渲染为带冒号的占位符，如 `Summary:`。
    - **Final 模式**: `SectionBlock.header` 渲染为 ATX 标题 (`##`, `###`)。绝对级别由 Assemble 阶段传入的 `baseHeadingLevel` 和块在树中的深度共同决定。
- **列表 (List)**:
    - 有序列表使用显式编号 `1. 2. 3.` (`numberStyle=dot`)，保证跨渲染器的一致性。
    - 无序列表使用 `-` 或 `*`。
- **表格 (Table)**: 生成表头和 `|---|` 对齐行 (`tableAlignmentRow`)。
  - 单元格内容按 ParagraphBlock 渲染为单段内联文本，渲染时负责转义管道符与竖线。
  - 当 `tableCellBlockPolicy=htmlFallback` 且存在块级内容时，整表以 HTML `<table>` 输出；当为 `markdownStrict` 或 `inlineCoerce` 时，遵循 §2.5.5 的相应策略。
- **代码块 (CodeBlock)**: 使用三反引号对 ``` (`codeFence`) 包围，并可附带语言标识。
- **错误处理**: 渲染异常时降级为纯文本并记录诊断，不抛出异常。

### 2.8 Assemble（可选的装配阶段）
- **输入**: 多个 Markdown 片段或 BlockTree
- **输出**: 完整的文档（如单个 .md 文件）
- **职责**:
    - 统一计算并分配绝对的 ATX 标题级别 (`headingAssignment`, `baseHeadingLevel`)。
    - 组合多个成员/类型的文档片段，形成章节。
    - 可选：生成并插入目录 (ToC)。
- **错误策略**: 配置缺失时使用默认值并记录诊断；装配失败时尽量输出可用片段。

---

## 3. 配置项汇总

- **Normalization**: `htmlDecode`, `expandInclude`, `expandInheritdoc`
- **Reference Resolution**: `mapPrimitivesToKeywords`, `displayFormat`
- **Layout**: `indentSize`, `blankBetweenHeterogeneousBlocks`, `stickySectionHeading`, `insertBlankBeforeTable`, `listItemTightWhenSingleParagraph`, `sectionTightWhenSingleParagraph`
- **Rendering**: `numberStyle`, `codeFence`, `tableAlignmentRow`, `renderMode` (`Preview`|`Final`), `tableCellBlockPolicy` (`markdownStrict`|`htmlFallback`|`inlineCoerce`)
- **Assemble**: `headingAssignment` (`Deferred`|`Immediate`), `baseHeadingLevel`

**建议默认值**:
- `htmlDecode=true`, `mapPrimitivesToKeywords=true`, `displayFormat=ShortStable`
- `indentSize=2`, `blankBetweenHeterogeneousBlocks=true`, `stickySectionHeading=true`, `insertBlankBeforeTable=false`, `listItemTightWhenSingleParagraph=true`
- `numberStyle=dot`, `codeFence="```"`, `tableAlignmentRow=true`, `renderMode=Preview`, `tableCellBlockPolicy=markdownStrict`
- `headingAssignment=Deferred`, `baseHeadingLevel=2`

---

## 4. 示例（端到端）

XML（摘录）：
````xml
<member>
  <summary>Use <see cref="System.String"/> and <c>inline</c>.</summary>
  <param name="count">Item count.</param>
  <returns>New instance.</returns>
</member>

````

映射后的块树（概念化）：
````text
Sequence(
  Section("Summary", Sequence(Paragraph("Use [string] and `inline`."))),
  Section("Parameters", Sequence(
    Section("count", Sequence(Paragraph("Item count.")))
  )),
  Section("Returns", Sequence(Paragraph("New instance.")))
)
````

渲染（Preview 占位风格）：
````markdown
Summary:
  Use [string] and `inline`.

Parameters:
  count
    Item count.

Returns:
  New instance.
````

最终装配（ATX 样式，示意）：
````markdown
### Summary
Use [string] and `inline`.

### Parameters
#### count
Item count.

### Returns
New instance.
````

---

## 5. 质量与测试建议
- 解析健壮性：空 XML、无效 XML、未知标签（递归子节点保留内容）。
- 引用解析：cref 到 DisplayName、方法参数简化、泛型参数替换；失败时降级策略。
- 元素映射：列表显式编号、表格对齐行、<code> → 三反引号；表格单元格仅允许 Paragraph（内联）内容。
- 布局稳定：sticky 规则、异质块 1 空行、连续列表无空行。
- 渲染一致：Preview 与 Final 两模式对齐（除标题样式外文本一致）。
- 端到端对照：给定 XML → 期望 Markdown（Preview/Final）。

表格相关专项用例：
- 仅内联富文本（see/paramref/typeparamref/langword/`<c>`）的单元格应完整渲染为单段文本。
- 含 `<code>` 或嵌套 `<list>` 的单元格在 `markdownStrict` 策略下应“提升出表格”，表格内保留空/占位，提升出的块顺序正确；日志记录一次降级。
- 在 `htmlFallback` 策略下应输出 HTML `<table>` 并保留块级内容。
- 在 `inlineCoerce` 策略下，代码块被截断为首行并以内联代码呈现（验证提示信息或文档说明有潜在信息丢失）。

### 5.x 可观测性（建议）
- 统一使用 DebugUtil 打印关键阶段产物（如 BlockTree JSON/Markdown 预览），便于定位版式与映射问题：
  - DebugUtil.Print("XmlDocPipeline", "...关键信息...")
- 日志默认写入 .codecortex/ldebug-logs/XmlDocPipeline.log；控制台输出由环境变量 ATELIA_DEBUG_CATEGORIES 控制（设置为 XmlDocPipeline 或 ALL）。

---

## 6. 扩展点
- ReferenceResolver：自定义 DisplayFormat、跨仓库跳转、外链策略。
- Include/Inheritdoc Provider：结合 DocFX/Sandcastle 等工具的扩展处理。
- BlockVisitor/Transformer：在 Layout 前对 BlockTree 做变换（如合并短段、拆分长行）。
- Renderer：支持多方言（GitHub Flavored Markdown、CommonMark 的差异适配）。

---

## 7. 迁移与落地
- 以本规范为基线调整 Element Mapping 与 Layout/Render 的术语与模型引用（Block/Sequence/Section）。
- 代码层建议实现清晰的阶段 Facade：XmlCapture → XmlParser → XmlNormalizer → RefResolver → BlockBuilder → LayoutEngine → MarkdownRenderer → Assembler；阶段间以模型对象传递。
- 渐进启用：先支持 Preview 渲染稳定 diff；再引入 Final 装配与 ATX 深度策略。

---

## 附录：现状与开放问题（可选）
- 现状：numbered list 使用显式 1./2./3.；表格前空行默认关闭；<code> 渲染为围栏代码块。
- 开放问题：
  - ToC 集成：是否在 Assemble 阶段自动生成局部目录。
  - 表格方言差异：是否提供方言适配层。
  - 行内富文本（强调/粗体）归一策略与渲染透明度。
