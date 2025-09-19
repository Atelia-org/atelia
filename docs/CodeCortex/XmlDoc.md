## 综述
C# 的 XML 文档注释有一组被编译器和常见文档工具（Visual Studio、DocFX、Sandcastle 等）广泛支持的标签。它们大体分为：
- 顶层（块级）标签：描述成员的不同部分（如 summary/remarks/returns/param…）
- 文本与内联标签：用于正文中做高亮、链接、参数/类型参数引用等
- 列表与结构化内容：list/item/term/description/para/code 等
- 外部包含与继承：include、inheritdoc（多由文档工具处理）

下面按用途列清单，并给出允许/推荐的嵌套关系。

---

## 顶层（块级）标签
这些标签应直接作为某个成员注释的“顶层子节点”（不要嵌套在 summary/remarks 内）。一般每个成员最多/建议出现一次（param、typeparam、exception、seealso 允许多次）。

- summary：简要说明（建议最多一段）
- remarks：详细说明/背景信息（可包含段落、列表、示例等）
- example：示例（可含 code、list、para 等）
- returns：返回值说明（仅对有返回值的成员；属性请用 value）
- value：属性的值说明（用于属性代替 returns）
- param name="…": 方法/索引器参数说明（可出现多次）
- typeparam name="…": 泛型类型参数说明（可出现多次）
- exception cref="…": 可能抛出的异常及条件（可出现多次）
- seealso cref|href="…": 相关链接（顶层的“另请参阅”，可多次）
- include file="…" path="…": 引入外部 XML 片段（顶层）
- permission：历史遗留，通常不再使用

注意
- returns 与 value 不应同时用于同一成员；void 方法不应写 returns。
- param/typeparam 的 name 必须与签名匹配，否则会有编译器警告（如 CS1572/CS1573）。

---

## 文本与内联标签（可嵌入 summary/remarks/returns/value/example 等文本容器中）
- c：行内代码（短片段，等宽显示）
- code：多行代码块（可搭配 example/remarks 使用）
- para：段落（用于显式分段。可嵌在 summary/remarks 等）
- see cref|href|langword：内联链接/关键字高亮
  - cref 指向代码符号，href 指向外部 URL，langword 用于 C# 关键字（如 null、true）
- paramref name="…": 行内引用参数名
- typeparamref name="…": 行内引用类型参数名
- list：可在文本中嵌入结构化列表（见下节）
- <em>/<strong> 不属于 C# XMLdoc 标准，若需强调请用文字或列表结构表达；是否支持取决于文档生成器的扩展能力。

一般规则
- 以上内联标签可以出现在 summary、remarks、returns、value、example、list 的 description/term、para、code（code 内一般不再嵌其他）等“文本容器”中。
- see 与 seealso 区别：see 为行内链接，seealso 为顶层“另请参阅”。

---

## 列表与结构化内容（list）
list 是结构化内容核心，尤其适合条目、步骤、表格。

- list 元素属性：type="bullet|number|table"
  - bullet：无序列表
  - number：有序列表
  - table：表格（每个 item 的列用多个 term 表示）
- 子元素与结构
  - listheader（可选、最多一个）
    - term（table 模式下表示各列表头列；可多个）
    - description（在 bullet/number 中可用作标题说明）
  - item（可多个）
    - term（bullet/number 用作条目的标题；table 模式下每个 term 对应一列）
    - description（条目的正文/说明；可包含段落、内联标签、甚至嵌套 list）

允许的嵌套
- item/description 内可以再嵌 para、c、code、see/paramref/typeparamref，以及再嵌一个 list（嵌套列表是允许的）。
- code 内一般不再嵌其他标签（当作原样文本处理）。

示例（项目中常见写法）：
````xml mode=EXCERPT
<list type="bullet">
  <item><description>要点一</description></item>
  <item>
    <description>要点二，参见 <see cref="SomeApi"/>。</description>
  </item>
</list>
````

表格示例（列头 + 行）：
````xml mode=EXCERPT
<list type="table">
  <listheader><term>名称</term><term>说明</term></listheader>
  <item><term>Foo</term><description>第一列为名称，第二列为描述</description></item>
</list>
````

---

## 继承与外部包含
- include：将外部 XML 文件中的片段包含进来（顶层）。用于复用文档。
- inheritdoc：常见于 DocFX/Sandcastle，表示从基类/接口继承文档。
  - 说明：C# 编译器会把它原样写入 XML 输出，具体“继承合并”通常由文档工具完成；若仅依赖编译器的 XML，不会自动展开。

---

## 允许的嵌套关系（速查）
- 顶层（直接属于成员）：summary、remarks、example、returns、value、param、typeparam、exception、seealso、include、permission、inheritdoc
- 文本容器内（summary/remarks/returns/value/example/item/description/para 等）：
  - 允许：c、code、para、see、paramref、typeparamref、list
  - list 内部结构：listheader(0..1)、item(0..n)；listheader 内 term/description；item 内 term(0..n)/description(0..1)
- 数量与唯一性（建议/常见规范）：
  - summary/remarks/returns/value/example：0..1
  - param/typeparam/exception/seealso：0..n
  - listheader：每个 list 最多 1 个
  - code：在需要的文本容器内任意使用；避免在 code 中再嵌其他标签

---

## 编译器与常见告警要点
- CS1570/CS1580/CS1574：XML 结构或 cref 解析错误
- CS1572/CS1573：param/typeparam 与签名不匹配或缺失
- CS1587：注释放置位置不合法
- 一般工具对“重复 summary/returns/value”等不会直接报错，但会引发生成器行为不一致，建议保持唯一。

---

## 实用建议
- 结构化优先：简要信息放 summary，细节放 remarks，示例放 example，返回值用 returns/value。
- 链接统一：引用代码符号优先用 see/seealso + cref（而非手写字符串），关键字用 see langword。
- 列表清晰：bullet/number 用 description 承载正文；table 保持每行 term 数与表头一致。
- 复用与继承：团队文档用 include 统一片段，配合 DocFX 等工具使用 inheritdoc。
