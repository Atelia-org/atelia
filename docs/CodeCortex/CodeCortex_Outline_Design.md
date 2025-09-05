# CodeCortex Outline 生成与渲染设计（维护笔记）

> 目的：把“现在这位我”的上下文与实现细节沉淀下来，便于后续会话快速恢复认知、延续改进。
> 范围：XmlDoc 提取 → 行序列（IR） → Markdown 渲染 → Outline 组装 的三层职责与关键约定。

---

## 1. 三层职责划分（Single Responsibility）

1) XmlDocLinesExtractor（提取层）
- 输入：Roslyn 符号（ISymbol）/ XML 文档字符串
- 输出：List<string> 行序列（Intermediate Representation, IR）
- 能力：
  - 清洗三斜线前缀（StripDocCommentPrefixes）再做 XDocument.Parse（保留空白）
  - 结构化节点递归展开：<summary> / <para> / <see> / <paramref> / <typeparamref> / <c> / <code> / <list type="bullet|number|table">
  - 列表渲染：
    - number → 显式编号“1. 2. 3.”（非 Markdown 的“全 1.”），利于 LLM 直读
    - bullet → “- ”
    - table → “| A | B |\n|---|---|\n...”
  - C# 关键词化映射：Boolean→bool, Int32→int 等（CrefToDisplay）
  - 安全性：HasStructuralPayload 判空，TrimLeading/TrailingEmpty 去除开头/结尾空行

2) MarkdownRenderer（渲染层）
- 输入：行序列 IR + indent + bulletizePlain（表格前空行已内置）
- 输出：追加到 StringBuilder 的 Markdown 文本
- 能力：
  - IsStructuralLine：识别 bullet/numbered/table 三种结构行
  - HasStructuralPayload：过滤“空负载”的结构行（仅有“|”或空白不输出）
  - RenderLinesWithStructure：
    - 结构行：原样保留缩进与前缀
    - 普通行：根据 bulletizePlain 决定是否加 “- ”
    - 表格前空行：当前行是表格、上一输出非表格 → 插入空行，提升 VS Code 等渲染器的表格识别

3) OutlineExtractor（组装层）
- 负责“装配 + 排序 + 版式”：
  - 文件/程序集/哈希头信息
  - 类型 Summary（XMLDOC）
  - Public API 清单（含属性、委托、嵌套类型）
  - 预定义分节：Params / Returns / Exceptions
    - 首行结构化（表格/列表）→ 不拼接 em dash；先输出“名称/类型”行，再渲染结构
    - 首行纯文本 → “名称/类型 — 内容” + 其余行按结构渲染

---

## 2. 关键选项（OutlineOptions）

- IncludeXmlDocFirstLine: bool = true
  - 是否输出类型 XMLDOC 段落（结构化行序列）


调用示例：
```csharp
var text = extractor.BuildOutline(type, hashes, new OutlineOptions(
    IncludeXmlDocFirstLine: true
));
```

---

## 3. 渲染与结构识别规则（要点）

- 有序列表：显式编号 1., 2., 3.（提取层就生成），嵌套层级从 1 重新开始
- 无序列表：统一 “- ”，嵌套层级每层 +2 空格缩进
- 表格：
  - 表头行：`| Key | Value |`
  - 分隔行：`|---|---|`
  - 单元格行：`| A | Alpha |`
  - 空行策略：进入表格块前插入 1 行空行（若上一输出非表格）
- 结构判断（Renderer）：
  - Bullet：`^\s*-\s+`
  - Ordered：`^\s*\d+[.)]\s+`
  - Table：`^\s*\|`（HasStructuralPayload 确保非空）
- Cref/关键词映射：
  - see/cref：尾标识 + 常见 BCL → C# 关键词
  - see/langword：true/false/null 原样

---

## 4. 预定义分节的特殊处理（Params / Returns / Exceptions）

- Params：`- name — inline` 或 `- name` + 结构化块（若首行是结构）
- Returns：同理，首行结构化则直接渲染结构块
- Exceptions：
  - `- System.ArgumentException`（首行结构化时不拼 em dash）
  - 随后渲染结构化块（表格/列表）或普通描述行

设计原则：
- 首行结构化 → 不与“名称/类型”拼接文本，避免 `Type — | A | B |` 的错误拼接
- 首行普通文本 → “名称/类型 — 文本”，其余行按结构渲染

---

## 5. 测试要点（已覆盖）

- 有序列表显式编号（1. / 2.）
- Exceptions 首行表格不拼 em dash，表格分隔线存在
- 表格前空行：Step 2 与表格之间存在确切的空行
- 嵌套编号列表：父/子层级均按 1. 起始
- 列表项 term/description 组合：
  - term+desc → "term — desc"
  - 仅 desc → "desc"
  - 仅 term → "term"

---

## 6. 常见坑与经验

- RS1034：Roslyn 语法判定尽量用 IsKind（已修复）
- 三斜线 XML：不要直接 Parse，先去掉前缀再解析；保留空白避免错误拼接
- 表格识别：某些渲染器需要表格块前有空行；本项目始终开启（遵循块级元素规范）
- 结构化判空：HasStructuralPayload 防止输出“空表格行”或“空列表”

---

## 7. 面向未来的扩展点

- Renderer 策略化：
  - 表格对齐风格（居中/左/右）、更多分隔线风格
  - 缩进宽度可配置（当前每层 +2 空格）
- Extractor 丰富：
  - <example> / <remarks> / <value> 等节点的结构化提取
  - <code> 块的样式（多行代码块 vs 行内 `code`）
- Outline 可配置版式：
  - 头部摘要字段的选择与顺序
  - Public API 分组（方法/属性/事件/嵌套类型）

---

## 8. 维护指引（TL;DR）

- 改动 XML 提取 → 优先修改 XmlDocLinesExtractor；不要在 OutlineExtractor 里“顺手解析 XML”
- 改动渲染逻辑 → 优先修改 MarkdownRenderer；OutlineExtractor 只负责“怎么组织输出段落”
- 遇到 VS Code 渲染与 LLM 直读冲突 → 遵循块级元素规范优先；必要时在 Renderer 提供策略扩展，但不破坏规范默认
- 新增规则 → 先补最小单测，再实现；小步提交，保证 100% 通过

---

（本文件由当前会话的“我”编写，记录了实现决策与上下文。后续会话若需要更多细节，可从此处快速恢复认知并继续推进。）

