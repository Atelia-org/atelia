# MemoTree 设计摘要

> 用途：总结 MemoTree 当前讨论得到的主线设计与实现要点，作为 `prototypes/MemoTree` 的起步文档。
>
> 当前阶段：接口先行草案。本文只锁定高性价比主线，不展开底层实现细节。
>
> 面向 LLM 的 Window 文本布局草稿见：`docs/MemoTree/llm-window-ui-draft.txt`
>
> 结构方案取舍草稿见：`docs/MemoTree/structure-options-tradeoff.md`
>
> 概念与术语总表见：`docs/MemoTree/concepts-and-terminology.md`

## 0. 一句话定位

MemoTree 是面向 LLM Agent 的长期外置记忆图：

- 本体上，它是 durable memo graph，而不是 Markdown 字符串。
- 当前 v0 上，它采用统一节点 `Unified Node` 模型：每个节点都可以同时拥有正文与子节点。
- 当前 v0 上，它只先公开一种核心关系：`contains`。所有节点从根出发可达时，会形成一棵易于理解与实现的包含树。
- 渲染上，它当前 v0 SHOULD 采用 `index+flatten` 布局：同一个 Window 中先给结构 index，再平铺若干展开节点卡片，而不是输出一棵大型嵌套树。

## 1. 为什么它目前是高性价比路线

### decision [S-MEMOTREE-HIGH-ROI-MEMORY-BASELINE] MemoTree 是当前长期外置记忆的主线候选

在当前 Agent.Core / StateJournal 现状下，MemoTree SHOULD 作为长期外置记忆的主线方向。

理由：

- 相比“把全部 RecentHistory 直接持久化”，MemoTree 不需要先解决 `HistoryEntry` 多态落盘与完整 replay 语义。
- 相比向量库检索，MemoTree 更可检查、可手工编辑、可预测，不依赖黑箱召回。
- 相比知识图谱，MemoTree 对 schema 的要求低得多，更适合早期快速迭代。
- 相比单个大文本文件，MemoTree 允许稳定节点 ID、局部摘要、局部增量编辑与预算内投影。

MemoTree 的核心取舍是：把 durable memo graph 作为权威真相，节点本体采用 `Unified Node + Contains Tree + Optional Tags`，把 Markdown 退化为节点内部正文的一种低噪音承载格式，再用单独的 `index+flatten` Window 渲染层负责结构骨架与展开卡片，同时补上稳定引用、分层展开与增量持久化能力。

## 2. 核心设计决策

### spec [S-MEMOTREE-MEMO-GRAPH-AS-CANONICAL-SOURCE] durable memo graph 而非单个 Markdown 字符串

MemoTree 的权威真相 MUST 是 durable memo graph，而不是单个拼接后的 Markdown 字符串。

当前 v0 中，public surface SHOULD 只先暴露 `contains` 关系，因此业务上看起来仍是一棵 rooted tree；但这棵树只是 graph 的首个、也是当前唯一需要落地的关系投影。

节点正文中的 Markdown 只是一种派生视图。这样做的原因是：

- 标题改名时不破坏引用。
- 节点可独立维护 `summary` 与 `gist`。
- 渲染器可按节点与子树粒度做折叠和预算分配。
- 存储层可按节点与正文块增量提交。

### spec [S-MEMOTREE-UNIFIED-NODE] 节点本体采用 Unified Node 而不是 directory/file 二分

MemoTree 的节点本体 SHOULD 采用统一节点模型：每个节点都 MAY 同时拥有：

- `title`
- `body`
- `gist`
- `summary`
- `children`

这意味着：

- “像目录”的节点只是“当前没有正文、但有子节点”的一种使用形态
- “像文件”的节点只是“当前有正文、但没有子节点”的一种使用形态
- “像章节”的节点则可以同时拥有正文与子节点

当前 v0 不应把节点本体先拆成 `Directory` / `File` 两类，也不应要求调用方在“容器”和“正文节点”之间做硬性二选一。

这样做的原因是：

- 长期记忆里，很多自然节点同时需要总述正文和子结构。
- 如果先走 FS 二分，后续很容易发明 `README` / `_index` / `overview` 一类补丁结构把统一节点偷偷绕回来。
- Unified Node 更贴近“章节型知识单元”的真实形态，也更适合 Agent 渐进维护。

### spec [S-MEMOTREE-WINDOW-AS-INDEX-FLATTEN] Window 应表现为 index 加 node card flatten list

MemoTree 的上下文投影 SHOULD 采用 `index+flatten` 布局，而不是一整篇扁平拼接后的 Markdown 文档，也不是一棵层层嵌套并直接展开正文的大树。

推荐心智模型：

- 同一个 Window 内先给一个稳定的树形 index，帮助 Agent 快速定位“记忆库里有哪些区块”
- 然后再给若干彼此不嵌套的 node card，它们组成一个 flatten list；每张卡片承载标题、breadcrumb、`Gist`、`Summary`、状态与必要的正文片段
- 只有当前相关节点才进入 flatten list 并提升到更高 LOD

这样做的原因是：

- 长期任务里，Agent 更需要“先定位再深入”，而不是被迫重读一篇会不断改写的大文档。
- `index` 与 node card flatten list 分离后，实现上更接近“导航栏 + document tabs”的经典界面分工。
- 结构骨架与节点正文解耦后，节点内 Markdown 不再承担整棵树的结构表达。
- 后续即使把整体 Window 渲染换成更像 HTML/outline 的文本投影，也不需要回头修改 memo graph 本体。

### spec [S-MEMOTREE-TREE-STRUCTURE-AS-SINGLE-TRUTH] 树结构是真相，heading level 只是渲染派生物

当前 v0 中，`contains` 树结构 MUST 是唯一真相。任何 Markdown/HTML 风格的 heading level 都 MUST 由节点深度在渲染时推导，而不应作为节点持久化字段或编辑 API 的独立输入。

这样做的原因是：

- `MoveSubtree(...)` 只需要维护 parent/children 关系，不必再同步第二套 heading level 真相。
- 子树移动后的可见层级会自然随深度变化，而不是留下过期层级元数据。
- 渲染格式以后即使从 Markdown heading 切到别的结构化容器，也不用先洗一遍历史数据模型。

### spec [S-MEMOTREE-OPTIONAL-TAGS-AS-SECONDARY-INDEX] tags 只作为次索引，而不是主结构

MemoTree MAY 支持轻量 `tags`，但 tags SHOULD 只是节点上的可选次索引，而不是替代 `contains` 树成为主导航结构。

原因：

- 对长期任务来说，稳定结构骨架比自由标签云更重要。
- tags 很适合表达横切视角、筛选与聚合，但不适合承担默认导航。
- 把 tags 放在次索引层，可以保留灵活性，又避免过早走向“单层文档 + 查询系统”的复杂路线。

### spec [S-MEMOTREE-STABLE-NODE-ID] Memo 节点必须有稳定 nodeId

每个 memo 节点 MUST 有稳定 `nodeId`，业务层 MUST NOT 把标题文本、标题路径或序号当作主键。

标题会变，结构会调整，只有稳定 `nodeId` 才能支撑：

- 后续编辑
- 渲染缓存
- pin
- 搜索结果回指
- agent 在多轮中的可靠引用

### spec [S-MEMOTREE-BODY-BLOCK-ADDRESSING] 节点正文应支持 block 级寻址

节点正文 SHOULD 采用 `DurableText` 一类的 block 级文本容器承载，并暴露稳定 `blockId`。

原因：

- 这比整段替换更适合 Agent 增量编辑。
- 这与 StateJournal 现有 `DurableText` 的稳定 block ID 语义天然契合。
- 后续若需要“只改某一段”或“引用正文中的第 N 块”时，无需靠内容复述定位。

默认编辑工作流 SHOULD 围绕 block 级操作展开，而不是围绕整段重写展开。

### spec [S-MEMOTREE-THREE-LEVEL-LOD] 每个节点支持三层内容 LOD

每个节点 MUST 支持以下三种可渲染层级：

1. `Gist`：标题 + 一句话印象
2. `Summary`：标题 + 印象 + 节点正文摘要
3. `Full`：标题 + 印象 + 节点正文摘要 + 节点正文全文

其中：

- `Gist` 负责保留“知道这里有什么”的最小存在感。
- `Summary` 负责在预算紧张时保留本节点正文大意。
- `Full` 只给真正相关、真正需要细节的节点。

补充约束：

- `Summary` MUST 只概括本节点自己的内容，不应把子节点内容混进来。
- 当一个节点已被纳入可见 Window 时，结构骨架 SHOULD 尽量保留；预算压力优先压缩正文 LOD，而不是先抹掉子节点列表。
- 默认没有显式 hint 的节点，SHOULD 优先以 `Gist` 级别保留结构存在感。

这意味着：MemoTree 的 LOD 主要压缩“内容”，而不是压缩“结构”。结构骨架比正文细节更应优先留在上下文里。

### spec [S-MEMOTREE-SUMMARY-STALE-TRACKED] 摘要过期状态必须显式可见

节点摘要 MUST 记录它基于哪个本地正文版本生成，并允许判断当前摘要是否过期。

如果没有 stale 标记，Agent 会把“旧摘要”误当作“当前事实”。这比完全不看摘要更危险。

`gist` 与 `summary` 都应视为节点正文的派生记忆，而不是结构层元数据。

这里的“版本”应收窄理解为节点自身正文的版本，而不是“正文/结构/pin 状态等所有变化”的总版本。移动子树、调整 pinned 顺序等操作不应自动让该节点 `summary` 过期。

### spec [S-MEMOTREE-EXPLICIT-MEMORY-MAINTENANCE] Summary/Gist 维护应是显式记忆动作

MemoTree MUST 把 `Summary` / `Gist` 的更新视为显式记忆维护动作，而不是 renderer 在某次预算收缩时静默产生的隐式副作用。

推荐的 v0 主线路径是：

- Agent 先看过某节点 `Full` 内容
- 当它决定把该节点从 `Full` 收起到非 `Full` 时，显式调用一次 `Collapse`/`Remember` 类工具
- 工具调用同时提交新的 `Gist` 与 `Summary`
- 提交时显式带上 `basedOnBodyVersion`

这样做的原因是：

- 记忆沉淀动作可审计。
- 工具语义稳定，不依赖 renderer 当次预算偶然变化。
- “我为什么忘掉细节、留下了什么印象”能保留在操作层而不是藏在实现细节里。

Micro-Wizard 可以作为后续增强：让同一套记忆维护语义在旁路时间线中更无感地完成；但 v0 不应把它作为唯一依赖。

### spec [S-MEMOTREE-WHOLE-BODY-REWRITE-IS-DANGEROUS] 整段正文重写应降级为危险入口

MemoTree MAY 保留整段正文重写入口，但它 MUST 被视为危险度较高的辅助操作，而不是长期记忆维护的默认主线。

原因：

- 整段重写很容易把“局部修补”偷换成“全量覆盖”。
- 实现可能选择重建正文 block，从而使旧 `blockId` 引用失效。
- 对长期任务来说，这会破坏可审计性，也会鼓励 Agent 用“重写一版我此刻更顺手的理解”覆盖旧积累。

因此，整段重写更适合：

- 初始化新节点正文
- 从外部文本导入
- 测试夹具快速造样本
- 人工明确接受“旧 block 引用可能失效”的场景

### spec [S-MEMOTREE-PINNED-OVERLAY] MemoTree 需要 pinned 叠层

纯树结构之外，MemoTree SHOULD 有一个很薄的 pinned 叠层，用来承载极少量高价值长期信息，例如：

- 当前长期目标
- 核心身份锚点
- 不可违背的约束
- 未闭环的重要承诺
- 高频引用的索引节点

Window 渲染时 SHOULD 优先保留这部分内容，再决定哪些树节点需要展开。

Pinned 的权威真相 SHOULD 只有一份有序集合。节点上的 `IsPinned` 可以作为读取快照时的派生便利字段，但存储层不应同时维护第二份布尔真相。

在 Window 中，`pinned` / `active` 当前更适合视为 node card flatten list 的排序策略，而不是额外独立的布局分区。

若未来需要 `hotset`，它更适合作为动态排序策略，而不是与 `pinned` 并列的第二份权威状态。

### spec [S-MEMOTREE-WINDOW-IS-DYNAMIC] Window 只在调用时动态生成

MemoTree 的 Window 投影 MUST 只在 LLM 调用时动态渲染，不应把某次完整 Window 结果作为长期存储真相。

长期存储保存的是 memo graph 节点、`contains` 关系与节点元数据；Window 是预算、热点、上下文相关性的派生物。

Window 的派生职责包括：

- 决定 `index` 保留到什么粒度
- 决定哪些节点进入 node card flatten list，以及它们按什么顺序平铺
- 决定哪些 node card 升到 `Summary` 或 `Full`
- 决定节点正文内的 Markdown 如何以低噪音文本形式投影到上下文

当前 MVP 中，`index` 与 node card flatten list 仍渲染到同一个 `IApp` Window，但实现 SHOULD 把“渲染 index”与“渲染单个 node card”拆成相对独立的两段逻辑。

## 3. 与 Agent.Core 集成时的关键约束

### spec [S-MEMOTREE-APP-WINDOW-INTEGRATION] MemoTree 通过 IApp Window 注入上下文

MemoTree 当前主线 SHOULD 作为 `IApp` 集成，最终以 `AppProjection.Window` 的形式注入模型上下文。

这条路径与 `Agent.Core` 的现有设计一致：App 不主动 push `Observation`，而是在请求构造阶段输出一段动态 Window 文本。

### spec [S-MEMOTREE-RENDER-BUDGET-MUST-BE-CONSERVATIVE] 渲染预算必须保守

MemoTree 渲染器 MUST 使用保守预算。

当前 `AgentEngine.EstimateCurrentContextTokens()` 明确不包含 App Windows 注入，因此 MemoTree 自己不能假设“引擎已经替我算过这部分预算”。

这意味着：

- View 侧的自动折叠阈值 SHOULD 以“可见内容字符预算”为直接约束。
- 实际渲染策略 SHOULD 预留安全余量，避免 Window 在最后一步把请求静默顶爆。

### spec [S-MEMOTREE-MINIMUM-TOOLS] MemoTree 工具集至少覆盖读搜改钉四类动作

MemoTree 对 Agent 的最小有用工具集 SHOULD 至少包括：

- `read`：按 `nodeId` 取节点、路径、正文块
- `search`：按标题/摘要/正文搜索
- `edit`：改标题、gist、摘要、正文、树结构
- `pin`：显式提升或降低长期重要性

若已启用 tags，最小工具集可以进一步扩展为轻量的 `tag` 操作，但 tags 不应成为主工作流的前置依赖。

只有自动 Window 而没有这组工具，Agent 仍然无法有效把长期记忆变成可操作对象。

## 4. 建议的持久化模型

MemoTree 当前适合基于 `DurableDict<string>` + `DurableText` 建模。

一个建议性的根对象结构如下：

| 键 | 建议类型 | 含义 |
|---|---|---|
| `meta` | `DurableDict<string>` | graph 级元数据，如 schemaVersion、title、lastRenderPolicy |
| `nodes` | `DurableDict<string>` | `nodeId -> node record` |
| `rootOrder` | `DurableDeque<string>` | v0 根节点顺序 |
| `pinned` | `DurableDeque<string>` 或 `DurableOrderedDict<long, string>` | pinned 顺序，也是 pinned 的唯一真相 |

单个 node record 可用 mixed dict 建模，字段建议包括：

| 字段 | 建议类型 | 含义 |
|---|---|---|
| `id` | `string` | 稳定 nodeId |
| `parentId` | `string?` | 父节点 ID；顶层节点为 null |
| `title` | `string` | 标题 |
| `gist` | `string?` | `Gist` 级别的一句话印象 |
| `summary` | `string?` | 节点自身正文摘要，不含子节点内容 |
| `tags` | `DurableDeque<string>` 或 `DurableHashSet<string>` | 可选次索引标签 |
| `body` | `DurableText` | 正文块序列 |
| `children` | `DurableDeque<string>` | v0 `contains` 子节点顺序 |
| `bodyVersion` | `long` | 节点自身正文版本 |
| `summaryBodyVersion` | `long` | 摘要生成时基于的正文版本 |

## 5.1 一个刻意保留的未决点

`RewriteBodyText(...)` 与稳定 `blockId` 的关系目前仍是未决设计问题。

当前草案暂时保留它，主要为了：

- 测试样例快速铺正文
- 在 block 级编辑方案未定稿前保留最朴素的整段写入口

当前建议已把它降级为 `RewriteBodyText(...)` 一类的危险入口。后续很可能还要继续把“节点整体结构渲染”和“节点内文本承载格式”进一步解耦，让正文 Markdown 只负责节点内内容，而不是承担整棵树的整体结构表达。

## 6. 渲染策略

### spec [S-MEMOTREE-BUDGET-FIRST-RENDERING] 渲染器先满足预算，再追求完整性

MemoTree 渲染器 MUST 把“预算内提供高信号信息”放在“尽量多展开”之前。

建议策略：

1. 先生成紧凑的 `index`
2. 再挑选进入 node card flatten list 的节点，并按 pinned / active / preferred 等策略排序
3. 对进入 node card flatten list 的节点提升 LOD
4. 预算不足时按节点粒度降级正文：`Full -> Summary -> Gist`
5. 仍不足时先减少低优先级 node card，再进一步收缩 index 细节

这是一种“像缓存驱逐一样”的折叠过程：不是问“全部放不放得下”，而是不断驱逐低价值可见内容，直到预算满足。

## 7. 当前不应该过度承诺的点

- MemoTree 当前价值主要来自结构化长期记忆与预算内投影，不来自 lazy loading。`StateJournal.Open` 目前仍是全量加载。
- 不必一开始就做语义搜索、embedding、知识图谱。
- 不必一开始就把所有 Agent 历史统一并入 MemoTree。
- 不必一开始就暴露复杂 merge/rebase 语义给上层调用者。
- 不必现在就锁死整体 Window 一定使用 Markdown heading；只要结构骨架对 LLM 低噪音、可预测即可。
- 不必现在就把 tags 做成复杂查询语言；先把它当轻量次索引就够了。

## 8. 建议的实现阶段

1. 先做纯树模型与 public API 契约
2. 再做可测试的 render planner
3. 再补 `IApp` 适配、`index+flatten` Window 投影、`Collapse` 记忆维护工具与最小工具集
4. 再接上 StateJournal 持久化
5. 最后做摘要刷新、优先级策略与更聪明的相关性排序
