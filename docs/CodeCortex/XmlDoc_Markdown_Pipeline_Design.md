# C# XmlDoc → Markdown 自动化渲染流水线设计（统一规范）

目标：自顶向下定义“从 C# XML 文档注释生成高可读 Markdown”的完整流水线，统一术语、阶段、数据模型与职责边界，使 Element Mapping 与 Layout/Render 两份文档可以无缝衔接并可演进。

不做的事：与具体实现/代码库耦合；强行约束最终文档的结构（ATX 级别与位置由装配阶段决定）。


## 术语表（简）
- ATX：Markdown 标题的 # 语法（例如 ##、### 等）。
- 符号（Symbol）：代码符号（类型、成员等），通常指 Roslyn 的 ISymbol。
- Block：语义块；与具体渲染无关的中立模型（见 §1 数据模型）。
- Section：带 header 的语义块，body 为 SequenceBlock。
- SequenceBlock：按顺序包含若干 Block 的复合容器。

---

## 0. 总览

分层阶段（输入 → 输出）：
1) CaptureXml：从符号抓取 XML 文本（含 fallback）→ RawXml
2) ParseXml：解析为 XDocument（保留空白）→ XmlDom
3) Normalize：HTML 实体解码、空白规范化、可选 include/inheritdoc 展开 → NormalizedXmlDom
4) ResolveRefs：解析 cref/langword/href、类型显示名映射（可选：System.Int32→int）→ ResolvedXmlDom
5) ElementMapping：Xml 元素到“语义块”的映射（不含布局）→ BlockTree（统一块模型）
6) Layout：缩进、空行、列表/表格粘连策略（不改写语义）→ LaidOutBlockTree
7) Render：Markdown 文本化；可选择 Preview（冒号占位）或 Final（ATX 级别）→ Markdown
8) Assemble（可选）：将多成员/多类型拼装成章/节，统一确定 ATX 深度与 ToC

备注：
  - 阶段 5 引用《C# XML 文档注释 → Markdown 元素级映射设计（Element Mapping Spec）》；
  - 阶段 6/7 引用《XmlDoc → Markdown 的 Layout/Render 设计（去耦合、组合式模型）》。

---

## 1. 数据模型（统一定义）

- RawXml：string
- XmlDom：XDocument（LoadOptions.PreserveWhitespace）
- Block（语义块，Renderer-agnostic）：
  - ParagraphBlock(text)
  - CodeBlock(text, language?)
  - ListBlock(kind: bullet|number, items: List<SequenceBlock>)
  - TableBlock(headers: List<string>, rows: List<List<string>>)
  - SequenceBlock(children: List<Block>)
  - SectionBlock(header: string, body: SequenceBlock)

关键约束：
- SequenceBlock 是唯一复合容器；任何“多个块组合”的语义均通过 SequenceBlock 表达。
- SectionBlock = header + body（body 必为 SequenceBlock）。header 仅为语义，不含呈现风格（ATX/冒号延后）。

---

## 2. 阶段职责与契约

### 2.1 CaptureXml
- 输入：ISymbol
- 输出：RawXml
- 职责：优先 ISymbol.GetDocumentationCommentXml()；若为空，尝试从 LeadingTrivia 清洗出 XML。
- 错误策略：缺失时返回空字符串；不抛异常。

### 2.2 ParseXml
- 输入：RawXml
- 输出：XmlDom
- 职责：XDocument.Parse(xml, PreserveWhitespace)。
- 错误策略：解析失败返回空的占位文档或直接短路后续阶段（无文档）。

### 2.3 Normalize
- 输入：XmlDom
- 输出：NormalizedXmlDom
- 职责：
  - HTML 实体解码（例如 &lt; → <，&amp; → &）。
  - 空白折叠策略（仅在不影响 Markdown 语义的范围内，保留段落边界）。
  - 可选：<include/>、<inheritdoc/> 展开（若无工具支持，则保持原样，交给 Assemble/文档生成器）。
- 选项：htmlDecode=true；expandInclude=false；expandInheritdoc=false。

### 2.4 ResolveRefs
- 输入：NormalizedXmlDom
- 输出：ResolvedXmlDom
- 职责：
  - cref → DisplayName（符号短名、泛型参数替换、方法签名简化）。
  - langword → 关键字原样文本（null、true、false…）。
  - href → 正规化 URL/alt 文本。
  - 可选类型关键字映射：System.Int32 → int 等（MapPrimitivesToKeywords）。
- 选项：mapPrimitives=true；displayFormat=ShortStable。
- 错误策略：无法解析时保留原值/降级为文本。

### 2.5 ElementMapping（核心衔接点）
- 输入：ResolvedXmlDom
- 输出：BlockTree（SequenceBlock/SectionBlock 等）
- 职责：将 Xml 元素按《Element Mapping》映射为语义块：
  - 段落/换行：<para/>、文本 → ParagraphBlock
  - 行内代码：<c> → Paragraph 内的 `code span`（已在文本中形成，不再建块）
  - 代码块：<code> → CodeBlock（纯文本，不含三反引号）
  - 列表：<list type="bullet|number"> → ListBlock（items 为 SequenceBlock；支持嵌套）
  - 表格：<list type="table"> → TableBlock
  - 链接：<see cref|href|langword/> → 段落内的链接文本（由 ResolveRefs 阶段产出）
  - 分节：
    - summary/remarks/example/returns/value → SectionBlock(header=固定标题, body=SequenceBlock)
    - param/typeparam/exception → SectionBlock("Parameters"|"Type Parameters"|"Exceptions")；每个条目再是一个子 SectionBlock（header=名称/显示名，body=条目内容的 SequenceBlock）
    - seealso → SectionBlock("See Also")，body 内通常是 ListBlock（链接条目）
- 约束：不做缩进/空行决策；不输出 ATX 标题；仅保证语义层级正确。

### 2.6 Layout
- 输入：BlockTree
- 输出：LaidOutBlockTree（或直接文本化时的排版指令）
- 职责：
  - 缩进策略：子块相对父块 indentSize 空格（建议 2），多层累积。
  - 空行策略：异质块之间插入 1 空行；同类连续 List 不插空行；Section 标题与首块可配置 sticky。
  - 表格粘连：标题/段落 → 表格前 1 空行；Section 标题 → 不加空行。
- 选项：indentSize、blankBetweenHeterogeneousBlocks、stickySectionHeading、insertBlankBeforeTable、listItemTightWhenSingleParagraph、sectionTightWhenSingleParagraph。

### 2.7 Render
- 输入：LaidOutBlockTree
- 输出：Markdown
- 职责：
  - 列表编号：有序列表输出显式 1. 2. 3.
  - 表格：生成表头与对齐行（|---|）。
  - 代码块：三反引号 ```（或按选项使用缩进式）。
  - 标题：
    - Preview 模式：标题渲染为 "Heading:" 行（占位）。
    - Final 模式：由 Assemble 决定 ATX 级别（baseHeadingLevel + 深度），Renderer 负责具体输出。

### 2.8 Assemble（可选）
- 输入：多个 Markdown 片段或 BlockTree
- 输出：完整文档（含 ToC/章节）
- 职责：统一计算绝对标题级别、组合章节、插入目录（可选）、统一风格。

---

## 3. 配置项（跨阶段归属）
- Parsing/Normalization：htmlDecode, expandInclude, expandInheritdoc
- Reference Resolution：mapPrimitives, displayFormat
- Layout：indentSize, blankBetweenHeterogeneousBlocks, stickySectionHeading, insertBlankBeforeTable
- Rendering：numberStyle=dot, codeFence=```, tableAlignmentRow=true
- Final Assembly：headingStyle=ATX|Preview, baseHeadingLevel

建议默认：
- htmlDecode=true；mapPrimitives=true；displayFormat=短且稳定
- indentSize=2；blankBetweenHeterogeneousBlocks=true；stickySectionHeading=true；insertBlankBeforeTable=true
- numberStyle=dot；codeFence=```；tableAlignmentRow=true
- headingStyle=Deferred（最终装配阶段统一决策）；baseHeadingLevel=2

---

## 4. 示例（端到端）

XML（摘录）：
<augment_code_snippet mode="EXCERPT">
````xml
<member>
  <summary>Use <see cref="System.String"/> and <c>inline</c>.</summary>
  <param name="count">Item count.</param>
  <returns>New instance.</returns>
</member>
````
</augment_code_snippet>

映射后的块树（概念化）：
<augment_code_snippet mode="EXCERPT">
````text
Sequence(
  Section("Summary", Sequence(Paragraph("Use [string] and `inline`."))),
  Section("Parameters", Sequence(
    Section("count", Sequence(Paragraph("Item count.")))
  )),
  Section("Returns", Sequence(Paragraph("New instance.")))
)
````
</augment_code_snippet>

渲染（Preview 占位风格）：
<augment_code_snippet mode="EXCERPT">
````markdown
Summary:
  Use [string] and `inline`.

Parameters:
  count
    Item count.

Returns:
  New instance.
````
</augment_code_snippet>

最终装配（ATX 样式，示意）：
<augment_code_snippet mode="EXCERPT">
````markdown
### Summary
Use [string] and `inline`.

### Parameters
#### count
Item count.

### Returns
New instance.
````
</augment_code_snippet>

---

## 5. 质量与测试建议
- 解析健壮性：空 XML、无效 XML、未知标签（递归子节点保留内容）。
- 引用解析：cref 到 DisplayName、方法参数简化、泛型参数替换；失败时降级策略。
- 元素映射：列表显式编号、表格对齐行、<code> → 三反引号。
- 布局稳定：sticky 规则、异质块 1 空行、连续列表无空行。
- 渲染一致：Preview 与 Final 两模式对齐（除标题样式外文本一致）。
- 端到端对照：给定 XML → 期望 Markdown（Preview/Final）。

---

## 6. 扩展点
- ReferenceResolver：自定义 DisplayFormat、跨仓库跳转、外链策略。
- Include/Inheritdoc Provider：结合 DocFX/Sandcastle 等工具的扩展处理。
- BlockVisitor/Transformer：在 Layout 前对 BlockTree 做变换（如合并短段、拆分长行）。
- Renderer：支持多方言（GitHub Flavored Markdown、CommonMark 的差异适配）。

---

## 7. 迁移与落地
- 以本规范为基线调整 Element Mapping 与 Layout/Render 两文档的术语与模型引用（Block/Sequence/Section）。
- 代码层建议实现清晰的阶段 Facade：XmlCapture → XmlParser → XmlNormalizer → RefResolver → BlockBuilder → LayoutEngine → MarkdownRenderer → Assembler；阶段间以模型对象传递。
- 渐进启用：先支持 Preview 渲染稳定 diff；再引入 Final 装配与 ATX 深度策略。

---

## 8. C# XML 文档注释 → Markdown 元素级映射设计（Element Mapping Spec）

目的：定义“元素级（Element-level）”的语义映射规则，把 C# XML 文档注释转换为可读性高的 Markdown 原语（行/段落/结构行）。本规范仅覆盖元素到 Markdown 原语的对应关系，不涉及：
- 布局/渲染层职责（如：空行插入、缩进、节点标题的 Markdown 级别“##/###”确定、分节顺序等）；
- 文档装配（跨块组成树、跨成员合并）；

这些由后续 Layout/Render 与 Outline 组装层承担。

---

### 1. 术语与模型
- 文本行（text line）：普通 Markdown 文本行，最终可由渲染层决定是否“bulletizePlain”。
- 结构行（structural line）：带前缀的列表/编号/表格行，渲染层应原样保留其结构特征。
- 块（block）：布局/渲染层的概念，用于分节与标题。本规范不规定块级标题级别，仅提供元素语义到“行序列”的映射。

---
#### 1.1 产出模型说明（与统一流水线对齐）
- 本阶段的标准产物（V2）是 BlockTree，采用统一块模型（详见《XmlDoc_Markdown_Pipeline_Design.md》§1 数据模型）。
- 关键类型：ParagraphBlock、CodeBlock、ListBlock(kind, items: List<SequenceBlock>)、TableBlock、SequenceBlock、SectionBlock(header, body)。
- 历史兼容：早期实现（Core v1）以“行序列 + 定义项集合”作为中间形态；两者可等价投影到上述 BlockTree。


### 2. 顶层块元素的目标语义
这些 XML 元素在“成员级”形成语义分节，映射为渲染层可识别的块（Block）与行序列：

- summary → 段落与结构内容的行序列（文本行/结构行混合）
- remarks → 段落与结构内容的行序列
- example → 段落与结构内容的行序列（通常包含示例代码）
- returns → 返回值说明（行序列）
- value → 属性的值说明（行序列）
- param name=… → 参数定义项（Definition item）：名称 + 内容行序列
- typeparam name=… → 类型参数定义项：名称 + 内容行序列
- exception cref=… → 异常定义项：类型显示名 + 内容行序列
- seealso → 相关链接集合（行序列或链接列表，见 3.4）
- include → 外部片段（预处理或在解析层展开，不直接产出 Markdown）
- inheritdoc → 继承文档（在解析/装配阶段解析，不直接产出 Markdown）

说明：Definition item（参数/类型参数/异常）在布局层通常合成为“定义列表/表格/条目列表”的一部分，但本规范仅要求提供“键（名称/类型显示名）+ 行序列（说明）”。

---

### 3. 文本与内联元素的映射
这些元素可嵌入任何“文本容器”（summary/remarks/returns/value/example/item/description/para/term），映射为行内 Markdown 片段或行内文本：

#### 3.1 代码与关键字
- <c>inline</c> → `inline`（行内代码，单对反引号）
- <code>block</code> → 独占一段的代码“行”；为保持简洁与可组合：
  - 映射为独立的行内代码段，前后各插入一空行（布局层可升级为```围栏代码块）。
  - 当前实现（Core v1）产出：空行 + `content` + 空行；V2 渲染层可将其布局化为围栏代码。
- <see langword="null|true|false|…"/> → 关键字原样文本（例如：null、true）

#### 3.2 符号与链接
- <see cref="T:Namespace.Type"/> 或成员 → [DisplayName]（方括号强调，现阶段不附带链接 URL；DisplayName 由语义解析产出如 Foo<T>(), int 等）
- <see href="https://…"/> 或 <see href="…" alt="Text"/> → [Text](URL)
  - Text 为空时使用 URL 作为可见文本。
- <paramref name="x"/> / <typeparamref name="T"/> → x / T（原样文本，不加格式）

#### 3.3 段落控制
- <para>…</para> → 在其前后插入换行，内部递归映射（确保段落分隔）。
- <br/> → 换行（等价于在当前行结束后插入一个空行）。

#### 3.4 另请参阅（seealso）
- 顶层出现的多个 <seealso …/> 归并为“See Also”语义集合，内部元素映射按 3.2 规则生成行内链接；布局层可选择渲染为列表或行内链接序列。

---

### 4. 列表与结构化内容（list）
list 映射为“结构行”序列，渲染层须原样保留其结构特征；嵌套时通过增加缩进空格（推荐每级 2 个空格）体现层级。

#### 4.1 无序/有序列表
- <list type="bullet">：每个 <item> 映射为一行前缀 "- " 的结构行。
- <list type="number">：每个 <item> 映射为显式编号的结构行："1. ", "2. ", …（不使用 Markdown 的“全 1.”写法）。
- <item> 内容获取规则（优先级）：
  1) 存在 <description> → 使用其递归映射后的扁平化文本（首行）
  2) 否则存在 <term> → 使用 term 文本
  3) 否则回退为“把 item 的子节点递归映射为行内片段后拼接”为单行文本（保留 <see/> 与 <c/> 的内联语义）
- 嵌套 list：允许在 <item> 内嵌套 <list>，子 list 相比父项增加一个层级缩进。

#### 4.2 表格（type="table"）
- <list type="table"> 映射为 Markdown 表格：
  - 若存在 <listheader>，其 <term> 子项形成表头行，随后自动生成对齐分隔行（|---|…|）。
  - 每个 <item> 的多个 <term> 构成一行；若缺失 <term>，可回退使用 <description> 的文本作为单列。
- 表格属于“结构行”，渲染层可按选项决定表格前是否插入空行（布局责任）。

---

### 5. 定义项元素（Definition items）
仅描述“键 + 文本行序列”的语义，最终如何渲染为“定义列表/表格/条目列表”由布局层决定。

- <param name="n">…</param> → 键：n；值：内容递归映射后的行序列
- <typeparam name="T">…</typeparam> → 键：T；值：内容行序列
- <exception cref="…">…</exception> → 键：Cref 显示名（如 InvalidOperationException、ArgumentNullException）；值：内容行序列
- <returns>…</returns> / <value>…</value> → 直接产出行序列（无键），但在布局层会挂载到对应分节标题下。

---

### 6. 未知元素与回退策略
- 未识别的元素：忽略其标签本身，递归映射其子节点（内容最大化保留）。
- 空负载清理：
  - 映射结束后应 TrimLeading/TrimTrailing 空行；
  - 表格行若仅有分隔符且无有效单元格，视为无有效负载（由渲染层过滤）。
- HTML 解码：源 XML 文本先做 HTML 实体解码（例如 &lt;T&gt; → <T>），随后进入 Markdown 映射。

---

### 7. 转义与安全（Mapping 层职责）
- 行内代码 `…` 中出现反引号时，可在布局层升级为围栏代码块，或采用“内联 code span 转义”的策略；映射层可原样保留，避免过度转义。
- 表格单元格中的竖线（|）建议在布局/渲染层做转义或自动拆分为多列；映射层不强制改写原文。
- XML 保留字符应当在源注释中以实体写入；若未写入，由样式分析器（如 MT0101）在代码修复层面补救。

---

### 8. 兼容性与现状（与当前实现对齐）
- 显式编号（numbered list）采用 "1.", "2."…：已在 Core 与 V2 测试覆盖。
- 表格前空行：属于布局层策略，默认“开启/可控”（V2 渲染允许插入；Core 测试含断言）。
- <code> 的处理：
  - Core v1：映射为独占一行的 `inline-code`（前后空行）；
  - V2：布局层可升级为围栏代码块（```），本规范不强制具体外观。
- <see cref>：映射为 [DisplayName]（不带 URL）；<see href>：映射为 [Text](URL)。

---

### 9. 示例（元素级映射片段）

XML：
<augment_code_snippet mode="EXCERPT">
````xml
<summary>
  Use <see cref="System.String"/> and <c>inline</c>.
  <list type="number">
    <item>Step A</item>
    <item><description>Step B with <see href="https://ex">link</see></description></item>
  </list>
</summary>
````
</augment_code_snippet>

映射后的行序列（示意）：
<augment_code_snippet mode="EXCERPT">
````markdown
Use [string] and `inline`.
1. Step A
2. Step B with [link](https://ex)
````
</augment_code_snippet>

---

### 10. 与布局/渲染层的接口约定（非本规范职责）
- 标准输出（V2）：BlockTree（Sequence/Section 等），供下游 Layout/Render 使用。模型定义见《XmlDoc_Markdown_Pipeline_Design.md》§1。
- 历史兼容：如在 Core v1 通道仍产出“行序列 + 定义项集合”，应提供到 BlockTree 的等价转换。
- 布局层负责：
  - 分节与标题呈现（绝对 ATX 级别与位置在最终装配阶段统一决定）
  - 缩进与空行策略（包含表格前的空行插入）
  - 代码块样式（是否将 code 升级为围栏形式）
  - 定义项的最终外观（可渲染为“Section of Sections”、表格或项目符号列表）

---

### 11. 扩展与后续工作
- 链接分辨率：将 [DisplayName] 映射为可点击链接（结合符号解析/文档站点路由）
- 语义化 deflist：在 JSON/中间层提供标准 deflist Block，由 MarkdownLayout 统筹渲染
- 国际化：支持多语言关键字与本地化分节标题
- 验证与 Lint：为 param/typeparam/exception 的 name/cref 一致性提供编译期或 CI 级校验

---

## 9. XmlDoc → Markdown 的 Layout/Render 设计（去耦合、组合式模型）

目的：摆脱旧实现的束缚，从“语义 → 版式”的角度重新定义 Layout/Render 层。V2 不设“DefListBlock”，改用组合式容器来表达一切结构；Section 仅是“有标题的块”，其内容是一个通用的复合容器。

与 Element Mapping 的关系：
- Element Mapping 阶段产出统一的 BlockTree（见《XmlDoc_Markdown_Pipeline_Design.md》§1 数据模型），包含 Paragraph/Code/List/Table/Sequence/Section 等语义块。
- 本设计负责对 BlockTree 应用版式策略（缩进、空行、列表编号/粘连等）并渲染为 Markdown；绝对 ATX 级别延迟到最终装配阶段决定。

---

### 1. 核心 Block 模型（全新）
- ParagraphBlock(text)
  - 承载已完成 inline 级映射的纯文本（含 `code span`、[link] 等）。
- CodeBlock(text, language?)
  - 承载多行代码片段（来自 <code> 等）。
- ListBlock(kind: bullet|number, items: List<SequenceBlock>)
  - 每个列表项是一个“可包含多块”的 SequenceBlock（允许条目内段落、表格、子列表等）。
- TableBlock(headers: List<string>, rows: List<List<string>>)
  - 单元格为纯文本（已完成 inline 映射）。
- SequenceBlock(children: List<Block>)
  - 顺序保持的通用复合容器（组合根基），用于承载任意块序列。
- SectionBlock(header: string, body: SequenceBlock)
  - 语义上的“有标题的块”。header 仅是标题内容本身，不含呈现样式（ATX/冒号等由最终装配决定）。

设计动机：
- 统一使用 SequenceBlock 表达“聚合关系”，避免为某一种展示风格（如 deflist）固定数据结构。
- 列表项允许多块，使 list item 的内容具备与普通正文同等的表达能力。
- Section 是“标题 + 内容”的二元组合，标题风格（##/###/冒号）与相对/绝对层级交由最终装配阶段决定。

---

### 2. 逻辑分组到块树的组织
将 Element Mapping 提供的逻辑分组，按如下原则转为块树：

- Summary/Remarks/Example → SequenceBlock（由 Paragraph/List/Table/Code 自然组成）。
- Returns/Value → SectionBlock("Returns"|"Value", body: SequenceBlock)。
- See Also → SectionBlock("See Also", body: SequenceBlock)，其 body 多为列表（超链接条目）。
- Parameters/Type Parameters/Exceptions：
  - 外层：一个 SectionBlock（标题分别为 "Parameters"、"Type Parameters"、"Exceptions"）。
  - 内层：将每个条目映射为一个 SectionBlock（header 为参数名/类型参数名/异常显示名，body 为该条目的 SequenceBlock）。
  - 这样得到“Section of Sections”的一致表达，替代 DefList。

说明：
- 若条目的 body 只有一个段落，渲染器可以在展示层采用“紧凑模式”（例如标题与首段同行），但这属于可选的渲染策略，不属于模型本身。

---

### 3. 渲染职责与策略（Renderer-agnostic）

#### 3.1 标题（Header）策略
- 绝对层级延迟：SectionBlock 的 header 不带样式；绝对 ATX 级别（##/###/…）由“最终文档装配器”根据块树位置计算（baseHeadingLevel + 深度）。
- 预览/诊断模式（可选）：在独立渲染时，可用“Heading:”占位呈现以提升可读性，但此为非规范选项，不影响最终文档的 ATX 决策。

#### 3.2 缩进与空行
- 子块相对父块缩进 indentSize（建议 2 个空格），多层累积。
- 异质块之间插入 1 个空行；同类连续 List 项之间不强制空行。
- Section header 与其 body 的“邻接合同”：不强制插空行，由最终装配器或渲染选项决定（可配置 sticky）。

#### 3.3 列表与表格
- 有序列表使用显式编号“1. 2. 3.”，保证跨渲染器一致性。
- 表格：在 TableBlock 上生成表头与对齐行（|---|），遵循常见 Markdown 方言；表格前是否需要空行交由策略控制。

#### 3.4 代码块
- CodeBlock 渲染为围栏代码块 ```（或按选项降级为缩进式）；language 可空。
- 与 Paragraph 的 inline `code span` 概念区分明确。

---

### 4. 渲染选项（建议默认）
- indentSize = 2
- numberStyle = dot（1. 2. 3.）
- blankBetweenHeterogeneousBlocks = true
- stickySectionHeading = true（渲染为 ATX 时，标题下方不额外插空行）
- insertBlankBeforeTable = true
- listItemTightWhenSingleParagraph = true（只有一个段落的列表项使用紧凑模式）
- sectionTightWhenSingleParagraph = true（仅一个段落的 Section 允许紧凑展示：标题与首段同行；仅作为预览选项）
- headingStyle = Deferred（规范推荐：最终装配阶段统一决定 ATX；Preview 模式可选 Colon/ATX）
- baseHeadingLevel = 2（最终装配计算绝对层级的基准，可调）

---

### 5. 典型结构示例（语义 → 块树 → Markdown）

以“带参数与返回值的方法文档”为例：
- 语义输入：
  - Summary（段落 + 列表）
  - Parameters：count → "Item count."；selector → 多段描述 + 子列表
  - Returns：单段落

- 块树（概要）：
  - Sequence
    - Paragraph("…summary…")
    - List(kind:number, items:[Sequence(Paragraph("…")), …])
    - Section("Parameters",
        Sequence(
          Section("count", Sequence(Paragraph("Item count."))),
          Section("selector", Sequence(Paragraph("…"), List(…))))
      )
    - Section("Returns", Sequence(Paragraph("…")))

- Markdown（Preview/诊断风格，非最终 ATX）：
<augment_code_snippet mode="EXCERPT">
````markdown
…summary…
1. …
2. …

Parameters:
  count
    Item count.
  selector
    …
    - …

Returns:
  …
````
</augment_code_snippet>

在“最终装配”阶段，Parameters/Returns/count/selector 将被映射为正确深度的 ATX 标题（例如 ###/####），并应用缩进/空行策略，形成正式文档。

---

### 6. 与旧实现的差异与迁移
- 取消 DefListBlock：参数/类型参数/异常等“名-值”结构用“Section of Sections”表达；每个条目是一个 SectionBlock（header=名称，body=Sequence）。
- 取消对“Heading:”冒号风格的强绑定：改为“header 语义 + 延迟 ATX 决策”。
- 列表项支持多块：旧实现中文本以外的条目内容需要特殊处理；在 V2 中自然表达为 SequenceBlock。
- CodeBlock 引入：避免用内联反引号模拟多行代码的权宜之计。

迁移建议：
- Parser：从行序列解析时，直接构造 Sequence/List/Table/Code，再按逻辑分组封装为 Section/Section-of-Sections。
- Renderer：实现两套模式（Preview/ATX）。CI 用 Preview 便于 diff；发布文档用 ATX，按 baseHeadingLevel+深度计算“##/###…”。

---

### 7. 测试与可观测性
- 结构一致性：
  - Parameters 渲染为“Section→Section…”，不存在 deflist 的 “—” 连字符。
  - 列表显式编号稳定。
  - 表格存在表头时生成对齐行；空表/无效行被过滤。
- 空行/粘连：
  - heading 与首块的邻接符合选项（stickySectionHeading）。
  - 异质块之间恰 1 空行；连续列表之间不插空行。
- DebugUtil：输出“块树 JSON/Markdown 预览”，便于定位版式问题。

---

### 8. 开放问题
- 标题与目录（ToC）集成：是否在装配阶段自动生成本地目录。
- 表格对齐语法的方言差异：是否提供方言适配层。
- 行内富文本（强调、粗体等）是否在 Mapping 阶段统一归一；Renderer 保持透明。

---
