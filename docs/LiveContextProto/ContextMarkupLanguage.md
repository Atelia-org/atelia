# Context Markup Language (CTXML)

工作在Token Index这一层，为基础非文本Token提供规范。配合文本层级的Tokenizer，提供从内存对象与Token Index序列之间的双向转换。

### Structure Tokens
通过引入一组非文本Token来实现数据与思考的隔离，并支持输入输出的结构化。采用的是为JSON创造简单变体的思路。

动机:
1. ASCII 控制字符：借鉴“控制流 vs 数据流”分离。
2. 转义序列困境：尝试解决多层嵌套时的转义序列地狱问题。
3. JSON 语法迁移：它延续 JSON 树形结构，便于模型迁移已有能力。

Structure Tokens:
- `<|{|>`,`<|}|>`: Begin Object / End Object。对象开闭分界符。
- `<|“|>`,`<|”|>`: Begin Raw String / End Raw String。原始字符串开闭分界符，内部不转义，可嵌套。
- `<|:|>`: Name Separator。键值分隔，在对象成员里把 name（字符串）和 value 分开。
- `<|[|>`,`<|]|>`: Begin Array / End Array。它们包围一个 array（数组），内部是按顺序排列的 values（值）。
- `<|,|>`:  Value Separator。元素之间的分隔符，用在对象成员之间和数组元素之间。
- `<|\|>`: Escape。转义符号，是实现元编辑的关键，使得其后紧跟的一个结构Token被作为普通数据对待。这样CTXML本身是可以在ContextUI中编辑

上下文模板也使用这组Token来定义，在Token index层面先解析成内存对象，再将Token转成文本。甚至可以在这层扩展“增量验证”“约束解码”等功能，让解析器直接指导 logits 采样（类似 constrained decoding）。

### TUI Tokens
有意让TUI Tokens与Structure Tokens处在不同层级，目的是让Structure Tokens本身也可以变选取和编辑。TUI Tokens是不可被选取的。

TUI Tokens:
- `<|I|>`: Cursor。TUI光标。
- `<|RegionMark|>`,`<|RegionBegin|>`,`<|RegionEnd|>`: 用来实现可扩展的选区、高亮功能。
  - 选区例子`<|RegionMark|>Selection<|RegionBegin|>这是一段被选中的文本，可用于后续替换或复制粘贴等操作<|RegionEnd|>`
  - 高亮例子`<|RegionMark|>Caution<|RegionBegin|>SystemTemperature: 95℃<|RegionEnd|>`的。

### 缓解工具爆炸问题
传统Tool Calling机制中，每种功能都需要定义一个Tool，包括完整的Description和Schema，并且通过SystemInstruction区段注入。而随着Agent可用工具的增多，特别是开放MCP社区的兴起，Description+Schema本身就占据了很长的Token篇幅。同时，又不是所有工具都需要长期展示和可用，需要一种基于情景的动态展示与隐藏机制。
Structure Tokens构造的节点树，可以通过ID字段提供操作锚点。TUI Tokens可以灵活和可扩展的为展示的文本附加信息。结合二者可以很容易的构造一个UI元素层，例如Button和InputBox。定义一个具有一定自明性的Button，比增加一个专用的Tool要更轻量，还能解决多实例问题。Button所在的窗口被“折叠”为Gist/Summary后，Button自然不再显示，可以释放占用的LLM注意力。而且可交互UI元素可以出现在Context的任意位置，而不必写入SystemInstruction区段，对于KVCache更友好，也更易于抵抗恶意App的注入攻击(Tool的Description和Schema是来自Agent系统外的文本，不应进入SystemInstruction区段)。

### RegionMark功能可能对LLM阅读代码很有帮助
可以提供比现代人类IDE更强的“语法着色”功能，为普通文本附加上类型信息，Warning ID等信息。
