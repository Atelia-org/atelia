# Memo: Conversation History 抽象重构蓝图 · V2
*版本：0.2-draft · 更新日期：2025-10-16*

> 本文档专注于“Conversation History”重构的目标架构、已交付设计与待定设计。实施阶段安排请参阅《Memo: Conversation History 抽象重构实施路线图》V2。

---

## 目录
- [Memo: Conversation History 抽象重构蓝图 · V2](#memo-conversation-history-抽象重构蓝图--v2)
  - [目录](#目录)
  - [文档目的与阅读指南](#文档目的与阅读指南)
  - [设计状态总览](#设计状态总览)
  - [问题背景与动机](#问题背景与动机)
    - [背景概述](#背景概述)
    - [核心痛点](#核心痛点)
    - [重构目标](#重构目标)
    - [非目标与约束](#非目标与约束)
  - [目标架构（Target Design）](#目标架构target-design)
    - [核心原则](#核心原则)
      - [设计不变量](#设计不变量)
    - [系统构成摘要](#系统构成摘要)
    - [系统边界与外部依赖](#系统边界与外部依赖)
    - [能力覆盖范围与当前进度](#能力覆盖范围与当前进度)
    - [与 V1 蓝图的差异对照](#与-v1-蓝图的差异对照)
    - [数据与交互流](#数据与交互流)
    - [History ↔ Context ↔ Provider 协作](#history--context--provider-协作)
      - [LiveScreen 处理约定](#livescreen-处理约定)
    - [ContextMessage 层接口设计（✅）](#contextmessage-层接口设计)
    - [Provider 客户端设计要点（🛠️）](#provider-客户端设计要点️)
      - [通用契约（✅）](#通用契约)
      - [OpenAI 客户端要点（🛠️）](#openai-客户端要点️)
      - [Anthropic 客户端要点（🛠️）](#anthropic-客户端要点️)
      - [其他供应商（⏳）](#其他供应商)
      - [ModelOutputDelta 管线（🛠️）](#modeloutputdelta-管线️)
    - [组件责任边界](#组件责任边界)
    - [设计模式映射](#设计模式映射)
      - [MVVM 映射（✅）](#mvvm-映射)
      - [Event Sourcing 思维（🛠️）](#event-sourcing-思维️)
      - [CQRS 与 Repository（🛠️）](#cqrs-与-repository️)
      - [Strategy + Adapter（🛠️）](#strategy--adapter️)
      - [Decorator（历史回顾）](#decorator历史回顾)
    - [AgentState 与 Widget 职责（🛠️）](#agentstate-与-widget-职责️)
      - [Widget 约定（🛠️）](#widget-约定️)
      - [顺序与稳定标识策略（⏳）](#顺序与稳定标识策略)
      - [AgentState 目标 API 轮廓（🛠️）](#agentstate-目标-api-轮廓️)
        - [示例：AgentState 骨架草图（🛠️）](#示例agentstate-骨架草图️)
  - [已完成设计（Delivered Scope）](#已完成设计delivered-scope)
    - [落地组件一览](#落地组件一览)
    - [接口契约](#接口契约)
      - [AgentState 与历史写入](#agentstate-与历史写入)
      - [ContextMessage 与 Provider 消费](#contextmessage-与-provider-消费)
    - [关键类型定义](#关键类型定义)
    - [设计验收准则](#设计验收准则)
    - [实现约束与注意事项](#实现约束与注意事项)
  - [待定设计（Deferred \& TBD）](#待定设计deferred--tbd)
    - [延后引入项](#延后引入项)
    - [待进一步调研的问题](#待进一步调研的问题)
  - [与实施路线图的关系](#与实施路线图的关系)
  - [术语与参考资料](#术语与参考资料)
  - [重构进度追踪](#重构进度追踪)
    - [本轮更新](#本轮更新)
    - [既往里程碑](#既往里程碑)
    - [下一步计划](#下一步计划)

---

## 文档目的与阅读指南
- **定位**：定义 Conversation History 模块的长期架构与设计边界，作为实现团队的“设计真相源”，不覆盖具体实施节奏。
- **读者角色**：Agent Orchestrator 架构师、Provider 客户端作者、工具/记忆子系统维护者以及需要理解历史模型的协作者。
- **更新策略**：蓝图版本号与日期在每轮设计决议确认后更新；路线图发生阶段调整时，应在 24 小时内回链至蓝图并同步状态表。
- **编辑约定**：
  - 设计状态统一使用 ✅（已定稿）、🛠️（近期交付）、⏳（延期评估）。
  - 新增章节需更新目录与“设计状态总览”表。
  - 保持术语与 `Atelia_Naming_Convention.md` 一致；引用外部文档时使用相对路径。
- **关联文档**：
  - 《Conversation History 抽象重构实施路线图》V2（进行中）
  - 《AgentState API 草案》（筹备中，预计并入蓝图附录）
  - 《Atelia.Diagnostics.DebugUtil 用法说明》（`AGENTS.md`）

## 设计状态总览
> 使用 ✅（已完成）、🛠️（开发中/近期计划）、⏳（延后评估）标记设计状态。

| 设计主题 | 状态 | 简述 | 详细章节 |
| --- | --- | --- | --- |
| AgentState 历史层抽象 | ✅ | HistoryEntry 分层、追加式 AgentState 及其单向数据流已定稿，语义化追加 API 已全面实现并通过测试。 | [目标架构（Target Design）](#目标架构target-design) |
| ContextMessage 接口束 | ✅ | 基础接口、角色化派生与 mix-in 能力已定稿，并与 HistoryEntry 抽象保持一致。 | [ContextMessage 层接口设计（✅）](#contextmessage-层接口设计) |
| Context 渲染/投影 | ✅ | RenderLiveContext 流程与 LOD 裁剪策略已实现并验证，LiveScreen 以 `"[LiveScreen]"` Section 在写入阶段落盘，支持 Memory Notebook 动态注入。 | [数据与交互流](#数据与交互流) |
| Provider 抽象与路由 | ✅ | IProviderClient、ModelOutputDelta 管线与 ProviderRouter 已落地，Anthropic Provider 通过代理验证流式调用闭环。 | [目标架构（Target Design）](#目标架构target-design) |
| AgentState 语义化追加 API | ✅ | `AppendModelInput/Output/ToolResults` 等领域方法已实现，替代临时 `AppendHistory`，并集成时间戳注入与 DebugUtil 打点。 | [AgentState 与 Widget 职责](#agentstate-与-widget-职责️) |
| Widget 体系与附加能力 | 🛠️ | MemoryNotebookWidget 与 Widget 工具清单已投入使用，LiveScreen Section 注入已完成；附件体系、Notebook 事件化、动态工具 manifest 等延后至阶段 4+。 | [待定设计（Deferred & TBD）](#待定设计deferred--tbd) |
| 诊断与元数据策略 | 🛠️ | DebugUtil 集成与 TokenUsage 初步支持已完成，深度遥测方案（计费、缓存命中）尚未定稿。 | [待定设计（Deferred & TBD）](#待定设计deferred--tbd) |

状态标签释义：
- ✅ 表示设计与约束已冻结，可直接作为实现基准。
- 🛠️ 表示短期内要交付的主题，仍允许在实现过程中微调细节。
- ⏳ 表示暂缓或依赖外部决策，保留在蓝图中以免丢失上下文。

## 问题背景与动机
### 背景概述
MemoFileProto 在早期直接沿用 OpenAI Chat Completion 的消息结构作为内部对话历史。在只接入单一模型时实现简单，但一旦会话需要混合 Claude、OpenAI 或本地模型，历史记录与供应商协议的强耦合立即暴露出扩展性瓶颈。

### 核心痛点
| 痛点 | 现象 | 后果 |
| --- | --- | --- |
| 批量工具调用破坏消息交错 | Claude 期望一次 Tool 调用→一次 Tool 响应，而我们返回多条 `tool` 消息 | Claude 端解析失败或导致上下文错乱 |
| 历史消息缺乏结构 | 所有消息都仅有 `role + content` | 难以附加 `ToolCallId`、执行状态、环境信息等元数据 |
| 历史与请求耦合 | `_conversationHistory` 已等同于 OpenAI 消息格式 | 新增供应商时需改遍全链路 |
| 缺少统一仓储 | 调用栈、调试、记忆注入散落在多个方法 | 难以追踪并复用 |

### 重构目标
1. 引入强类型 `HistoryEntry` 分层，让不同事件拥有清晰数据模型，并由追加式 AgentState 统一持有。
2. 解耦历史存储与供应商调用，Provider 客户端只消费 AgentState 渲染出的上下文视图。
3. 支撑单会话混合多种模型/接口（如 Claude 规划 + OpenAI 执行），同时保持一致的历史视图。
4. 为 [LiveContext]、工具遥测、记忆系统等能力打好基础，并维持原型阶段的实现简洁度。

### 非目标与约束
- **非目标**：
  - 不在当前重构中实现持久化仓储、历史回放或 StableId —— 按照延后计划，相关功能将在事件存储方案敲定后另行设计。
  - 不提供面向终端用户的时间线 UI 或可视化工具，本蓝图只描述领域模型和编程接口。
  - 不尝试统一外部工具/Planner 的语义模型，工具声明与执行策略仍由上层 orchestrator 决策。
  - 不为每家 Provider 定制 prompt engineering 的最佳实践，蓝图仅定义最小公约数接口。
- **约束**：
  - 假定 AgentState 仍运行在单线程 orchestrator 内部，历史追加无需内部锁；多线程访问需由调用方负责同步。
  -  Diagnostic 输出统一依赖 `DebugUtil`，以便在不同阶段通过环境变量控制打印类别。
  -  所有 Metadata 键值需遵循轻量化约束（<2 KB），大型数据必须转为附件或专门条目，避免破坏持久化兼容性。
  -  继续沿用原型环境的依赖条件：不引入外部数据库，不要求跨会话迁移，保持最小部署半径。
## 目标架构（Target Design）
### 核心原则
- **供应商无关性**：所有领域模型围绕 `HistoryEntry` 与 `IContextMessage` 定义，不暴露任何厂商特定字段，便于在同一会话内切换或并行多个 Provider。
- **单一事实源**：AgentState 以追加式 HistoryEntry 保存全部历史，Widget 作为受控的运行时组件封装状态、工具与呈现，状态可由 AgentState 重建。
- **分层解耦**：History ↔ Context ↔ Provider 之间只通过接口交互，禁止跨层依赖具体实现，确保多供应商并存。
- **渐进扩展**：默认提供最小可行模型（文本 + 工具调用），附加能力（LiveScreen、附件、TokenUsage）通过可选接口扩展。
- **回放友好**：所有时间戳由 AgentState 注入，后续若接入持久化或事件回放，无需修改 Provider 层协议。

#### 设计不变量
- **History 不可逆**：写入路径统一通过 AgentState 的语义化 API，禁止直接修改 `_history` 集合；任何热修或调试脚本都必须通过追加新条目完成。
- **上下文纯读**：`RenderLiveContext()` 必须保持纯函数特性，不得缓存跨调用状态或修改历史条目，便于在诊断和回放中复现相同结果。
- **Provider 只读消费**：`IProviderClient` 视图以不可变 `IContextMessage` 集合传入，客户端不得更改消息内容或 Metadata，新增信息需通过回写新条目表达。
- **LiveScreen Section 化**：LiveScreen 在写入阶段以 `LevelOfDetailSections.Live` 中的 `"[LiveScreen]"` Section 存档，消费方应使用 `WithoutLiveScreen` 等扩展读取正文与附加片段，避免额外的装饰器协议。
### 系统构成摘要
Conversation History 的目标架构延续 v1 文档的“三段式流水线”，但将每一层的契约重新梳理为稳定的接口集合：
- **AgentState**：负责历史持久与 Widget 宿主管理，暴露追加式 API（`AppendModelInput`、`AppendModelOutput` 等）以及上下文渲染入口。所有可追溯事件都以 `HistoryEntry` 形式落盘；系统指令由宿主字段维护，而 Notebook 等运行态视图则通过 Widget（如 `MemoryNotebookWidget`）封装并可在必要时重放重建。
- **Context Projection**：`RenderLiveContext()` 读取 AgentState，并按调用需求生成 `IContextMessage` 序列，会根据条目在会话中的相对位置选择合适的 `LevelOfDetail`，LiveScreen 已在写入阶段嵌入 Section，无需额外装饰。该层对外表现为纯函数，不产生副作用。
- **Provider Router**：根据策略选择具体 `IProviderClient` 实现，并协调 Orchestrator 在 delta 汇总后回写标准化的 `ModelOutputEntry` / `ToolResultsEntry`。路由逻辑可以依据模型家族、任务阶段或策略标签扩展。
- **Provider Clients**：对接 OpenAI、Anthropic 等底层 SDK，将通用上下文转换为厂商协议，并解析增量流。所有解析出的工具调用以统一的 `ToolCallRequest` 表达。
- **Orchestrator Feedback Loop**：LlmAgent 或更高层 orchestrator 聚合 Provider 回传结果，调用 AgentState 追加新的历史条目，形成稳定的“读取 → 推理 → 回写”循环。

### 系统边界与外部依赖
- **上游协作者**：Conversation History 由 Orchestrator（当前为 `MemoFileProto.Agent.LlmAgent`）驱动；Planner、任务分解器与工具执行器只通过 AgentState 的语义化方法交互，不得直接访问 `_history` 集合。
- **下游依赖**：所有模型调用都经由 `ProviderRouter` 转发至各 `IProviderClient` 实现；Provider 负责协议适配与工具调用解析，但无权回写 AgentState。若未来引入缓存/计费系统，应通过 ProviderRouter 的增量事件捕获接口获取信息。
- **横向组件**：记忆系统、[LiveContext] 以及未来的对话可视化工具需消费 `IContextMessage` 列表或 `HistoryEntry` 只读视图，不再依赖旧版 `_conversationHistory`。任何实时监控应通过 DebugUtil 或 Metadata 订阅。
- **外部约束**：当前阶段依赖 .NET 运行时、OpenAI/Anthropic SDK 与 Atelia 的内部工具库（DebugUtil、命名规范约束）；未引入数据库或 KV 存储。若部署环境缺乏稳定磁盘，LiveScreen 与日志默认落在 `.codecortex/ldebug-logs`。
- **兼容性策略**：蓝图默认与 Phase 1 双写时期的代码共存，要求调用方在迁移期间同时维护旧结构；当路线图宣告 Phase 4 之后，可删除 `_conversationHistory` 并将历史读取改为仅依赖 AgentState。

### 能力覆盖范围与当前进度
| 能力 | 最终目标 | 已交付内容 | 尚需工作 |
| --- | --- | --- | --- |
| 历史事件模型 | 使用分层 `HistoryEntry` 表达所有可追溯事件，并支持回放和扩展 | 记录类型、Metadata 约定及追加式 AgentState 已完成，正在双写阶段验证 | 引入序列号/StableId 及回放策略，绑定持久化方案 |
| 上下文渲染管线 | 以 `RenderLiveContext()` 将历史与 Widget 渲染的 LiveScreen Sections 转换为供应商无关上下文，支持 Token 裁剪 | 基础渲染流程与 LOD 裁剪策略已就绪，LiveScreen 在写入阶段落盘 | Token 预算驱动的裁剪、跨 Provider 兼容性验收、Widget 状态自动记账 |
| Provider 抽象层 | 通过 `IProviderClient` + `ProviderRouter` 统一调度多家模型，并归并 `ModelOutputDelta` | 契约、delta 更名与 OpenAI/Anthropic 适配策略已定稿 | 实现 Router、聚合工具调用诊断、完善混合会话测试 |
| Widget 生态与辅助能力 | 将 Memory Notebook、工具清单、附件等纳入统一 Widget/Attachment 体系 | LiveScreen Section 注入机制、基础 Notebook Widget 化方案已经纳入渲染流程 | 定义附件协议、Notebook 事件条目化、动态工具 manifest 注入 |

### 与 V1 蓝图的差异对照
| 主题 | V1 蓝图侧重 | V2 调整 | 状态影响 |
| --- | --- | --- | --- |
| 文档职责 | 设计要点与阶段计划混杂，同一章节兼顾目标与节奏 | 明确把路线规划迁出，蓝图仅保留目标、已交付与待定设计 | ✅ 蓝图成为长期的“单一真相源”，路线节奏在 Roadmap 中维护 |
| 状态标识 | 通过文字段落隐含进度 | 新增状态表与章节标签（✅/🛠️/⏳） | ✅ 方便快速掌握边界，也便于例会同步与排期讨论 |
| Provider 抽象 | 着重描述 OpenAI/Anthropic 差异，缺乏统一验收口径 | 引入 `IProviderClient` 契约、ModelOutputDelta 聚合及诊断字段 | 🛠️ 直接驱动 Phase 2-3 的代码改造与测试要求 |
| Widget 体系 | Notebook/系统指令散落在段落描述中 | 将 Widget 约定、LiveScreen Section 策略及延后事项拆分成独立小节 | 🛠️ 明确 Phase 4 的前置条件，降低实现歧义 |
| 验收视角 | 缺少面向落地的完成定义 | 新增“设计验收准则”章节，列出判定标准 | 🛠️ 测试与代码评审可据此建立核对清单 |
| 历史 vs. 上下文 | 强调单向数据流但未细化操作约束 | 固化“历史只追加、上下文纯读、Provider 只读消费”等不变量 | ✅ 当前双写阶段的唯一操作准则 |
### 数据与交互流
- 参考总览：

```
┌────────────────────────┐
│    Agent Orchestrator  │
└──────────┬─────────────┘
           │ append events
           ▼
┌────────────────────────┐
│ [1] AgentState         │
└──────────┬─────────────┘
           │ RenderLiveContext()
           ▼
┌────────────────────────┐
│ [2] Context Projection │
│ (IContextMessage list) │
└──────────┬─────────────┘
           │ CallModelAsync()
           ▼
┌────────────────────────┐
│ [3] Provider Router    │
└──────┬─────────┬───────┘
       │         │
 ┌─────▼───┐ ┌───▼─────┐
 │OpenAI   │ │Anthropic│ …
 └─────────┘ └─────────┘
   ▲         ▲
   └────┬────┴─ model deltas & tool results
        │
        ▼ aggregate & append
┌────────────────────────┐
│  Orchestrator feedback │
└──────────┬─────────────┘
           │
           └────────────▶ AgentState (new history entries)
```

- **1. 历史读取**：Orchestrator 触发模型调用时，请求 AgentState 生成上下文；AgentState 反向遍历 HistoryEntry，筛选实现 `IContextMessage` 的条目，并保留写入阶段已经落盘的 Level-of-Detail Sections 与 `"[LiveScreen]"` 片段。该步骤保留时间戳与 Metadata，避免重复构造。
- **2. 上下文渲染**：渲染结果始终包含当前系统指令 (`ISystemMessage`)，以及最近相关的输入、输出、工具结果；`RenderLiveContext` 根据条目位置选择 `Live/Summary/Gist` 粒度，同时保持 LiveScreen Section 原样返回。消费方若需拆分 LiveScreen，可使用扩展方法提取，不会修改历史。
- **3. Provider 调用**：ProviderRouter 将渲染好的上下文封装为 `LlmRequest`（携带策略、`ModelInvocationDescriptor` 及工具清单），调用 `IProviderClient.CallModelAsync(request, cancellationToken)` 并获得 `ModelOutputDelta` 流；所有供应商必须遵循统一的 `CancellationToken` 与流式增量协议。
- **4. 增量解析**：ProviderClient 在流式阶段输出文本、工具调用片段和诊断增量，同时保留原始参数（必要时填入 `ParseError`）。Orchestrator 随后使用 `ModelOutputAccumulator` 聚合 delta，生成标准化的 `ModelOutputEntry`，并在工具执行完成后写入对应的 `ToolResultsEntry`。
- **5. 历史回写**：Orchestrator 在推理结束后调用 AgentState 追加模型输出和工具结果，实现“读取 → 推理 → 回写”的闭环。追加方法负责注入最终时间戳并触发 DebugUtil 记录。
- **6. 反馈与调试**：若 Provider 提供 TokenUsage 或调试元数据，可在回写前通过 `Metadata` 或 mix-in 接口补充，确保诊断信息伴随历史条目存档。
- 未来会在本节补充架构图：`![Conversation History 流程图草案](./media/conversation-history-v2.png)`（待绘制）。

### History ↔ Context ↔ Provider 协作
- **接口分层**：仅实现 `IContextMessage` 的历史条目才会进入上下文列表；非上下文型事件（如未来的配置更新）默认被过滤，形成天然的职责分界。ProviderRouter 在此基础上构造只读的 `LlmRequest`，Provider 通过请求中的角色化接口消费 Sections，并可使用 `LevelOfDetailSectionExtensions.WithoutLiveScreen` 等扩展按需拆分附加片段。
- **上下文复用**：`RenderLiveContext()` 优先返回历史条目本身，只在需要选择 `LevelOfDetail` 时包装为 `ModelInputMessage` / `ToolResultsMessage`，避免频繁复制。未来在引入 Token 限额时，可在此层实现统一的裁剪策略而不触碰 Provider。
- **回写一致性**：模型推理完成后，Orchestrator 必须先聚合全部增量再调用 AgentState 追加条目，禁止 Provider 自行写入 `_history`，以确保每轮推理对应一条 `ModelOutputEntry` 与可选的 `ToolResultsEntry`。
- **Widget 对齐**：系统指令维持宿主字段，而 Notebook 等运行态数据由 Widget 渲染为 LiveScreen 段落，并在写入阶段注入 `Live` 档 Section；当 Widget 需要事件化（例如 Notebook 切换为事件流）时，可新增专门的 `HistoryEntryKind` 而不会影响既有 Provider 协议。
- **Widget ↔ 工具执行**：Widget 暴露的 `ITool` 通过 `AgentState.EnumerateWidgetTools()` 汇总，供 Planner 与 ToolExecutor 统一调度；Widget 执行结果仍以 `ToolResultsEntry` 回写，保证历史视图的一致性。

#### LiveScreen 处理约定
- LiveScreen 由 AgentState 在 `AppendModelInput` / `AppendToolResults` 阶段调用 `BuildLiveScreenSnapshot()` 生成，并写入最近条目的 `LevelOfDetailSections.Live` 中，键名固定为 `"[LiveScreen]"`。
- `RenderLiveContext()` 仅负责选择 `LevelOfDetail`，不会额外装饰消息；消费方若需展示或忽略 LiveScreen，可通过 `WithoutLiveScreen(out liveScreen)` 之类的扩展方法拆分。
- 更多迁移细节参见《[LiveScreen 装饰器移除与 LOD Sections 迁移指南](../LiveContextProto/LiveScreen_LOD_Migration.md)》。

### ContextMessage 层接口设计（✅）
- **设计目标**：保持 Context 层供应商无关性，避免重复构造历史条目，并让 Provider 通过接口检测快速定位所需字段。
  - 基础接口仅保留角色、时间戳与 Metadata，语义字段交由派生接口承载。
  - 角色化接口覆盖系统指令、模型输入、模型输出与工具结果四种核心消息类型。
  - 可选能力通过 mix-in 接口表达（ToolCalls、TokenUsage 等），按需组合而非强制所有条目实现；LiveScreen 统一由 `LevelOfDetailSections` 提供。

```csharp
interface IContextMessage {
  ContextMessageRole Role { get; }
  DateTimeOffset Timestamp { get; }
  ImmutableDictionary<string, object?> Metadata { get; }
}

enum ContextMessageRole {
  System,
  ModelInput,
  ModelOutput,
  ToolResult
}
```

- **角色化接口**：

| 接口 | 额外字段 | 说明 |
| --- | --- | --- |
| `ISystemMessage` | `string Instruction` | 当前系统指令原文，面向所有 Provider 的最小公约数。 |
| `IModelInputMessage` | `IReadOnlyList<KeyValuePair<string,string>> ContentSections`、`IReadOnlyList<IContextAttachment> Attachments` | 表达输入分段与附件，附件目前默认返回空集合。 |
| `IModelOutputMessage` | `ModelInvocationDescriptor Invocation`、`IReadOnlyList<string> Contents`、`IReadOnlyList<ToolCallRequest> ToolCalls` | 汇总单轮模型输出的文本与工具调用声明。 |
| `IToolResultsMessage` | `IReadOnlyList<ToolCallResult> Results`、`string? ExecuteError` | 汇总工具执行结果，`ExecuteError` 表示整体失败原因（若有）。 |

```csharp
interface ISystemMessage : IContextMessage {
  string Instruction { get; }
}

interface IModelInputMessage : IContextMessage {
  IReadOnlyList<KeyValuePair<string, string>> ContentSections { get; }
  IReadOnlyList<IContextAttachment> Attachments { get; }
}

interface IModelOutputMessage : IContextMessage, IToolCallCarrier {
  ModelInvocationDescriptor Invocation { get; }
  IReadOnlyList<string> Contents { get; }
}

interface IToolResultsMessage : IContextMessage {
  IReadOnlyList<ToolCallResult> Results { get; }
  string? ExecuteError { get; }
}
```

`ModelInputMessage` 与 `ToolResultsMessage` 在实例化时会从底层 `LevelOfDetailSections` 中挑选目标粒度的 Section；当消费方需要更细粒度控制时，可重新调用 `GetSections(LevelOfDetail)` 或扩展方法在不同档位之间切换。

- **可选能力接口**：
  - `IToolCallCarrier` 由 `IModelOutputMessage` 实现，统一暴露模型声明的工具调用集合。
  - `ITokenUsageCarrier` 用于 Provider 在响应结束后回填 token 统计；当前实现默认返回 `null`。

```csharp
interface IToolCallCarrier {
  IReadOnlyList<ToolCallRequest> ToolCalls { get; }
}

interface ITokenUsageCarrier {
  TokenUsage? Usage { get; }
}

static class LevelOfDetailSectionExtensions {
  public static IReadOnlyList<KeyValuePair<string, string>> WithoutLiveScreen(
    this IReadOnlyList<KeyValuePair<string, string>> sections,
    out string? liveScreen
  );
}
```

- **协同约定**：
  - `RenderLiveContext()` 优先复用历史条目本身，通过 `LevelOfDetailSections.GetSections(lod)` 选择粒度，不再创建额外的 LiveScreen 装饰对象。
  - Provider 读取输入或工具结果时，如需展示 LiveScreen，可调用 `WithoutLiveScreen(out var liveScreen)` 拆分；未拆分也能保持上下文一致性。
  - Metadata 键名采用蛇形命名，体量限制 2 KB，超出部分需转交附件或专用条目。
  - `ToolCallRequest.RawArguments` 永远保留原始参数文本，解析成功后再补写 `Arguments` 字典；失败时将原因写入 `ParseError`。
- **暂缓设计**：结构化附件、动态工具清单等延后能力在“待定设计”章节有单独说明，本节仅定义占位接口保持兼容。

### Provider 客户端设计要点（🛠️）
#### 通用契约（✅）
- 所有 Provider 实现统一暴露 `IProviderClient.CallModelAsync(LlmRequest request, CancellationToken)`，返回 `ModelOutputDelta` 流；实现层可在 delta 中解析工具调用并填充 `ToolCallRequest.Arguments`，失败时保留 `RawArguments` 并记录 `ParseError`/`ParseWarning`。
- ProviderRouter 在构建 `LlmRequest` 时会附带策略、`ModelInvocationDescriptor` 与工具清单；Provider 只能读取 `request.Context`、`request.Tools` 等只读视图，不得修改或绕过 Orchestrator 直接回写历史。
- 任何流式阶段收集的诊断信息（TokenUsage、原始 SDK 错误等）需要在聚合完成后，通过 `Metadata` 或可选接口返回给 Orchestrator。
- 为减少重复解析代码，推荐提供共享的 `ToolArgumentParser` 辅助器（例如 `GetRequired(...)`、`ParseList(...)`、`ParseJsonSafely(...)`），供各 Provider 与工具实现重复利用。
- Provider 层需维护“Specification → 参数解析策略”的静态映射：`ModelOutputEntry.Invocation` 由 ProviderRouter 预先确定，具体的格式解析由各实现内部决定，保持 History 抽象的供应商无关性。
- 工具执行产生的耗时、失败信息由 Orchestrator 与 ToolExecutor 在回写 `ToolResultsEntry` 时统一填充，Provider 只需确保工具调用声明完整可靠。

#### OpenAI 客户端要点（🛠️）
- 将 `LlmRequest` 中的上下文翻译为 Chat Completion 所需的消息数组；`ModelInputEntry.ContentSections` 可按“标题 + 内容”的 Markdown 组合拼接成单条文本，确保段落语义被保留。
- OpenAI 在流式阶段可能输出多条工具调用 delta，客户端需在返回的 `ModelOutputDelta` 中保留声明顺序，并在 `ToolCallRequest` 上补写解析结果或错误信息。
- 推理结束后，由 Orchestrator 使用 `ModelOutputAccumulator` 聚合 delta，并在工具执行完成后写入与 `ModelOutputEntry.ToolCalls` 对齐的 `ToolResultsEntry`，保持上下游语义一致。

#### Anthropic 客户端要点（🛠️）
- Claude 要求 Assistant ↔ Tool 消息严格交替，客户端需要将模型发出的多个工具调用合并为单条工具消息，补齐必要的占位文本，避免奇偶序列破坏。
- 返回 `ModelOutputDelta` 流时沿用与 OpenAI 客户端相同的 `ToolCallRequest` 协议，解析失败时在请求上记录 `ParseError` 或发出 `ModelOutputDelta.ExecutionError`，保持跨供应商一致性。
- 工具调用执行结果由 Orchestrator/ToolExecutor 负责写回历史；若 Provider 侧识别到底层消息无法解析，可在 delta 流中暴露错误信息，供上层策略决定是否重试。

#### 其他供应商（⏳）
- Azure OpenAI 与 OpenAI v1 客户端共享消息构造逻辑，可通过配置区分终端点与凭据。
- 针对本地推理（vLLM/Transformers），客户端可选择直接在进程内调用推理引擎，但仍须遵守 `IProviderClient` 契约并返回标准化 delta。

#### ModelOutputDelta 管线（🛠️）
- 当前阶段仅对既有 `ChatResponseDelta` 更名为 `ModelOutputDelta`，保留文本片段、工具调用增量等字段，确保改造成本最小化。
- Orchestrator 在流式消费时累积 delta，结束后转化为一条 `ModelOutputEntry` 与可选的 `ToolResultsEntry`；任何实时调试数据应在聚合完成时写入 AgentState，避免历史层出现半成品数据。
- 随后若引入更细粒度的 delta 类型（如多模态 Part），将在保持向后兼容的前提下扩展结构，而不会影响既有 Provider 实现。

### 组件责任边界
- **AgentState（✅历史事实源）**：管理 `_history` 与 Widget 宿主的一致性，负责时间戳注入、DebugUtil 打点以及只读视图暴露；禁止外部直接篡改集合，任何追加必须经过语义化入口。
- **Context Projection（🛠️渲染层）**：通过 `RenderLiveContext()` 实现从历史→上下文的单向转换，可插入 LiveScreen 或其他临时视图，但不产生副作用；后续 Token 裁剪、去重逻辑也将集中在此处。
- **Provider Router（🛠️调度层）**：根据模型策略选择 `IProviderClient`，对输入执行协议适配，对输出聚合 `ModelOutputDelta` 并反馈统一结构；仍需补齐混合供应商的故障转移与指标上报。
- **Orchestrator（🛠️业务协调）**：封装模型调用生命周期，触发上下文渲染、驱动 Provider 调用，并在推理结束后统一回写历史；需要在实现阶段补充失败重试、工具链超时等跨层策略。

### 设计模式映射
#### MVVM 映射（✅）
三段式流水线天然映射到 MVVM：

| MVVM 层次 | Conversation History 角色 | 主要职责 |
| --- | --- | --- |
| Model | AgentState | 持有不可变历史与 Widget 宿主，是单一事实源 |
| ViewModel | `RenderLiveContext()` 输出 | 将历史投影成上下文视图，可按需注入 LiveScreen / 记忆片段 |
| View | Provider 客户端 | 消费 `IContextMessage` 列表，转换为供应商协议并驱动推理 |

该映射保证单向数据流（History → Context → Provider），并允许在不修改历史的前提下为不同供应商提供多种上下文视图。

#### Event Sourcing 思维（🛠️）
HistoryEntry 以仅追加方式记录事件，运行时快照（系统指令、记忆 Notebook 等）可通过重放恢复。当前阶段仅注入时间戳；顺序号与 StableId 延后到持久化方案落地时统一引入，避免早期维护冗余字段。

- `ModelInputEntry` / `ModelOutputEntry` / `ToolResultsEntry` 被明确视作领域事件，命名中保留业务语义，避免回到 CRUD 式的模糊表示。
- 系统指令与记忆 Notebook 分别由宿主字段与 Widget 维护，渲染阶段生成 `SystemInstructionMessage`；一旦 Widget 事件模型敲定，将把这些变更补记到历史中以确保可回放性。
- 在缺省环境下依赖 `_clock.Now` 注入时间戳，单元测试可替换 `IClock` 模拟，保证事件时间线稳定；后续若引入序列号，将复用同一注入点。
- DebugUtil 打点绑定在事件追加流程中，结合事件日志可追溯调用链与工具执行，契合 Event Sourcing“事件即审计记录”的目标。

#### CQRS 与 Repository（🛠️）
AgentState 内部将写入接口（`AppendModelInput/Output/ToolResults` 等）与查询接口（`RenderLiveContext`、计划中的 `GetRecentEntries`）分离，并对外以领域化 API 暴露。未来若引入持久化层，可用仓储实现替换内存集合而不影响调用方。

- 写入侧负责时间戳注入、ToolCall 对齐、Debug 打点等副作用，调用者只需知道“追加哪个领域事件”，无需了解内部集合结构。
- 查询侧维持纯读特性，后续若需要缓存或裁剪上下文，可以在不触碰写入路径的情况下迭代策略，降低回归风险。
- Repository 思路体现在 AgentState 对外暴露的受控 API：调用方无法直接访问 `_history`，未来将可替换为持久化存储（JSON、SQLite、事件库）而无需改动 orchestrator 代码。
- 在单元测试中可通过 stub/fixture AgentState 验证写入事件与查询视图的分离效果，为未来的持久化层适配留出余地。

#### Strategy + Adapter（🛠️）
`IProviderClient` 为 ProviderRouter 提供统一策略入口，Router 会把 `RenderLiveContext()` 输出与策略元数据封装为 `LlmRequest` 传入，不同实现以 Adapter 方式对接 OpenAI、Anthropic、vLLM 等 SDK。每个实现按照 `LlmRequest` 契约读取上下文、工具清单与 `ModelInvocationDescriptor`，负责翻译 `IContextMessage`、解析增量与工具调用，并回写标准化结构。

- Strategy 层由 ProviderRouter 承担，根据模型策略（规划/执行/评审）或运行时指标（延迟、成本）挑选不同 Provider，实现“按需换脑”。
- Adapter 层屏蔽供应商 SDK 差异：每个 `IProviderClient` 实现只处理自身协议细节，例如 OpenAI 的 Function Calling vs Responses API，Anthropic 的 Messages API 等。
- 组合 Strategy + Adapter 让新供应商的接入流程清晰：先实现一个 Adapter，暴露统一接口，再在 Router 中注册策略即可投入使用。
- 该设计也便于构建模拟 Provider，用适配器包装测试桩即可复用 Router 逻辑，支撑端到端验证。

#### Decorator（历史回顾）
早期版本通过 `ContextMessageLiveScreenHelper` 装饰上下文条目动态附加 LiveScreen。自 LOD Sections 上线后，LiveScreen 改为在写入阶段落盘为 `"[LiveScreen]"` Section，装饰器实现已退役，该章节保留作为迁移背景记录。

### AgentState 与 Widget 职责（🛠️）
- **状态聚合器**：AgentState 维护 `_history` 并作为 Widget 宿主，统一创建、持有并暴露受控访问器（当前包含 `MemoryNotebookWidget`），确保“唯一事实源”。
- **追加式历史**：所有历史事件都需通过 AgentState 追加，禁止外部直接编辑或删除；Widget 在更新自身状态时必须委托 AgentState 的写入入口，以保持历史与运行态一致。
- **上下文渲染约束**：`RenderLiveContext()` 默认运行在单线程 orchestrator 上，反向遍历历史并挑选最新输入或工具条目附加 LiveScreen；Widget 通过 `RenderLiveScreen` 输出 Markdown 片段，由 AgentState 统一注入。
- **Widget 工具桥接**：AgentState 提供 `EnumerateWidgetTools()` 将所有 Widget 暴露的 `ITool` 组合成只读清单，供 Planner/LLM 在调用阶段发现与执行。
- **宿主配置变更**：系统指令等宿主字段仍由 AgentState 直接维护；Widget 负责自身的运行态快照（例如 Notebook 内容），必要时可将变更透出为历史元数据或 Debug 日志。
- **单线程假设**：依赖 orchestrator 的串行执行模型，内部不加锁；未来若引入多线程，需要在调用方增加同步层或引入事务包装。
- **回放预留**：暂不支持历史回放/快照，但所有追加接口与 Widget 状态入口都集中在 AgentState，后续可在同一点扩展序列号、快照与 rollback 能力。

#### Widget 约定（🛠️）
- Widget 必须实现 `IWidget` 接口并提供稳定的 `Name`、`Description` 与只读 `Tools` 集合，供发现与能力曝光。
- `RenderLiveScreen(WidgetRenderContext)` 负责将 Widget 内部状态序列化为 Markdown 片段；返回 `null` 或空字符串表示不注入 LiveScreen。
- `ExecuteTool(toolName, ToolExecutionContext)` 用于派发工具执行；Widget 内部需要验证工具名称，并在成功或失败路径上通过 `ToolHandlerResult` 明确结果。
- Widget 可以访问 `WidgetRenderContext.AgentState` 的只读视图读取历史或其他 Widget 快照，但不得直接修改 `_history`；任何状态变更需通过宿主提供的受控方法（如 `ReplaceNotebookFromHost`）完成。
- Widget 应在关键操作中调用 `DebugUtil`，为运行时追踪提供统一日志入口（推荐使用 `MemoryNotebookWidget` 等专用类别）。
- 当前阶段默认 `AgentState` 以固定顺序遍历 `_widgets` 并拼接 LiveScreen；未来若需要优先级或分组，可扩展 `WidgetMetadata`。
- Widget 接口草案与更详细的职责说明可参考《[LiveContextProto Widget 设计概念草案](../LiveContextProto/WidgetConcept.md)》。

#### 顺序与稳定标识策略（⏳）
- 当前阶段不维护单调序列号或稳定 Guid，以避免在尚未启用持久化功能前引入额外负担。
- 待事件存储或快照方案明确后，将在追加入口集中注入 `SequenceNumber`、`StableId` 等字段，并配套测试与回放机制。
- 领域方法已保留统一的时间戳注入路径，后续扩展顺序号或其他元数据时可复用相同的注入点。

- **暂缓能力**：历史回放、快照恢复、Widget 状态自动记账等能力被统一纳入 Deferred 范畴，待事件存储方案明确后再设计，以免分散当前阶段的实现精力。

#### AgentState 目标 API 轮廓（🛠️）
> 目标：在 Phase 1 尾声替换临时的 `AppendHistory`，让 orchestrator 仅依赖语义化追加入口，并显式暴露 Widget 宿主能力。

- `AppendModelInput(ModelInputEntry entry)`
  - **输入**：已填充业务字段的 `ModelInputEntry`，至少包含一段 `ContentSections`。
  - **行为**：在注入时间戳与默认 Metadata 后写入 `_history`，并记录 DebugUtil 打点。
  - **错误模式**：当 `ContentSections` 为空时抛出 `ArgumentException`，阻止空输入污染历史。
- `AppendModelOutput(ModelOutputEntry entry)`
  - **输入**：聚合后的模型输出条目，至少包含正文或工具调用其一。
  - **行为**：写入 `_history` 前执行最小校验，后续可在调用方注入 delta 统计等 Metadata。
  - **错误模式**：当正文与工具调用同时为空时抛出 `ArgumentException`。
- `AppendToolResults(ToolResultsEntry entry)`
  - **职责**：保证工具执行结果与上一条 `ModelOutputEntry.ToolCalls` 对齐；支持仅写入 `ExecuteError` 的失败场景。
  - **行为**：统一注入时间戳并追加到 `_history`。
- `SetSystemInstruction(string instruction)`
  - **行为**：更新系统指令宿主字段并写入 Debug 日志；后续计划通过专门的 HistoryEntry 记账。
- `UpdateMemoryNotebook(string? content)`
  - **职责**：由宿主调用以同步 Notebook 最新内容，内部委托 `MemoryNotebookWidget.ReplaceNotebookFromHost`。
- `EnumerateWidgetTools()`
  - **行为**：遍历 `_widgets` 聚合所有 `ITool`，供 Planner 或 ToolExecutor 获取 Widget 能力清单。
- `RenderLiveContext()`
  - **行为**：反向遍历历史生成上下文列表，基于位置选择 `LevelOfDetail` 并返回包装消息；LiveScreen Section 在写入阶段已注入，无需额外附着。

上述方法共用私有 `AppendContextualEntry<T>`，负责统一注入时间戳并写入 Debug 日志；Widget 状态写入统一通过宿主辅助方法完成。

**边界场景**：

- **空历史初始化**：首次追加应自动注入系统指令投影，避免 `RenderLiveContext()` 返回空序列。
- **并发写入**：继续依赖外部加锁，但通过注入式 `IClock` 与 `IDebugSink` 让单元测试可控。
- **回滚需求**：暂不暴露撤销接口；若未来引入，将通过事务化包装器与 `AgentStateSnapshot` 协调。

##### 示例：AgentState 骨架草图（🛠️）
> 此示例以伪代码形式展示语义化追加接口、统一时间戳注入与上下文渲染的协作方式，具体依赖（`IClock`、`LiveContextProjectionOptions`、`EnsureToolCallAlignment` 等）将在实现阶段落地。

```csharp
sealed class AgentState {
  private readonly List<HistoryEntry> _history = new();
  private readonly Func<DateTimeOffset> _timestampProvider;
  private readonly ImmutableArray<IWidget> _widgets;

  public AgentState(Func<DateTimeOffset> timestampProvider, string systemInstruction) {
    _timestampProvider = timestampProvider;
    SystemInstruction = systemInstruction;

    var notebook = new MemoryNotebookWidget();
    _widgets = ImmutableArray.Create<IWidget>(notebook);
    MemoryNotebookWidget = notebook;
  }

  public string SystemInstruction { get; private set; }
  public MemoryNotebookWidget MemoryNotebookWidget { get; }
  public IReadOnlyList<HistoryEntry> History => _history;

  public ModelInputEntry AppendModelInput(ModelInputEntry entry) {
    if (entry.ContentSections.Live is not { Count: > 0 }) {
      throw new ArgumentException("ContentSections must contain at least one section.", nameof(entry));
    }

    var enriched = AttachLiveScreen(entry);
    return AppendContextualEntry(enriched);
  }

  public ModelOutputEntry AppendModelOutput(ModelOutputEntry entry) {
    if ((entry.Contents is null || entry.Contents.Count == 0) &&
        (entry.ToolCalls is null || entry.ToolCalls.Count == 0)) {
      throw new ArgumentException("ModelOutputEntry must include content or tool calls.", nameof(entry));
    }

    return AppendContextualEntry(entry);
  }

  public ToolResultsEntry AppendToolResults(ToolResultsEntry entry) {
    if (entry.Results.Count == 0 && string.IsNullOrWhiteSpace(entry.ExecuteError)) {
      throw new ArgumentException("ToolResultsEntry requires results or an error.", nameof(entry));
    }

    var enriched = AttachLiveScreen(entry);
    return AppendContextualEntry(enriched);
  }

  public IReadOnlyList<IContextMessage> RenderLiveContext() {
    var messages = new List<IContextMessage>(_history.Count + 1);
    var detailOrdinal = 0;

    for (var index = _history.Count - 1; index >= 0; index--) {
      if (_history[index] is not ContextualHistoryEntry contextual) { continue; }

      switch (contextual) {
        case ModelInputEntry input: {
          var detail = ResolveDetailLevel(detailOrdinal++);
          messages.Add(new ModelInputMessage(input, detail));
          break;
        }
        case ToolResultsEntry results: {
          var detail = ResolveDetailLevel(detailOrdinal++);
          messages.Add(new ToolResultsMessage(results, detail));
          break;
        }
        default:
          messages.Add(contextual);
          break;
      }
    }

    messages.Add(new SystemInstructionMessage(SystemInstruction) {
      Timestamp = _timestampProvider()
    });

    messages.Reverse();
    return messages;
  }

  private T AppendContextualEntry<T>(T entry) where T : ContextualHistoryEntry {
    var finalized = entry with { Timestamp = _timestampProvider() };
    _history.Add(finalized);
    DebugUtil.Print("History", $"Appended {finalized.Role} entry (count={_history.Count})");
    return finalized;
  }

  private static LevelOfDetail ResolveDetailLevel(int ordinal)
    => ordinal == 0 ? LevelOfDetail.Live : LevelOfDetail.Summary;

  private ModelInputEntry AttachLiveScreen(ModelInputEntry entry) {
    var liveScreen = BuildLiveScreenSnapshot();
    if (string.IsNullOrWhiteSpace(liveScreen)) { return entry; }

  var updatedSections = entry.ContentSections.WithLiveSection(LevelOfDetailSectionNames.LiveScreen, liveScreen);
    return ReferenceEquals(updatedSections, entry.ContentSections)
      ? entry
      : entry with { ContentSections = updatedSections };
  }

  private ToolResultsEntry AttachLiveScreen(ToolResultsEntry entry) {
    if (entry.Results.Count == 0) { return entry; }

    var liveScreen = BuildLiveScreenSnapshot();
    if (string.IsNullOrWhiteSpace(liveScreen)) { return entry; }

    var results = entry.Results.ToArray();
    var latest = results[^1];
  var updated = latest.Result.WithLiveSection(LevelOfDetailSectionNames.LiveScreen, liveScreen);
    if (ReferenceEquals(updated, latest.Result)) { return entry; }

    results[^1] = latest with { Result = updated };
    return entry with { Results = results };
  }

  private string? BuildLiveScreenSnapshot() {
    var fragments = new List<string>();
    var renderContext = new WidgetRenderContext(this, ImmutableDictionary<string, object?>.Empty);

    foreach (var widget in _widgets) {
      var fragment = widget.RenderLiveScreen(renderContext);
      if (!string.IsNullOrWhiteSpace(fragment)) {
        fragments.Add(fragment.TrimEnd());
      }
    }

    if (fragments.Count == 0) { return null; }

    var builder = new StringBuilder();
    builder.AppendLine("# [Live Screen]");
    builder.AppendLine();

    for (var i = 0; i < fragments.Count; i++) {
      builder.AppendLine(fragments[i]);
      if (i < fragments.Count - 1) {
        builder.AppendLine();
      }
    }

    return builder.ToString().TrimEnd();
  }

  internal IEnumerable<ITool> EnumerateWidgetTools()
    => _widgets.SelectMany(widget => widget.Tools);
}
```

- **暂缓功能**：历史回放、快照恢复、Widget 状态事件化等能力被列入 Deferred 范畴，避免干扰当前阶段的核心实现。

## 已完成设计（Delivered Scope）
### 落地组件一览
| 组件 | 设计结论 | 实施指引 |
| --- | --- | --- |
| HistoryEntry 类型族 | 采用 `ContextualHistoryEntry` + `HistoryEntryKind` + `Metadata` 的 record 层级，所有可投影到上下文的事件直接实现 `IContextMessage` | Phase 1 期间维持 `_conversationHistory` 与 `_history` 双写，语义化追加入口负责注入时间戳与 DebugUtil 日志 |
| ContextMessage 接口束 | 基础接口 + 角色化接口（System/Input/Output/ToolResult）+ mix-in 能力（ToolCall、TokenUsage），保证供应商无关抽象，LiveScreen 由 Sections 持久化 | Provider 通过接口检测分支处理，新增能力以 mix-in 扩展保持向后兼容，禁止直接依赖具体记录类型 |
| ModelInvocationDescriptor 与 Tool* 结构 | `ModelInvocationDescriptor` 描述 Provider/Specification/Model，`ToolCallRequest` + `ToolCallResult` 统一表达工具调用及执行结果 | Provider 客户端负责填充 Invocation 与参数解析，Orchestrator 在聚合 delta 后一次性落盘，与工具执行结果顺序对齐 |
| LevelOfDetailSections & 扩展方法 | 提供 `Live/Summary/Gist` 三档 Section，并内置 `"[LiveScreen]"` 片段抽离/替换能力 | 写入阶段统一注入 LiveScreen Section，消费方可使用 `WithoutLiveScreen`/`TryGetSection` 等扩展按需拆分 |

上述组件是当前蓝图已经冻结的设计模块，可直接作为代码实现的基线；待 Phase 1 收尾后，旧的 `_conversationHistory` 将被逐步淘汰。

### 接口契约
#### AgentState 与历史写入
- AgentState 负责注入时间戳并维护只读 `History` 视图，外部不得直接修改 `_history`；所有追加入口都会触发 DebugUtil 日志。
- `ToolCallRequest.RawArguments` 永远保留原始文本，Provider 在写入前解析并填入 `Arguments`，解析失败需把原因写入 `ParseError`。
- `IContextAttachment` 在 Phase 1 内必须返回空集合以维持接口稳定，附件体系扩展后将由 AgentState 统一管理写入。
- 每条 `ModelOutputEntry` 必须携带非空的 `ModelInvocationDescriptor`，便于在历史审计和混合模型会话中准确复原调用环境；缺失视为实现缺陷，应通过单元测试阻断。
- 双写阶段追加 `_conversationHistory` 与 `_history` 时，若发生写入失败需保持旧结构与新结构的一致性，必要时通过事务包装或补偿追加避免“新旧历史”分叉。

#### ContextMessage 与 Provider 消费
- `IModelOutputMessage.ToolCalls` 与最近的 `IToolResultsMessage.Results` 需要按顺序一一对应，Provider 负责保证配对；若无法解析，必须以失败状态的 `ToolCallResult` 回写。
- Metadata 键名遵循蛇形命名且单条限制 2 KB，超限信息需转化为附件或独立条目；Provider 在追加诊断信息时同样遵守该约束。
- LiveScreen 以 `LevelOfDetailSections.Live` 中的 `"[LiveScreen]"` Section 持久化，消费方可在渲染前调用扩展方法拆分附加片段，未拆分时也会维持上下文一致。
- `IContextMessage` 仅暴露角色、时间戳与 Metadata，正文语义由角色化接口提供；Provider 通过 `is` 检查选择处理路径。
- `IToolCallCarrier`、`ITokenUsageCarrier` 等 mix-in 为可选能力，未实现即视为不支持相应特性。
- Provider 必须把 `Timestamp` 视为只读数据源：不得重写、覆盖或重新排序；当底层 SDK 提供更精确的时间数据时，应通过 Metadata 新增字段，而非修改既有时间戳。
- 任意 Provider 扩展都需验证 `IContextMessage` 列表中的未知角色/能力能被安全忽略，防止未来新增 mix-in 时出现运行时异常。

### 关键类型定义
以下定义展示了当前阶段的核心记录类型与接口组合，作为实现团队的结构参考：

```csharp
abstract record HistoryEntry {
  public DateTimeOffset Timestamp { get; init; }
  public abstract HistoryEntryKind Kind { get; }
  public ImmutableDictionary<string, object?> Metadata { get; init; }
    = ImmutableDictionary<string, object?>.Empty;
}

abstract record ContextualHistoryEntry : HistoryEntry, IContextMessage {
  public abstract ContextMessageRole Role { get; }
}

sealed record ModelInputEntry(
  LevelOfDetailSections ContentSections
) : ContextualHistoryEntry {
  public override HistoryEntryKind Kind => HistoryEntryKind.ModelInput;
  public override ContextMessageRole Role => ContextMessageRole.ModelInput;
  public IReadOnlyList<IContextAttachment> Attachments { get; init; } = Array.Empty<IContextAttachment>();
}

sealed record ModelOutputEntry(
  IReadOnlyList<string> Contents,
  IReadOnlyList<ToolCallRequest> ToolCalls,
  ModelInvocationDescriptor Invocation
) : ContextualHistoryEntry, IModelOutputMessage {
  public override HistoryEntryKind Kind => HistoryEntryKind.ModelOutput;
  public override ContextMessageRole Role => ContextMessageRole.ModelOutput;
  IReadOnlyList<string> IModelOutputMessage.Contents => Contents;
  ModelInvocationDescriptor IModelOutputMessage.Invocation => Invocation;
  IReadOnlyList<ToolCallRequest> IToolCallCarrier.ToolCalls => ToolCalls;
}

sealed record ToolResultsEntry(
  IReadOnlyList<HistoryToolCallResult> Results,
  string? ExecuteError
) : ContextualHistoryEntry {
  public override HistoryEntryKind Kind => HistoryEntryKind.ToolResult;
  public override ContextMessageRole Role => ContextMessageRole.ToolResult;
}

sealed record HistoryToolCallResult(
  string ToolName,
  string ToolCallId,
  ToolExecutionStatus Status,
  LevelOfDetailSections Result,
  TimeSpan? Elapsed
);

record ToolCallRequest(
  string ToolName,
  string ToolCallId,
  string RawArguments,
  IReadOnlyDictionary<string, object?>? Arguments,
  string? ParseError,
  string? ParseWarning
);

record ToolCallResult(
  string ToolName,
  string ToolCallId,
  ToolExecutionStatus Status,
  IReadOnlyList<KeyValuePair<string, string>> Result,
  TimeSpan? Elapsed
);

sealed class LevelOfDetailSections {
  public LevelOfDetailSections(
    IReadOnlyList<KeyValuePair<string, string>> live,
    IReadOnlyList<KeyValuePair<string, string>> summary,
    IReadOnlyList<KeyValuePair<string, string>> gist
  ) { /* ... */ }

  public IReadOnlyList<KeyValuePair<string, string>> Live { get; }
  public IReadOnlyList<KeyValuePair<string, string>> Summary { get; }
  public IReadOnlyList<KeyValuePair<string, string>> Gist { get; }

  public IReadOnlyList<KeyValuePair<string, string>> GetSections(LevelOfDetail detail);
  public LevelOfDetailSections WithLiveSection(string key, string value);
  public static LevelOfDetailSections CreateUniform(IReadOnlyList<KeyValuePair<string, string>> sections);
}

static class LevelOfDetailSectionNames {
  public const string LiveScreen = "[LiveScreen]";
}

static class LevelOfDetailSectionExtensions {

  public static IReadOnlyList<KeyValuePair<string, string>> WithoutLiveScreen(
    this IReadOnlyList<KeyValuePair<string, string>> sections,
    out string? liveScreen
  );

  public static string ToPlainText(IReadOnlyList<KeyValuePair<string, string>> sections);
}

enum HistoryEntryKind { ModelInput, ModelOutput, ToolResult }

enum ToolExecutionStatus { Success, Failed, Skipped }

record ModelInvocationDescriptor(string ProviderId, string Specification, string Model);

record SystemInstructionMessage(string Instruction) : ISystemMessage {
  public ContextMessageRole Role => ContextMessageRole.System;
  public DateTimeOffset Timestamp { get; init; }
  public ImmutableDictionary<string, object?> Metadata { get; init; }
    = ImmutableDictionary<string, object?>.Empty;
}

record TokenUsage(int PromptTokens, int CompletionTokens, int? CachedPromptTokens);

interface IContextAttachment { }
```

> 备注：`IContextAttachment` 仍为空占位，待附件协议敲定后补充具体实现；`HistoryEntryKind` 可在未来扩展 LiveContextSnapshot、PlannerDirective 等派生类型。

### 设计验收准则
- **HistoryEntry 分层**：所有可写入条目均需通过 AgentState 的语义化方法生成，单元测试应验证追加后 `Kind`、`Role` 与时间戳被正确注入；若直接向 `_history` 写入视为违背设计。
- **上下文渲染**：`RenderLiveContext()` 在给定固定输入时必须具备幂等性，同一历史集应生成完全一致的 `IContextMessage` 列表；验收以快照测试或语义比较断言为准。
- **Provider 契约**：实现 `IProviderClient` 的类需通过统一契约测试，覆盖文本增量、工具调用解析失败、LiveScreen Section 拆分等场景；若任一场景失败则视作未达成设计要求。
- **ToolCalls 配对**：`ModelOutputEntry.ToolCalls` 与 `ToolResultsEntry.Results` 必须在回写阶段完成一一对应校验，并在 Metadata 中记录异常；验收测试需模拟对齐与错位两种情况。
- **Widget 记账**：Widget 引起的状态变更（如 Notebook 更新）必须能在历史中追踪（当前阶段允许人工触发），验收时需检查历史条目能够重建最新 Widget 视图。
- **调试输出**：关键写入路径需调用 `DebugUtil` 打点，并允许通过 `ATELIA_DEBUG_CATEGORIES=History` 精确开启；若新增路径未打点须补齐。

### 实现约束与注意事项
- 当前实现仍与旧 `_conversationHistory` 并行，需要双写同步；完成 Provider 管线改造后方可移除旧结构。
- AgentState 假定运行在单线程 orchestrator 上，未在内部加锁；如需跨线程写入，必须由调用方提供同步机制。
- RenderLiveContext 暂未实现基于 Token 的截断策略，长对话需依赖上层策略控制上下文长度；后续由 Provider 或 Router 根据预算裁剪。
- 系统指令仍由宿主字段维护，记忆 Notebook 已由 Widget 封装，尚未追加对应的 HistoryEntry；在引入统一 Widget 事件模型前需由调用方保证一致性。
- HistoryEntry 的序列号、StableId 暂缺省，相关字段需等待持久化设计落定后统一引入。
- 单元测试在 Phase 1 内应围绕语义化追加接口构造用例（时间戳注入、ToolCall 对齐、LiveScreen Section 注入判定），并利用 DebugUtil 日志辅助排查；关于顺序号/回放的断言延后至持久化阶段再补齐。
- 集成测试需覆盖“规划模型 + 执行模型”混合流程，验证双 Provider 场景下 HistoryEntry 顺序、LiveScreen Section 及 ToolResult 配对均保持稳定。
- 新增 Provider 实现必须通过统一的契约测试套件（Pending），该套件会模拟未知 mix-in、空附件等边界以确保向后兼容。

## 待定设计（Deferred & TBD）
### 延后引入项
- **稳定标识与顺序号（⏳）**
  - 目标：为历史条目提供跨持久化的稳定引用，并支撑回放排序与幂等写入。
  - 依赖：事件存储或快照机制落定后，再在统一入口生成 `SequenceNumber`、`StableId`，并补充迁移与回放测试。
  - 现状：Phase 1-3 内继续依赖时间戳排序，语义化追加方法已预留注入点。
- **结构化附件体系（⏳）**
  - 目标：让上下文支持富媒体、JSON 片段等结构化数据，避免使用纯文本约定。
  - 约束：当前 `IContextAttachment` 要求返回空集合；附件的序列化、缓存与存储策略将在 Phase 4 集中评审，并与工具、[LiveContext] 共享协议。
- **动态工具清单与提示生成（⏳）**
  - 愿景：根据实时可用的工具、函数签名生成精准提示，替代静态写死的工具说明。
  - 依赖：需要 Planner/Executor 在运行期公开工具注册信息，并由 Provider Router 决定注入时机；待 Router 稳定后再设计上下文入口。
- **Memory Notebook 事件化（⏳）**
  - 目标：将 Notebook 编辑操作记录为 `HistoryEntry`，支持审计、撤销与跨线程同步。
  - 现状：Phase 1 使用 `MemoryNotebookWidget` 保持内存态，调用方需手动保证 Notebook 内容与历史描述一致。
- **[LiveContext] 与 History 协调（⏳）**
  - 目标：记录每次调用注入的 [LiveContext] 片段，便于调试与回放时比对上下文差异。
  - 约束：需要确定 [LiveContext] 的持久化策略与裁剪逻辑，避免引入过多冗余条目；待 Provider Router 与 Widget 统一接口稳定后评估实现窗口。
- **TokenUsage / 调试遥测整合（⏳）**
  - 议题：统一 token 统计、缓存命中与 DebugUtil 输出，避免在 Provider 层重复采集。
  - 依赖：全局遥测与计费策略确定责任边界后，再决定由 Provider 还是 AgentState 聚合统计。
- **AgentStateSnapshot / Rollback 能力（⏳）**
  - 目标：提供历史回溯与灾难恢复手段，并支持失败事务的回滚。
  - 计划：与持久化方案同批评审，预计与事件存储、快照回放一并设计，当前阶段只保留追加式接口。

### 待进一步调研的问题
- **多 Provider 混合会话的上下文截断策略**
  - 需要明确 Planner/Executor 交替调用时的上下文裁剪优先级、缓存复用与去重规则，避免重复传输大量提示文本。
- **Anthropic 工具消息奇偶失配的自动修复**
  - 待确认是否在 Provider 层自动补齐占位消息，或在 Orchestrator 引入重试/回退策略，从而减少人工介入。
- **事件回放落地后对 AgentState API 的影响**
  - 需评估语义化追加接口是否要支持事务包装、乐观并发控制，以及如何与 DebugUtil、快照机制协同。
- **Memory Notebook v2 的耦合模式**
  - 需要定义 Notebook 摘要如何进入上下文、冲突如何解决，以及 Notebook 条目与历史事件之间的链接形式。
- **Tool 参数解析一致性策略**
  - 需要评估是否在 Provider 层建立统一的 `ToolArgumentParser` 规则库，或允许工具作者提供自描述 schema；该决定将影响 `ToolCallRequest.Arguments` 的稳定性与向后兼容性。

## 与实施路线图的关系
- 路线图的 Phase 规划以本蓝图的设计主题为边界：Phase 1 覆盖 HistoryEntry 与核心接口，Phase 2/3 处理 Provider 管线，Phase 4 聚焦延后引入项。
- 当 Roadmap 调整阶段目标或里程碑时，需在本蓝图的“设计状态总览”同步状态，并在对应章节更新假设/约束。
- 蓝图不再列出具体任务顺序，相关内容统一迁移至路线图与工程任务列表。

## 术语与参考资料
- **AgentState**：管理对话历史与运行时状态的聚合对象，是 Conversation History 的唯一事实来源。
- **Widget**：封装运行态状态、工具集合与 LiveScreen 呈现的组件，目前包含 `MemoryNotebookWidget`，未来将扩展更多能力。
- **LiveInfo（Legacy）**：旧版本对运行态快照的称谓，现已由 Widget 体系接手其职责。
- **LiveScreen**：在上下文中暂时展示的高优先级信息，由 Widget 渲染为 Markdown 并在写入阶段注入到最近条目的 `"[LiveScreen]"` Section 中。
- **Provider Router**：根据策略选择底层模型供应商的调度层，标准接口为 `IProviderClient`。
- **ModelOutputDelta**：供应商返回的流式增量统一表示，最终汇总为 `ModelOutputEntry`。
- **ModelInvocationDescriptor**：记录 Provider/Specification/Model 三元组，用于回溯模型选择上下文。
- **ToolCallRequest / ToolCallResult**：模型输出的工具调用声明与执行结果，前者保留原始参数文本，后者记录执行状态与耗时。
- 参考文档：
  - 《Memo: Conversation History 抽象重构实施路线图》V2（筹备中）
  - 《Atelia_Naming_Convention.md》
  - 《AGENTS.md》中 DebugUtil 章节

---

## 重构进度追踪
### 本轮更新
- 2025-10-13：**设计状态总览更新**，根据 LiveContextProto 实际代码进度同步状态标记：AgentState 历史层抽象、ContextMessage 接口束、Context 渲染/投影、Provider 抽象与路由、AgentState 语义化追加 API 已全部标记为 ✅（已完成并验证）；Widget 体系与附加能力、诊断与元数据策略升级为 🛠️（基础功能已实现，扩展功能待补充）。与路线图 Phase 2 完成、Phase 3 进行中的实际进度保持一致。
- 2025-10-12：修正"设计状态总览"中 ContextMessage 章节锚点，恢复状态表与正文之间的跳转一致性。
- 2025-10-12：为 Provider 客户端与 AgentState 章节补充状态标识与目录标注，使蓝图主体与"设计状态总览"保持一致的成熟度提示。
- 对照 V1 蓝图补写"Event Sourcing""CQRS 与 Repository""Strategy + Adapter"章节，明确事件语义、读写分离与供应商适配流程。
- 将 DebugUtil 打点、`IClock` 注入、策略路由等细节显式写入设计模式章节，便于实现团队直接引用。

### 既往里程碑
- 2025-10-12（第 1 轮）：新增"与 V1 蓝图的差异对照"表、补充设计验收准则，并完成目录与状态标识的统一整理。

### 下一步计划
- 在 Phase 3 完整验收后，评估真实 Provider（OpenAI/Anthropic）接入的准备工作，补充跨供应商兼容性测试用例。
- 梳理 Deferred 项（尤其是 Widget 事件化、附件体系、动态工具 manifest）的设计前置条件，准备 Phase 4 所需的待办列表。
- 定期校验蓝图与路线图的状态总览一致性，确保设计真相源与实施进度同步更新。
