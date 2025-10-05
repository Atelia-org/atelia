# GFM 速查手册 (Quick Reference)

本文档是 GitHub Flavored Markdown Spec 的速查版本，仅列出语法要点，省略详细示例。

完整规范参见：`docs/GitHub-Flavored-Markdown-Spec/`

---

## 一、Leaf Blocks（叶块）

叶块是不包含其他块的块级元素。

### 1. Thematic Breaks（分隔线）
- **语法**：3个及以上的 `-`、`_` 或 `*`（可选空格间隔）
- **缩进**：0-3 空格
- **示例**：`***` / `---` / `___`

### 2. ATX Headings（ATX标题）
- **语法**：1-6个 `#` + 空格 + 标题内容
- **缩进**：0-3 空格
- **关闭符**：可选的尾部 `#`（需空格分隔）
- **示例**：`# H1` / `## H2` / `### H3`

### 3. Setext Headings（Setext标题）
- **语法**：标题文本 + 下划线（`=` 为 H1，`-` 为 H2）
- **缩进**：标题文本 0-3 空格，下划线 0-3 空格
- **限制**：不能中断段落（除非有空行）
- **示例**：
  ```
  Heading 1
  =========

  Heading 2
  ---------
  ```

### 4. Indented Code Blocks（缩进代码块）
- **语法**：每行缩进 4+ 空格
- **内容**：字面文本，不解析 Markdown
- **限制**：不能中断段落
- **示例**：
  ```
      code line 1
      code line 2
  ```

### 5. Fenced Code Blocks（围栏代码块）
- **语法**：3+ 个 `` ` `` 或 `~` 包围
- **缩进**：开始围栏 0-3 空格
- **Info String**：围栏后可指定语言/属性
- **关闭**：相同类型且不少于开始围栏的字符数
- **示例**：
  ````
  ```python
  def hello():
      print("world")
  ```
  ````

### 6. HTML Blocks（HTML块）
- **类型**：7种类型（script/style/pre, 注释, 处理指令, 声明, CDATA, 块级标签, 自定义标签）
- **缩进**：0-3 空格
- **结束条件**：根据类型不同而不同
- **限制**：类型1-6 可中断段落，类型7 不可
- **示例**：
  ```html
  <div>
    content
  </div>
  ```

### 7. Link Reference Definitions（链接引用定义）
- **语法**：`[label]: destination "optional title"`
- **缩进**：0-3 空格
- **位置**：文档任意位置（生效全局）
- **限制**：不能中断段落
- **示例**：`[foo]: /url "title"`

### 8. Paragraphs（段落）
- **语法**：不能解释为其他块的非空行序列
- **缩进**：首行 0-3 空格
- **终止**：空行或其他块级元素
- **示例**：普通文本行

### 9. Blank Lines（空行）
- **作用**：分隔块级元素，决定列表松紧
- **忽略**：文档开头/结尾的空行

### 10. Tables（表格）**[GFM 扩展]**
- **结构**：表头行 + 分隔行 + 数据行
- **分隔符**：`---`（可选 `:` 指定对齐）
- **单元格**：用 `|` 分隔，支持内联元素
- **转义**：`\|` 表示管道字符
- **示例**：
  ```
  | Header 1 | Header 2 |
  | -------- | -------: |
  | Left     |    Right |
  ```

---

## 二、Container Blocks（容器块）

容器块包含其他块作为内容。

### 1. Block Quotes（块引用）
- **语法**：行首 `>` + 可选空格 + 内容
- **缩进**：`>` 可缩进 0-3 空格
- **嵌套**：支持多层嵌套
- **懒惰续行**：段落续行可省略 `>`
- **分隔**：空行分隔不同引用块
- **示例**：
  ```
  > This is a blockquote.
  > It can span multiple lines.
  >
  > > Nested blockquote
  ```

### 2. List Items（列表项）
- **无序标记**：`-`、`+` 或 `*`
- **有序标记**：1-9 位数字 + `.` 或 `)`
- **缩进规则**：
  - 标记后需 1-4 个空格
  - 续行缩进 = 标记宽度 + 后续空格数
  - 标记本身可缩进 0-3 空格
- **内容**：可包含任意块级元素
- **空项**：允许空列表项
- **懒惰续行**：段落续行可减少缩进
- **示例**：
  ```
  - Item 1
    - Nested item
  - Item 2

  1. First
  2. Second
  ```

### 3. Task List Items（任务列表项）**[GFM 扩展]**
- **语法**：列表项 + `[ ]`（未完成）或 `[x]`（已完成）
- **位置**：必须在段落开头
- **渲染**：转为复选框 `<input type="checkbox">`
- **嵌套**：支持任意嵌套
- **示例**：
  ```
  - [ ] Todo item
  - [x] Done item
  ```

### 4. Lists（列表）
- **定义**：同类型列表项的序列
- **类型**：无序列表（bullet list）或有序列表（ordered list）
- **起始编号**：由首项决定
- **松紧**：
  - **Tight（紧凑）**：无空行分隔，`<p>` 标签省略
  - **Loose（松散）**：有空行分隔或包含多块级元素，`<p>` 标签保留
- **中断段落**：仅起始编号为 1 的有序列表可中断段落
- **分隔**：不同标记符开始新列表
- **示例**：
  ```
  - Tight item 1
  - Tight item 2

  - Loose item 1

  - Loose item 2
  ```

---

## 三、块级元素分类总览

我来帮你创建一个 GFM Spec 的速查手册版本。首先让我看看完整的 GFM Spec 文档结构，然后整理出 Leaf blocks 和 Container blocks 的列表。

读取 [](file:///e%3A/repos/Atelia-org/atelia/docs/GitHub-Flavored-Markdown-Spec/04-Leaf-blocks.md#1-1)，行 1 到 2000

搜索与 `**/docs/GitHub-Flavored-Markdown-Spec/*.md` 匹配的文件，16 匹配项

读取 [](file:///e%3A/repos/Atelia-org/atelia/docs/GitHub-Flavored-Markdown-Spec/05-Container-blocks.md#1-1)，行 1 到 2000

读取 [](file:///e%3A/repos/Atelia-org/atelia/docs)

现在我已经了解了完整的 GFM Spec 结构，让我为你创建一个速查手册版本。让我先查看一下文件的剩余部分，确保完整了解所有类型：

读取 [](file:///e%3A/repos/Atelia-org/atelia/docs/GitHub-Flavored-Markdown-Spec/04-Leaf-blocks.md#2000-2000)，行 2000 到 2500

读取 [](file:///e%3A/repos/Atelia-org/atelia/docs/MemoDomProto)

读取 [](file:///e%3A/repos/Atelia-org/atelia/docs/GitHub-Flavored-Markdown-Spec/04-Leaf-blocks.md#2500-2500)，行 2500 到 2976

完美！现在我了解了完整的结构。让我创建一个简洁的速查手册：

已创建 [](file:///e%3A/repos/Atelia-org/atelia/docs/MemoDomProto/GFM-Quick-Reference.md)

太好了！我已经为你创建了 GFM 速查手册 GFM-Quick-Reference.md。

## 总结

我创建的速查手册包含以下内容：

### **Leaf Blocks（叶块）- 10种**
1. **Thematic breaks** - 分隔线（`***`, `---`, `___`）
2. **ATX headings** - ATX标题（`#` 到 `######`）
3. **Setext headings** - Setext标题（下划线形式）
4. **Indented code blocks** - 缩进代码块（4空格）
5. **Fenced code blocks** - 围栏代码块（` ``` ` 或 `~~~`）
6. **HTML blocks** - HTML块（7种子类型）
7. **Link reference definitions** - 链接引用定义
8. **Paragraphs** - 段落
9. **Blank lines** - 空行
10. **Tables** - 表格 [GFM扩展]

### **Container Blocks（容器块）- 4种**
1. **Block quotes** - 块引用（`>`）
2. **List items** - 列表项（`-`, `+`, `*`, 数字）
3. **Task list items** - 任务列表项（`[ ]`, `[x]`）[GFM扩展]
4. **Lists** - 列表（列表项的元容器）

---

## 四、关键概念速记

### 缩进规则
- **0-3 空格**：大多数块级元素的允许缩进
- **4 空格**：触发缩进代码块

### 中断段落
- **可中断**：ATX标题、围栏代码块、HTML块(类型1-6)、分隔线、块引用、列表(有限制)
- **不可中断**：Setext标题、缩进代码块、HTML块(类型7)、链接引用定义

### 优先级
- 块结构 > 内联结构
- 特定块类型有解析优先级（如分隔线 vs 列表项）

### 懒惰续行（Lazy Continuation）
- **适用**：块引用、列表项中的段落
- **规则**：段落续行可省略块标记（`>` 或缩进）

---

## 五、常见陷阱

1. **列表缩进混淆**：续行缩进取决于标记后的空格数，非列数
2. **表格列数不匹配**：表头必须与分隔行列数一致
3. **代码块类型判断**：4空格 vs 围栏的优先级
4. **Setext vs 分隔线**：`---` 在不同上下文的解析
5. **HTML块边界**：类型6 vs 类型7 的空行要求

---

## 六、参考

- **完整规范**：`docs/GitHub-Flavored-Markdown-Spec/`
- **在线规范**：https://github.github.com/gfm/
- **CommonMark**：GFM 基于 CommonMark，增加了表格、任务列表等扩展
