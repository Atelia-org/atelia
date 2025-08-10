# 头脑风暴
我们的MemoTree也好，正在折腾的Context UI原形也好，LLM们就是用户，用户就是LLM们，通过注入上下文来让LLM看见，LLM通过工具调用并填写参数来进行对CtxtUI的操作。你说的Range是个关键问题，人类UI是靠光标、反显选区、高频反馈交互来进行基于选区的操作的。但是你们LLM存在不同，你们思考一步大概要30秒，肯定不能按我们类似的精细调节+高频反馈这套模式交互，而应该是尽量直接的让LLM输出目标选区（你现在工具里就有search_replace之类的工具吧，其要求精确复述search文本的设计就挺恶心的），并给与一定的确认反馈。光标和反显，一个提供了编辑点信息，一个提供了选区信息，这两类信息也要想办法变成适合LLM理解与操作的形式...坑好大，我暂时没有好主意。你有哪些想法？

我觉得我们可以先头脑风暴信息的显示和单个IRenderSource内的独立功能。 然后可以设想一些跨IRenderSource的交互功能，比如LLM Agent打开了一个文件挂在到了一个LiveDocs的IRenderSource中来为LLM始终呈现文件的最新内容（用假数据，不用真的打开文件），而LLM可能会有复制粘贴的需求，把LiveDocs中的一个Headline整个创建为其memotree的一个节点(假的.memotree存储)，这个复制粘贴过程中只需要用Action Token就可以制定源和目标，而无需LLM费劲的逐个复述文本内容。又或者LLM把记忆中的某个一长串api key创建为目标代码块中的一个字符串变量的初始字面量。EnvInfoSource应该支持信息的LOD级别也就是展开和折叠操作，展开到Full显示具体信息，比如当前上下文空间占用了56%，自己的账户余额还剩多少金额或Token数。而折叠到Gist级别则只显示警信息如”上下文空间剩余不足20%“。我们人类的GUI从施乐那时起折腾到现在五花八门，可能面向你们LLM的Context UI也不会很简单...应该让你们LLM在查看和操作Context UI时具有接近我们人类操作TUI的能力。你怎么想？

我认为NodeRef不应该与ViewName耦合。既然我们还没有完全想清楚设计，我们先不在正式实现层代码动手，而是通过模拟类型和内存中的假数据，先在一个单独的prototype中试验和迭代IRenderSource系列设计如何？这样代码量小且集中，而且灵活，只关注外观和交互逻辑，内部数据都是写死在类型里的字面量。这样我们可以在早期就模拟多种数据源及复合在一起进行一些典型操作时的情况。等这个原形折腾稳定了之后，我们再按照其模式修改正式代码，把目前的功能封装或调整为IRenderSource和RenderSection。你怎么想？

# 一些候选想法

## 目标与步骤
- 设计 LLM 友好的 Selection/Range 与 Cursor 表达，避免长文本复述。
- 定义“操作意图 + 预览确认”的低频交互流，替代人类 GUI 的高频反馈。
- 以稳定锚点优先，offset 为结果，不作为唯一输入依赖。
- 在原型中落小步：新增 SelectionSpec、预览返回、跨源 copy 的“按选区”版本。

## 设计原则
- 少复述：让 LLM 指定“选区位置”，不要求重复全文内容。
- 高容错：允许文本轻微漂移（上下文锚点 + 容差）。
- 可确认：每次操作先返回预览 diff，LLM 再确认执行。
- 渐进增强：优先结构/锚点，必要时再回退到 offset。

## SelectionSpec（建议的多策略选区模型）
TargetRef（定位到要操作的对象）
- sectionId（首选，来自 RenderSection.SectionId）
- 或 nodeRef（Id + Title，用于结构化内容）

方法（任选其一，按优先级回退）
- OffsetRange
  - startLine/startCol/endLine/endCol + revisionId
  - 用于系统先前返回过精确位置时的“直接命中”
- AnchorRange（推荐默认）
  - before: 左锚文本（长度 N、可截断）
  - inside: 选区内“特征片段”（短）
  - after: 右锚文本（长度 N、可截断）
  - occurrence: 第几处匹配
  - checksum/hash: 可选，稳固 disambiguation
  - 说明：系统据此在最新文本中近似匹配→解析为 offset
- PatternRange
  - literal/regex + nth occurrence（避免复述长串）
- StructuralRange
  - 针对结构化内容：node-part（Title/Body/CodeBlock#3）、Heading 级别、列表项 index 等

回退次序
- 优先 OffsetRange（若 revisionId 匹配）
- 否则 AnchorRange（容错查找）
- 再 Pattern/Structural（需 disambiguation 时返回多选项让 LLM 指 index）

CursorSpec（插入点）
- 与 SelectionSpec 同源，但只产出单点：可用 Anchor（before/after 的交界）或 Structural（如“标题后第一行”）

## Action 模式（面向 LLM 的“单次大意图”）
- edit.replace(selection, newText)
- edit.insert_before(selection, newText) / insert_after
- edit.delete(selection)
- edit.wrap(selection, prefix, suffix)
- copy(selection, to = sinkId/path, mode=as-node/append/replace)
- rename/move（结构操作）
说明：
- 复制操作避免复述正文：源用 selection，目标只给“写入位置/容器”；系统读取源真实文本，构造结果。

## 预览确认（低频但确定）
- 请求：Action + SelectionSpec（不含大段文本，除非 newText）
- 返回：Preview
  - resolvedSelection（最终 offset + 截断的上下文片段）
  - unified diff 或 side-by-side snippet
  - diagnostics（多匹配/未匹配/歧义提示）
  - newRevisionId
- LLM：确认/修改 selection 参数/放弃
- 系统：应用 → 返回最终状态（含新的 RenderSection + revisionId）

## 稳定锚点与“句柄”
- 每个 RenderSection 提供 stable SectionId（source/type + key）
- 对选区可返回 SpanHandle（内部映射到 offset + hash），便于后续引用
- 对行块可加行级哈希或 rolling hash，提高重定位鲁棒性

## 误差与歧义处理
- 多匹配：返回候选（位置摘要 + 行号/上下文），要求 LLM 选 index
- 未匹配：提供“附近最相似位置”建议 + 容差等级
- 大文档：分块 + 在块内解析锚点；返回块级摘要辅助 disambiguation

## 与你的案例映射
- LiveDocs → FakeMemoTree：copy(selection=AnchorRange: inside=“某 headline 标题或短句”，to=memotree/inbox, mode=as-node)
- EnvInfo LOD：结构化 range（node-part=“Summary”/“Details”），Action=expand/collapse → 源内部切换渲染分支
- “把一长串 api key 写进目标代码块变量初值”：selection=AnchorRange 定位变量初始化语句或代码块，再 edit.replace 或 insert_after

## 在原型的最小落地（非破坏式）
- Contracts 扩展
  - 新增 SelectionSpec（OffsetRange | AnchorRange | PatternRange | StructuralRange）
  - 扩 ActionToken payload：可带 selection 与参数（仍保留简单 URI 样式）
- DemoActionDispatcher
  - 支持 copy(selection, to) 与 preview 模式（先 compose 预览，再确认 apply）
- LiveDocsSource
  - 给每个 headline 附一个 copy Token（带 AnchorRange: inside=title）
- FakeMemoTreeSinkSource
  - 接受 copy 结果，生成节点（Title/Body）
- EnvInfoSource
  - 保持 IExpandableRenderSource（结构 LOD 示例）

这样，LLM 无需复述正文，就能用 Token + SelectionSpec 表达“在哪、做什么”，系统负责从锚点解析到精确 offset，并以 preview/diff 反馈，贴近 LLM 的工作方式与节奏。

若你同意，我可以在原型里加上 SelectionSpec 与预览返回的最小实现，并给 Program 增加一次“带 AnchorRange 的 copy + 预览确认”的演示。