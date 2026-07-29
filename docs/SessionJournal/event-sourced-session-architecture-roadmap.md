# SessionJournal 事件源会话与长期上下文架构路线图

> **状态**：Architecture Roadmap / current baseline P1～P6 + Prepared v5 + DM-0～DM-8
> **日期**：2026-07-29
> **底层依赖**：[EventJournal 功能需求与粗粒度设计基线](../EventJournal/event-journal-requirements-and-design.md)
> **相关既有研究**：[Dynamic Logical Context Store for Long-Running Role-Play Agents](../Galatea/backlog/idea/dynamic-logical-context-store-for-long-running-role-play-agents.md)
> **后续实施计划**：
> [SessionJournal 恢复与 DerivedMemory 化简](session-journal-recovery-and-derived-memory-simplification-plan.md)、
> [EADR 核心概念](event-addressed-derived-recap-concepts.md)、
> [Event-addressed Derived Recap V4 目标设计](event-addressed-derived-recap-v4-target-design.md)、
> [EADR V4 实现与替换计划](event-addressed-derived-recap-v4-implementation-plan.md)
>
> **已完成实施记录**：
> [DerivedMemory 可替换子系统与 Shared Epoch 实施方案](done/derived-memory-subsystem-implementation-plan.md)

> **DM-8 supersession（2026-07-28）**：本文后续章节中仍出现的 Prepared v3、raw
> derived-set activation、manual checkpoint 与 concrete store in core 均是路线演进记录，不是
> current contract。当前以 Prepared v5 + bounded two-phase store-neutral candidate +
> derived-only ArtifactSet 为唯一实现路径；raw audit 和 Prepared exact reopen 不打开
> DerivedMemory。unprepared online planning 由 host 注入 lifecycle/provider。
>
> **P3 current supersession（2026-07-29）**：DM-8 的 `Latest` / `NthPrevious` /
> `Budgeted` 与 runtime-local selection 现已是 historical implementation fact；current
> contract 只保留 governing `RuntimeConfigSetup` v2 中 durable `derivedContext.nthPrevious`
> 驱动的 exact single-candidate selection，无 automatic fallback。current architecture
> 同时保留 existing active branch + lifetime-bound `RefId` 与 shared epoch backpressure；
> P5 已把 full replay/audit 迁出 online core public/runtime surface；P6 已把 orchestration
> finalization 收窄为 v2，并保留 per-role crash resume 与 atomic publication。验收记录以
> [化简计划](session-journal-recovery-and-derived-memory-simplification-plan.md)为准。

> **EADR V4 target supersession（2026-07-30）**：current implementation 仍是上述 P1～P6 /
> DM-8 baseline，但下一代不再建设 immutable transaction workflow。目标拆为
> `SessionJournal.DerivedRecap.Store`、`SessionJournal.DerivedRecap.Planner` 与
> `SessionJournal.DerivedRecap.Maintainers`。Recap 是常驻、有限、替代 cold prefix 的前情提要；
> Memory 保留给未来动态召回与图查询。V4 以 event-addressed Published directory保留“不跳过、
> 不重编号”的 strict ordinal，以 per-block rolling checkpoint支持落后 cursor catch-up；目标术语
> 与规则以上述 concepts/target 文档为准。

## 1. 文档定位

本文记录如何建立以 `Atelia.SessionJournal` 为 raw correctness core 的新一代会话系统：
不可变事件源、可重建派生记忆、精确上下文规划与可恢复执行。它是
`Atelia.SessionJournal` 及其 companion projects 的现行架构路线图，而不是旧
`Atelia.ChatSession` 的升级计划。

旧 ChatSession 已冻结，只承担三种角色：

- 作为早期“StateJournal 当前工作态 + 定期压缩”方案的历史背景；
- 作为一次性、只读的数据迁移源，由 `ChatSession.LegacyExportCli` 导出；
- 在完成迁移验证后归档。

新功能、新 wire contract 和新架构只在 SessionJournal family 及其附属项目中实施。两代系统之间
不双写、不共享 mutable state，也不把新功能回流到旧项目；迁移方向始终是
`ChatSession -> legacy JSON -> SessionJournal`。

它回答：

- 哪些数据是长期事实源。
- Recap、Autobiography、World Understanding 等内容应如何建模。
- 每次 completion 的上下文如何动态选择并精确复现。
- tool-loop 如何逐步持久化并在崩溃后恢复。
- 全文、向量和图召回应放在哪一层。
- SessionJournal family 中各程序集分别拥有哪一层能力。
- 如何分阶段完成 current interim 到 target architecture 的切换。

本文不是每种 payload 的最终 wire spec，也不要求单个后续会话建成完整 Memory OS。后续会话应从本文的阶段列表领取一个垂直切片，产出更窄的 Decision/Spec/实现与测试。

## 2. 建立新系统所解决的问题

旧 ChatSession 曾以 StateJournal root 和 durable deque 表达会话工作态：

- 普通 turn 完成后整体 commit。
- compaction 用 Recap 替换旧消息前缀。
- ContextHeader / MemoryPack 表示当前应注入的压缩信息。
- StateJournal commit history 可以追溯旧状态，但它不是领域事件源。

这条路径已经验证了会话、回放、legacy recovery 与 Rewrite maintainer，但对长期自主 Agent 有四个结构性限制：

1. 原始体验与当前投影混在同一工作态中；compaction 的自然操作是改写当前 deque。
2. tool-loop 只在整轮结束后提交，中途崩溃无法精确判断 completion 或工具执行到了哪一步。
3. Recap、自传、世界理解等派生解释被当作“当前文本块”，缺少统一 provenance 与版本 lineage。
4. 上下文主要由固定 recent window 构造，难以在不同 artifact anchor、raw suffix 和动态召回之间做预算化选择。

SessionJournal 不在旧 deque 旁边补 raw log，而是从新的 storage/wire boundary 建立事实源、解释层、
投影层和执行状态。旧实现只帮助识别需求和提供迁移样本，不约束新系统的领域模型。

## 3. 核心决策

### decision [S-SJ-LEGACY-FROZEN] 旧 ChatSession 冻结

旧 ChatSession 不接受 SessionJournal 新能力、不参与双写，也不作为新 host 的依赖。允许的维护仅限于
保持 legacy export 可用和修复阻断迁移的缺陷；迁移完成后整体归档。

### decision [S-SJ-NEW-WORK-OWNERSHIP] 新工作归属 SessionJournal family

raw event、recovery contract 与 request execution 属于 `Atelia.SessionJournal`；EADR V4 concrete
recap maintainers 属于 `Atelia.SessionJournal.DerivedRecap.Maintainers`；派生文件与 point lookup
属于 `Atelia.SessionJournal.DerivedRecap.Store`，PlannerConfig、raw-growth scheduling 和
Maintain/Inherit orchestration 属于 `Atelia.SessionJournal.DerivedRecap.Planner`；CLI/Agent Host
作为 composition root 组合这些能力。current `Atelia.SessionJournal.DerivedMemory` 与
`Atelia.SessionJournal.Maintainers` 仍是待替换 baseline。

### decision [S-SJ-MIGRATION-ONE-WAY] Legacy migration 单向且经中立交换格式

`ChatSession.LegacyExportCli` 只读取旧 repo 并导出 JSON/Markdown。`SessionJournal.Cli` 通过自身的
anti-corruption DTO 导入 JSON，不引用 ChatSession 产品程序集。导入不会建立两代 repo 之间的运行时
同步关系。

### decision [S-SJ-RAW-EVENTS-AUTHORITATIVE] Raw Events 是长期事实源

Agent 实际接收、生成和执行过的内容，以不可变 raw events 保存。compaction、摘要更新或上下文切换不得删除或改写 raw events。

### decision [S-SJ-ARTIFACTS-DERIVED] Recap 与 Memory read models 都是派生解释

常驻 Recap、未来动态 Memory index/retrieval view 都由 raw events派生，可以替换、废弃或重算，
但不能冒充原始体验。EADR V4 只负责用有限 Recap近似 cold prefix；向量召回与多跳图查询属于未来
Memory layer。

### decision [S-SJ-PROJECTION-NOT-SSOT] Context projection 不是 SSOT

“本轮上下文需要的有序文本块投影”是有价值的，但它不是长期记忆的唯一事实源。current
`MemoryPack*` 是这一角色的现行实现；EADR R0 将其收窄为 `ContextHeader*`。该 projection 可由
选定的 `DerivedRecapSet`、future Memory retrieval results 与其他固定配置共同 materialize。

### decision [S-SJ-CONTEXT-PLAN-PERSISTED] 实际上下文选择必须持久化

每次 completion 前，系统必须保存精确 `ContextPlan` 和 canonical request manifest。崩溃恢复不能仅凭“当前配置 + 当前 head”重新运行 planner，因为配置、索引和 token estimator 可能已经变化。

这条 decision 描述 target contract。canonical request manifest 对 raw facts 采用 exact
address/range/setup refs，对实际进入 provider
request 的 Recap / future Memory context contribution 则保存 exact context snapshot 或 canonical
request bytes。
Prepared 不引用 derived artifact/set id，也不要求 derived store 在 reopen 时仍存在；planner/renderer
版本变化不能改写已经 Prepared 的外部调用事实。若要审计 derived selection，可在可重建 usage index 中
记录 `preparedAddress -> selected source provenance`，例如 Recap 的
`(RefId, SetAdmissionAnchor, publication envelope token)`；这不是新的 correctness identity。
current Prepared v5 已采用 exact context snapshots 与 raw provenance，不再含 raw
activation/artifact identity。

### decision [S-SJ-EXECUTION-INCREMENTAL] 执行状态逐步事件化

Observation、completion request、agent action、tool intent 和 tool result 必须在各自边界逐步持久化。
turn completion 若可由这些 raw facts 唯一确定，则保持为派生状态；只有出现不可推导的额外领域承诺时
才增加显式事件。不能继续把整个 tool-loop 当成一个只在末尾 commit 的内存事务。

### decision [S-SJ-INDEXES-REBUILDABLE] Retrieval Index 不进入正确性核心

全文、向量、实体图、时间索引和统计 read model 必须能从 raw events / artifacts 重建。索引损坏或丢失会降低召回能力，但不得改变历史事实或使 session 无法恢复。

## 4. 五层架构

```mermaid
flowchart TD
    A[Raw Event Journal] --> B[Derived Recap Sidecar]
    A --> C[Memory Retrieval Read Models]
    B --> C
    A --> D[Context Selection / Request Materialization]
    B --> D
    C -. future retrieval policy .-> D
    D --> E[Recoverable Execution State Machine]
    E --> A
```

五层职责如下：

| 层 | Canonical Data | 主要职责 |
|:---|:---------------|:---------|
| Raw Event Journal | 不可变 session events | 保存发生过什么、维持版本树与回放顺序 |
| Derived Recap Sidecar | current P6 使用 epoch/artifact/set records；EADR V4 使用 event-addressed Building/Published Recap directories | 用有限、常驻 Recap近似 cold prefix，并保存 coherent online membership |
| Memory Retrieval Read Models | 可重建索引 | 未来按语义、实体、时间、向量或图查询发现本次相关材料 |
| Context Selection / Request Materialization | Prepared 中的 exact request materialization | 选择 Recap 与 future retrieved Memory，拼接 dependency-closed raw suffix，并执行 final hard guard |
| Execution State Machine | 逐步执行事件 | 驱动 completion/tool-loop，并从任意持久边界恢复 |

DerivedRecap Store 可以物理附着在 SessionJournal repo 下，也可以使用独立 store，但不能把
derived plans/sets 写入 raw Parent sequence；逻辑上的权威边界不能因为物理共置而消失。
retrieval/model-assisted selection 仍是 future capability；接入时必须先定义 durable Agent policy 与
Prepared audit contract，不能借宽泛的 planner 名称把 automatic budget fallback 预设为目标。

### 4.1 Ownership 与依赖方向

| 项目 / 程序集 | Ownership | 依赖约束 |
|:---------------|:----------|:---------|
| `Atelia.SessionJournal` | raw event codec、tail recovery、request preparation/execution，以及 store-neutral context contracts | raw core 不引用 concrete Recap/Memory implementation |
| `Atelia.SessionJournal.DerivedRecap.Maintainers` | concrete recap maintainers、profiles、prompts 与 target paths | 单向依赖 SessionJournal contracts；future Memory有独立 components |
| `Atelia.SessionJournal.DerivedRecap.Store` | event-addressed Building/Published directories、point validation、strict descriptor 与 structural defects | 不拥有 PlannerConfig、Maintainer 或 restore orchestration |
| `Atelia.SessionJournal.DerivedRecap.Planner` | RecapPlannerConfig、raw-growth trigger、Maintain/Inherit、rolling catch-up 与 Resume/Restore | 依赖 Store 与 SessionJournal contracts；只接收注入的 `IRecapBlockMaintainer` |
| `SessionJournal.Cli` / Agent Host | composition root、迁移导入、离线开发运行、provider/tool 注入 | 可以同时引用上述项目，但不把应用 policy 推回 raw core |
| `ChatSession.LegacyExportCli` | 旧 ChatSession 数据的 JSON/Markdown 出口 | 只依赖旧 ChatSession；不依赖 SessionJournal，也不承担新功能 |

目标依赖图如下：

```text
SessionJournal.Cli / Agent Host
├── Atelia.SessionJournal
├── Atelia.SessionJournal.DerivedRecap.Maintainers ──> Atelia.SessionJournal
├── Atelia.SessionJournal.DerivedRecap.Store ────────> Atelia.SessionJournal
└── Atelia.SessionJournal.DerivedRecap.Planner ──────> Store + Atelia.SessionJournal

ChatSession.LegacyExportCli ──> Atelia.ChatSession   # frozen migration island
```

current trunk 已达到 raw core 不反向引用 concrete DerivedMemory/Maintainers 的主依赖方向；上图是
EADR V4 target，current trunk 仍使用 `Atelia.SessionJournal.DerivedMemory` 与
`Atelia.SessionJournal.Maintainers`。R0 将 current MemoryPack projection收窄为 ContextHeader
contract，并把 recap-specific maintainer一次性改名；raw event inventory仍不含 derived activation。

## 5. Raw Event Journal

### 5.1 角色

Raw Event Journal 保存“Agent 生命周期中确实发生过的事情”，包括外部输入、模型输出、工具交互和控制状态。它是审计、回放、迁移和所有派生分析的根。

EventJournal 负责 frame、`Parent` 与 opaque kind；`Atelia.SessionJournal` 解释
`EventJournal` header 中的 numeric kind，并以 canonical JSON `{ "v": ..., "body": ... }`
编码每种 kind 自己的 versioned payload。`EventAddress` 提供 store 内身份和 Parent 顺序，不以时间戳
或另造 ordinal 冒充因果顺序。

### 5.2 Current wire baseline

current trunk 的 schema 是 `atelia.session-journal.trunk.v1`。已实现的
`SessionEventKind` 如下；名称是 C# contract，持久化判别器是 EventJournal header 中的整数值：

| Kind | 值 | Current 语义 |
|:-----|---:|:-------------|
| `RuntimeConfigSetup` | 1 | model、completion surface 与 session schema 从此边界起生效 |
| `SystemPromptSetup` | 2 | system prompt snapshot 从此边界起生效 |
| `SessionCreated` | 3 | session 初始化完成 marker |
| `ObservationAccepted` | 4 | 外部 observation 已持久化 |
| `AgentActionProduced` | 5 | online completion response 已规范化并持久化 |
| `ToolExecutionStarted` | 6 | tool side effect 前的 durable intent / execution identity |
| `ToolResultObserved` | 7 | 与 execution 对应的 tool result 已持久化 |
| `CompletionRequestPrepared` | 8 | canonical request manifest 与 durable request origin 已持久化 |
| `CompletionAttemptFailed` | 9 | 已知 provider/host failure 已持久化 |
| `ImportedAgentAction` | 10 | 迁移导入的 action；不伪装成 online provider completion |
| — | 11 | 已退役；曾用于 opaque `CompletionAttemptRestarted`，不得复用 |
| — | 12 | 已退役；曾用于 interim `ArtifactSetCommitted`，不得复用 |
| `CompletionAttemptStarted` | 13 | provider dispatch claim；event address 是 attempt identity |

每种 payload 版本独立推进；不能把上表的 header kind 数值、payload `v` 和 repository schema 混为一个
版本号。codec 只接受它明确支持的 kind/version，未知 kind 和不受支持的 payload version 必须失败，
不得静默降级。

### 5.3 Future semantic boundaries

下列边界仍是路线图语义，不应被描述为已实现的 event kind：

- `ToolExecutionUncertain`：无法证明副作用是否发生，需要查询、人工处理或 capability-specific recovery。
- 显式 `TurnCompleted`、`TurnFailed`、`TurnPaused`：当仅靠 current tail phase 不足以表达产品语义时再引入。
- 独立 `ContextPlanCommitted`：只有需要把 planning 与 Prepared 分成两个 durable boundary 时才引入；
  current 将 selection plan 包含在 `CompletionRequestPrepared` 中。
- provider retry / rejection 的更细分类：应由明确 recovery contract 驱动，不提前枚举空事件。

### 5.4 Raw 与 operational event 的边界

“Raw”不是只指用户和 assistant 可见文本。为了恢复真实执行过程，以下内容也属于事实：

- 模型返回了哪些 tool calls。
- 哪个调用已开始执行。
- 工具返回了什么，或为何状态不确定。
- 哪次 completion attempt 失败、重试或被放弃。

provider-native 临时字段不应未经筛选整包回写。Canonical event schema 只保存后续回放、审计和恢复真正需要的信息；需要 HTTP 法证时可另存 provider call log。

### 5.5 分支、rewind 与替代未来

EventJournal 的 Parent lineage 和 repository/branch primitives 为未来 rewind、reroll 与替代未来提供
底层能力，但 current SessionJournal 尚未交付完整 branch UX。目标语义是：

- 正常会话沿选定 branch/ref 追加；
- rewind 不删除 event，而是移动 ref 或从历史 event 创建新 branch；
- reroll 从同一 durable boundary 附近产生替代 action 分支；
- 被放弃的未来仍可通过明确的 retention/reflog policy 查阅；
- 上层 UI 区分“当前有效父链”和“曾发生但当前不可达的旁支”。

这些语义需要后续单独定义 branch identity、ref ownership、retention 和 UI contract；不能仅因底层已有
EventJournal branch primitive 就宣称产品能力已经实现。

## 6. Derived Recap Sidecar 与 future Memory

### 6.1 跨代稳定概念

以下常驻 cold-prefix approximations 在 EADR V4 中统一称为 Recap：

- Recap / rolling summary。
- First-Person Autobiography。
- World Understanding。
- Scene / episode summary。
- Relationship state。
- World facts / known unknowns。
- Open threads / promises / unresolved hooks。
- Continuity、style 或 identity constraints。

Recap blocks 的正文可以是 Markdown、JSON 或其他受限 payload。跨代稳定的是 raw authority、
可重建性和 coherent request projection，不是 current P6 的 artifact identity/provenance schema。
query-dependent vector/graph/entity recall 是未来 Memory能力，不进入 Recap set。EADR V4 正式词汇见
[核心概念](event-addressed-derived-recap-concepts.md)。

它们不是 raw experience，也不属于旧 ChatSession。current ownership 是独立、可替换的
`Atelia.SessionJournal.DerivedMemory`；该 subsystem 可以删除并由 raw SessionJournal 重建。

### 6.2 Historical interim baseline（已由 DM-3B 取代）

以下记录 CS-5-lite/CS-3D5 当时的过渡实现，不描述 current trunk。DM-3B 已将 store/set/provider
搬入 `Atelia.SessionJournal.DerivedMemory`，删除 raw activation writer/manual checkpoint command，
DM-3B/DM-2 阶段的 Prepared v4 也不再引用 derived identity：

- `DerivedRecapStore`：位于 `Atelia.SessionJournal` core 内，使用 session repo 下的
  `derived/recaps/v1/` sidecar 保存可删除、可重建的 recap artifacts/indexes；
- addressed replay：MemoryMaintainer runner 从 `ReplayHistory()` 取得 raw provenance，以实际吸收
  fragment 的末事件形成 `anchorRawEvent`；
- `Atelia.SessionJournal.Maintainers`：保存 concrete `RewriteMemoryBlockMaintainer` profiles，不被
  raw core 反向引用；
- `SessionJournal.Cli run-memory-maintainer`：离线运行 maintainer 并发布 derived artifact；
- `SessionJournal.Cli checkpoint-artifact-set`：由开发者手动选择 exact members，在 raw chain
  追加 interim `ArtifactSetCommitted`；
- coherent request/recovery：当时的 `CompletionRequestPrepared` v3 引用 exact
  `ArtifactSetCommitted`，并内联 selected artifact context snapshots，使 sidecar 删除后仍可重建
  canonical request。

这条路径证明了 artifact persistence、provenance anchor、tail-only projection、coherent request 和
Prepared/attempt recovery，但它不是最终程序集或 authority 边界。尤其是 `DerivedRecapStore` 位于
core、raw `ArtifactSetCommitted` 引用 derived ids、manual checkpoint，以及 Prepared 仍引用 raw
activation，都是已知 interim coupling。

### 6.3 Current P6 Artifact 字段（V4 不再作为 target）

current P6 DerivedMemory schema 覆盖以下 provenance/lifecycle 信息；它们记录现行实现，不是 EADR
V4 必须保留的 target fields：

| 字段 | 含义 |
|:-----|:-----|
| `ArtifactKind` | 稳定 kind，例如 `autobiography` |
| `ProfileId` | 具体维护 profile / policy id |
| `Producer` | analyzer / model / code version |
| `ProducerFingerprint` | prompt、model、关键配置与 codec 的稳定 fingerprint |
| `SourceRawHead` | 生成时观察到的 raw branch head |
| `SourceRanges` | 实际吸收的 raw EventAddress 区间或集合 |
| `AnchorRawEvent` | 可作为后续 raw suffix 起点的边界事件；CS-5-lite 可先用 rolling summary 覆盖到的最后一个 raw event |
| `GoverningRuntimeConfigSetup` | 生成时按 `SourceRawHead` 解析到的 runtime-config-setup 地址 |
| `GoverningSystemPromptSetup` | 生成时按 `SourceRawHead` 解析到的 system-prompt-setup 地址 |
| `InputArtifacts` | 本次读取的旧 artifact 地址 |
| `PreviousArtifact` | 同 lineage 的上一版，可空 |
| `Content` | 不透明 artifact body |
| `Invocation` | 可选 completion / analyzer audit 摘要 |
| `Status` | produced / rejected / superseded 等领域状态 |

current P6 Artifact 本身 append-only，最新版由 lineage、ArtifactSet 或可重建索引确定。EADR V4
改用 `SetAdmissionAnchor + per-block AbsorbedThrough` 与 Published directory membership，不保留
`PreviousArtifact`、producer fingerprint 或 content-addressed identity 作为长期 target。

### 6.4 Current P6 audit provenance（V4 非目标）

current P6 artifact 可以回答：

1. 它由哪个 profile 和 producer 生成。
2. 它读取了哪个 raw head、哪些 raw ranges。
3. 它基于哪一版旧 artifact。
4. prompt/model/config 是否与当前版本相同。
5. 它是否已被后续 artifact 取代。

这使 current analyzer 升级后可以审计并比较解释。V4 主动降低这项 audit 承诺：Store 只保证
admission/cursor 如实、checksum/shape 可验证、Prepared exact bytes 不变；prompt/model/invocation
审计若未来需要，应作为独立能力重新设计。

### 6.5 Current P6 Coherent Artifact Set（V4 将替换）

Autobiography 与 World Understanding 可能并行生成，但一次上下文不应偶然混用不同 source head 的半套结果。

ArtifactSet 已作为 derived subsystem 内的 immutable record：

- 引用一组完整 exact artifact members；
- 引用一个在 producer 调用前已持久化的 shared coverage epoch；同一 coherence group 的 required
  members 必须共享 exact `epochId` 与 raw range；
- 记录 common anchor、source raw provenance、coverage setup refs、set policy/producer fingerprint；
- 维护自身 previous-set lineage 与可重建 latest/default indexes；
- 只有 derived publication 成功后，Context Selection / Request Materialization 才能把整组用于
  exact ordinal request。

若其中一个 maintainer 失败，旧 active set 保持可用；已成功写出的单个 artifact 可以留作诊断或未来复用，但不自动进入当前 coherent set。

history 分块是 maintainer 之前的公共 planning 结果。DerivedMemory subsystem 已保存 versioned planner
config 和 immutable epoch ledger：config 记录 token estimator、最小 recent suffix、触发阈值与
dependency-safe boundary policy；epoch record 固定实际 raw range/anchor/setup/config fingerprint。
日常运行并行启动同 epoch 的 maintainers，prompt-tuning 可针对已持久化 epoch 独立重跑其中一个 role，
而不重新计算 split。同步来自 shared epoch identity，而不是进程启动时间或恰好相同的
`--threshold-tokens`。

EADR V4 保留“一组 Recap blocks 必须整体进入 online”的产品性质，但以 frozen Building、
single rolling checkpoint 和原子 directory promotion 实现。strict ordinal计数 Published
directories，不按当前 materializability重编号；current `PreviousSetId`/latest index不再是目标。

### 6.6 Historical interim-to-current migration（DM-0～DM-8 已完成）

下表记录已经完成的 authority/assembly 迁移，不是 future backlog：

| 关注点 | Historical interim | Current |
|:-------|:-------------------|:--------|
| Derived store | `DerivedRecapStore` 在 SessionJournal core，repo-local sidecar | 独立 DerivedMemory assembly/repository |
| Coverage 协调 | 每个 runner 独立 split，人工组合 | shared immutable epoch 先于 maintainers 持久化 |
| Set publication | CLI 手动 checkpoint；raw kind 12 `ArtifactSetCommitted` 激活 | DerivedMemory 内 immutable ArtifactSet publication/index |
| 引用方向 | raw activation 和 Prepared 含 artifact/set identity | 只允许 derived -> raw address/range/setup refs |
| Prepared recovery | v3 内联 contribution snapshot，但仍引用 raw activation | Prepared v5 self-contained：exact context snapshot + raw provenance，不读取 DerivedMemory |
| Composition | SessionJournal engine 直接打开 sidecar | Host/CLI 注入 store-neutral candidate provider/lifecycle |

current raw SessionJournal 不追加 `ArtifactSetCommitted` 或其他 derived-set activation，也不引用
artifact/set/epoch id。某次 completion 实际使用的 derived memory 必须在
`CompletionRequestPrepared` 中以 exact context snapshot 或 canonical request bytes 提升为 execution
fact；exact reopen 不打开 DerivedMemory，也不重新运行 planner。用于审计 selection 的
`preparedAddress -> selected source provenance` 记录属于可重建 derived usage index；Recap
provenance 使用 `(RefId, SetAdmissionAnchor, publication envelope token)`，不引入 SetId。

该迁移已按专门实施计划完成：先建立 cross-assembly neutral contracts 和 neutral request
materialization，再切换 self-contained Prepared，之后移动 concrete store、删除 raw
`ArtifactSetCommitted`，最后引入 shared epoch、并行 orchestration 与 online selection。这一顺序
避免了 raw core 反向依赖 concrete DerivedMemory。

`SessionExecutionTailResolver` 始终 raw-only；DerivedMemory 缺失只阻止尚未 Prepared 的 context
planning，不应破坏已 Prepared request 的恢复。

### 6.7 Current MemoryPack role 与 EADR cutover

`MemoryPack` 变为 materialized context view：

```text
selected ArtifactSet
+ pinned/manual context blocks
+ rendering profile
-> MemoryPack
-> ContextHeader projection
```

`RewriteMemoryBlockMaintainer` 已作为首批 artifact producer 使用：输入旧 artifact + raw range，输出
完整新 artifact。CS-5-lite 与后续 current trunk 已验证 recap 可以 materialize 为
`ContextHeader` 形态的 observation/action header，并以真实 anchor 之后的 raw suffix 保留近期细节。
full replay 只用于显式 offline audit；maintainer 输入使用 addressed bounded planning window。
current 与 V4 online 没有 coherent candidate 时都保持 not-ready，不允许静默 full-raw fallback。DM-8
current empty-lineage bootstrap 已受 native raw fresh-genesis topology 约束，而不是 normal
fallback。后续工作是在保持这一
raw/artifact 语义的前提下，按
[EADR V4 target](event-addressed-derived-recap-v4-target-design.md)
替换 current DerivedMemory，而不是把能力回迁到 ChatSession。

EADR R0 不保留宽泛 Memory naming：recap maintenance 改为 `IRecapBlockMaintainer`，
`MemoryPack*` 这组实际只负责 ContextHeader projection 的类型改为 `ContextHeader*`。未来动态
Memory retrieval仍可产出 neutral context contributions，但不进入 `DerivedRecapSet`。

## 7. Context Selection（Current P3/P4）

### 7.1 Current 边界：durable exact ordinal

current online request path 只接受 host 注入的 coherent candidate source；core 通过 bounded
two-phase selection/materialization 按 governing `RuntimeConfigSetup` v2 的
`derivedContext.nthPrevious` 选择一个 exact candidate，并把它与 dependency-closed raw suffix
物化为 request。`n = 0` 等价于 latest，但 production contract 不再保留 `Latest` mode。
provider 只返回 `Selected`、`EmptyLineage` 或 `OrdinalUnavailable` 与至多一个 descriptor；
lineage link 缺失/损坏或 ordinal 越界都不跳过、不重编号。

current 已删除 raw/bootstrap/total token budgets。可选
`SessionRuntime.MaximumCanonicalRequestBytes` 只测量最终 canonical request JSON 的精确 UTF-8
byte length；超限即 not-ready，绝不自动改选另一个 set。它不是 provider tokenizer 或 model
context-window 保证。current 不会静默退回无界 full raw，retrieval 与多
coherence-group 组合仍未实现。

current 内部 `SessionContextPlan` 描述 Prepared v5 的单一 coherent recipe：raw start、raw range
hash、exact context snapshots 与 paired setup refs，不含 raw `ActiveArtifactSet` reference。
Prepared 是 execution fact；ordinal policy 已是 raw governing setup 的 durable contract，而
provider/domain 仍由 host provision。

### 7.2 Current exact ordinal 与 strict bootstrap invariants

P3 已删除 automatic budget candidate planner。Agent 通过 governing
`RuntimeConfigSetup` 持久化 `nthPrevious`：`0` 选择 selected branch 当前 published coherent set
lineage 的最新 set，`n` 选择沿 `PreviousSetId` 向前的精确第 n 个 set。tail-only projection 的边界
仍来自该 set 的 raw anchor；更旧 set 自然产生更长 recent raw suffix。

current exact selection 与 strict bootstrap 必须满足：

- **current**：ordinal 是唯一 Agent-controlled durable source；host/runtime 不再注入第二份选择；
- **current**：exact nth link 缺失、损坏、越界或 raw anchor 无法验证时显式 not-ready，不跳过、不重编号；
- **current**：provider 保留 descriptor/materialization 两阶段，但只服务 bounded IO 与 raw authority proof，
  不返回一组 candidates 供 core 自动试选；
- **current**：strict bootstrap 只识别 first-online-request 的 native fresh-genesis topology，不证明“从未发布 set”：
  provider 必须报告 healthy empty lineage，raw ancestry 必须无 Prepared；raw tail 可以处于
  `SessionCreated` 后只有 governing setup 更新的 pre-append boundary，也可以在该 fresh predecessor
  后恰有一个 active first `ObservationAccepted` 的 exact/recovery boundary。后者覆盖 `Send` append
  observation 后、Prepared 前的第二阶段与 crash/reopen；该 observation 后不得再有
  history/execution-bearing fact，也不允许把任意历史 observation 当成 fresh；
- **current**：fresh genesis 上未被 Prepared 使用的 derived set 即使曾发布后又删空，仍允许 bootstrap，不影响
  raw correctness；imported/legacy non-genesis history 不允许 bootstrap，但 offline maintainers /
  rebuild 可以在首个 Prepared 前发布 set，online 再按 exact ordinal 使用；
- **current**：selected exact request 超过 hard guard 时 fail-fast，不自动换另一个 set；
- **current**：Prepared v5 固定实际 request；之后 setup、latest pointer 或 DerivedMemory 变化都不触发重选。

EADR V4 保留相同的 Agent-visible strict ordinal，但替换实现机制：

- current P6 沿 immutable `PreviousSetId` lineage 解释 ordinal；
- V4 从 exact completion boundary 沿 raw Parent lineage point lookup event-addressed Published
  Recap directories；
- V4 计数 Published membership，而不是当前可 materialize payload；exact set损坏时
  Restore/not-ready，不跳过、不重编号；
- V4 Building 与 block-local rolling checkpoint不进入 ordinal；
- `SetAdmissionAnchor` 与 per-block `AbsorbedThrough` 分离，允许 block 暂缓后从真实旧 cursor
  bounded catch up。

若未来引入 retrieval、多 selection domain 或模型辅助选择，必须另立 durable Agent policy 与
Prepared audit contract；它们不是恢复 historical `Budgeted` mode 的理由。

### 7.3 Historical：Prepared v3 基线与 self-contained Prepared v4 目标

DM-2 之前的 `CompletionRequestPrepared` v3 已内联 artifact contribution snapshots、governing setup
references、tool definitions/runtime identity、request target 与 canonical request commitment，能够在
derived sidecar 删除后重建已 Prepared request。但 `SessionContextPlan.ArtifactInputs` 仍带 exact
artifact ids，并保存 raw `ActiveArtifactSet` reference；reconstructor 仍需沿 raw activation 验证
coherence。这是当时的 interim wire，不是 current 依赖方向。

DM-2 当时的 target 是 self-contained Prepared v4；DM-8 后 current wire 已进一步升级为
Prepared v5，并能 self-contain zero-input bootstrap request。该 wire 表达能力与 §7.2 的
fresh-genesis topology eligibility 是两个独立问题：

- 固定可逐字节重建 canonical `CompletionRequest` 的 exact context / manifest。
- 保存必要的 raw range、setup、tool schema/config provenance 与 hash。
- 固定 renderer、serializer、prompt template、tool rendering、model/connection identity 和 request
  commitment。
- 保存 durable request origin；attempt identity 继续由后续 `CompletionAttemptStarted` event address
  表达。
- 不保存 derived artifact/set/epoch id，不以 DerivedMemory 或 raw activation 作为 reopen 依赖。

raw suffix 可用 exact address/range/hash 与 deterministic fold 重建；derived contribution 则必须在
Prepared 中提升为 exact snapshot 或 canonical bytes。恢复时不得重新打开 DerivedMemory、重新运行
planner，或用“当前最新配置”替换已经 Prepared 的内容。

canonical request manifest 是崩溃恢复和重发的 Canonical Source。ContextPlan/selection record 只负责
解释“为何选择这些材料”；需要关联具体 derived candidate 时，由可重建的 derived usage index 以
`preparedAddress -> selected source provenance` 单向引用 raw Prepared；该 provenance 是审计信息，
不是 reopen dependency 或新的 derived identity。

### 7.4 Scheduling threshold 与 request hard guard

P4 明确区分两条职责：

- current DerivedMemory epoch planner、未来 V4 Planner 的 thresholds 都只决定何时维护与何时
  backpressure；
- online request hard guard 只检查 Agent 已选择的 exact ordinal request 是否可物理提交，超限即
  fail-fast，不参与 candidate selection。

Historical CS-5-lite 曾由
`SessionJournal.Cli run-memory-maintainer --threshold-tokens` 在 runner 内执行 synthetic split；该
模式已经删除。current `run-memory-maintainer --epoch <epoch-id>` 只执行 DM-5 planner 预先持久化的
exact epoch，不拥有 split policy。current DerivedMemory epoch planner 统一决定：

- `minimumRecentTokens`：始终保留的最新 dependency-closed suffix。
- `epochTriggerTokens`：eligible prefix 达到多少后创建新 epoch。
- token estimator、dependency boundary policy、headroom 与 hard limit。
- immutable epoch plan 的实际 raw range；boundary alignment 可使每个 epoch 大小不同。

同一 coherence group 的 maintainers 消费同一 epoch plan。针对某个 role 做 prompt tuning 时，只替换
该 epoch 的 producer candidate，不重新切分 history。

上述 epoch 是 current P6 实现。V4 不保留 shared epoch identity；它在 building manifest 中冻结公共
`SetAdmissionAnchor`、per-block source cursor、bounded catch-up route 与 prior-context snapshot。
多个 block 可以从不同 cursor 出发，但只发布一个 final common set。

current 已删除 `RawSuffixTokenBudget`、`TotalContextTokenBudget`、
`BootstrapRawSuffixTokenBudget` 与 `SessionRuntime.ContextBudgets`。唯一可选 guard 是
`MaximumCanonicalRequestBytes`，使用 canonical JSON 的精确 UTF-8 byte length；它不参与
selection/fallback，也不等于真实 model tokenizer 或 provider context window。planner 的
message-token estimator 只服务 durable epoch scheduling/backpressure，未与 request guard 合并。

### 7.5 选择结果也是事实

Retrieval index 可以重建，查询结果却可能随模型、索引版本或时间变化。未来凡实际进入 completion
request 的 recalled item，都必须被 Prepared 的 exact request materialization 固定，并由
ContextPlan/selection record 留下解释。这样既不把可删索引升级为事实源，也能回答模型当时实际看到了
什么。

## 8. 可恢复 Execution State Machine

### 8.1 Current 已实施骨架

current raw protocol 已包含：

- `CompletionRequestPrepared` v5：保存 durable request origin、exact context/setup/tool facts 与
  可逐字节重建的 canonical request commitment。
- `CompletionAttemptStarted`：严格空 body，event address 是物理 provider attempt identity。
- `CompletionAttemptFailed`：记录 provider 明确 non-success 或 host-known rejection。
- `AgentActionProduced` / `ImportedAgentAction`：区分 live completion 与 legacy/manual import。
- `ToolExecutionStarted` / `ToolResultObserved`：在外部工具调用前固定 intent，调用后记录观察结果。

在线恢复走 `SessionExecutionTailResolver` 的 tail-only 路径。P5 已删除 public full
projection/replay 与 production full reducer；有价值的 raw audit/import checks 位于
`SessionJournal.Offline` companion，history/provenance caller 使用 bounded planning window。
Offline audit 从来不是 tail recovery 的运行时 fallback。current 状态流可概括为：

```mermaid
stateDiagram-v2
    [*] --> ObservationAccepted
    ObservationAccepted --> CompletionRequestPrepared
    CompletionRequestPrepared --> CompletionAttemptStarted: dispatch
    CompletionAttemptStarted --> CompletionAttemptStarted: explicit retry
    CompletionAttemptStarted --> AgentActionProduced: success
    CompletionAttemptStarted --> CompletionAttemptFailed: known non-success
    AgentActionProduced --> Idle: no tool calls (derived)
    AgentActionProduced --> ToolExecutionStarted: has pending tool call
    ToolExecutionStarted --> ToolResultObserved
    ToolResultObserved --> ToolExecutionStarted: more pending calls
    ToolResultObserved --> CompletionRequestPrepared: all calls settled
    CompletionAttemptFailed --> TurnFailed: derived phase
```

图中的 `Idle`、`TurnFailed` 是 reducer/tail resolver 推导的 phase，不是同名 raw event。current turn
completion 同样是隐式派生状态：只有 continuation completion 最终产生不含 tool call 的 Action 才进入
Idle；某个 Action 的全部 tool calls 已结算只形成下一次 completion 的 continuation boundary。
不存在 `TurnCompleted` event。

### 8.2 Current Completion 恢复语义

崩溃窗口按 Prepared 与 Started 分开：

- Prepared 前崩溃：尚无 committed request，可重新进入 planning。
- Prepared 已提交、Started 前崩溃：phase 为 `AwaitingCompletionDispatch`；显式 Resume 从 Prepared
  重建并验证同一 request，先追加 Started，再调用 provider。
- Started 已提交、Action/Failed 前崩溃：phase 为 `AwaitingCompletion`，物理调用 outcome uncertain。

Started 以 Parent 串联 source Prepared 与后续 retry；Prepared 始终是 request 唯一真源。current 默认
`Refuse` 不会在 uncertain Started 上发起新的外部调用。只有调用方显式选择
`RestartWithNewAttempt`，系统才追加新的 Started 并执行 at-least-once retry；新 completion 不会被
伪装成旧 attempt 的响应。显式 retry 当前还要求调用方独占 branch driver；CAS 只能保护 journal
attachment，不能撤回并发发出的 provider request。

current 尚未实现 provider capability discovery、按 provider handle/result lookup、reconcile 或
跨进程 lease/single-flight。因此“支持幂等 key 的 provider 可自动安全恢复”仍是 future hardening，
不能据现有 Started 链宣称已经具备。

### 8.3 Current Tool 协议与 identity

current 工具路径已经采用 started/result 两阶段协议：

1. `ToolExecutionStarted` 在外部调用前保存 tool call、validated arguments、reserved
   execution sequence、deterministic operation id 和固定的 tool runtime identity。
2. host 使用同一 operation id / execution sequence 执行工具。
3. 已知结果写成 `ToolResultObserved`；多个结果按原 Action 中 tool-call 声明顺序投影。

completion correlation、attempt address、tool-call identity、reserved sequence 与 operation id 都是
durable/deterministic identity，恢复不能重新随机生成。它们解决的是“这次 intent 和观察结果属于谁”，
并不单独提供外部世界 exactly-once。

current `ResumeAsync` 遇到已提交但尚无 result 的 `ToolExecutionStarted` 时，会复用 durable
operation id / sequence 直接重执行工具并补写 `ToolResultObserved`。因此 current 自动恢复只对幂等
工具，或能按该 operation id 自行去重的工具安全；非幂等且不可查询的工具目前不得接入这条自动恢复
路径。

### 8.4 Future hardening：uncertain 与 capability-aware recovery

Journal 只能保证 intent 和观察结果可恢复，不能单独保证外部副作用 exactly-once。完整协议仍需按工具
能力补齐：

| Tool 能力 | Future 恢复策略 |
|:----------|:----------------|
| 原生 idempotency key | 复用同一 deterministic operation id 安全重试 |
| 可按 operation id 查询状态 | 先 lookup/reconcile，再补写结果或决定重试 |
| 事务性本地工具 | journal 与本地事务按专门协议协调 |
| 非幂等且不可查询 | 写入 uncertain/paused 事实，停止自动推进并请求人工或领域补偿 |

`ToolExecutionUncertain`、`TurnPaused` 及其 reducer/driver 语义目前尚未实现；provider lookup/reconcile
也尚未接入。current runtime 无法自动识别或安全处置付款、发送消息、删除资源等非幂等且不可查询的
工具；Host 必须不将其接入自动恢复路径。不能把 started/result 骨架描述成完整恢复协议，更不能在
crash 后盲目重试。

### 8.5 Turn completion 的 current 语义

current 不需要也不存在 `TurnCompleted` event。只有不含 tool call 的最终 Action 表示隐式 turn
completion；tool results 全部结算后仍需 continuation completion，不能提前判定 turn done。raw
Action、Started、Result 等事实仍逐条保留。未来只有当产品需要表达无法从 raw tail 推导的额外领域
承诺时，才应另行设计显式 completion/paused event，而不是预先把派生摘要写成第二真源。

## 9. Dynamic Retrieval（Future Read Model）

### 9.1 尚未实现的独立 Read Path

Dynamic Retrieval 当前尚未实现，也没有冻结 `IMemoryRetriever`、`IContextMemorySource` 等公共接口。
它属于 future Memory read-model layer，不属于 EADR Recap subsystem。retriever 在尚未 Prepared
的 request planning 阶段按需选择候选材料；current `IMemoryBlockMaintainer` 只是等待 R0
cutover 的 Recap maintenance baseline，future retrieval 不依赖它，也不参与已 Prepared request
的 reopen。

应先完成一个真实 backend 的端到端切片，再从使用证据中提炼公共 contract，避免先围绕假想的向量库
冻结接口。

### 9.2 候选索引方向

长期 Role-Play / Agent memory 不应押注单一索引：

- 全文 / FTS：专有名词、代码、原话、路径和精确事实。
- 向量：语义相似的经历与主题。
- 时间索引：最近、某一时期、事件区间。
- Entity / relation graph：人物、关系、承诺、项目、地点。
- Artifact lineage index：查同 kind 最新版本、source head 与 supersession。
- Open-thread index：尚未闭合的问题、计划和承诺。

未来可由多个 retriever 汇合候选，再由 ranker/planner 在预算内选择。索引只保存 raw/derived address
和可重建特征，不复制成为新的事实源；具体组合、打分与 contract 仍是开放设计。

### 9.3 Rebuild、版本与降级

未来每个 index 至少应记录：

- index schema/version。
- source raw/artifact high-watermark。
- embedding/model/tokenizer fingerprint（若适用）。
- rebuild 状态与错误。

索引缺失或落后不应破坏 raw replay、Prepared recovery 或基本审计。尚未 Prepared 的 online request
如何降级，则必须服从当时明确的 readiness policy；在 current coherent-only 基线上，最多是不使用
dynamic recall，不能借“索引降级”静默绕过 coherent ArtifactSet 要求。

## 10. Artifact Maintenance 调度

### 10.1 Current development surface：single-profile epoch runner

current `SessionJournal.Cli run-memory-maintainer` 是面向 maintainer 开发的离线 runner，而不是
DerivedMemory online scheduler：

- 每次只运行一个显式 `--profile`。
- 必须消费既有 durable epoch，不再自行 threshold/split。
- non-genesis 从 exact input set 恢复 MemoryPack，支持同 epoch 的独立 prompt tuning。
- 只写 alternative artifact candidate，不推进 shared epoch 或 latest set。

online provisioning/planning 由 DM-5～DM-8 的 lifecycle coordinator 负责，single-profile runner
只保留开发与调优价值。

### 10.2 Implemented：shared epoch 与 cursor

DerivedMemory scheduler 已先持久化 shared immutable coverage epoch，再让同一 coherence group
的 maintainers 消费同一 exact raw range。每个 artifact/role lineage 固定：

- 上一版 artifact 吸收到哪个 raw Event。
- 本轮 epoch 计划吸收哪个 raw range。
- 生成结果基于哪个 `SourceRawHead`、哪个旧 artifact 与哪版 producer。

cursor 必须在 source branch/raw Parent lineage 中解释，不是 store 级全局 ordinal。发生 rewind、
reroll 或从历史 Event 分叉后，新 branch 只能从该 lineage 可达的 artifact/cursor 起步，不能借用另
一 branch 更靠后的 cursor 跳过 raw events。

“即将滑出上下文”仍可作为触发信号，但维护完成后不删除 raw prefix，只推进 derived lineage 与
candidate publication。

### 10.3 Implemented baseline：触发、并行与结算

current scheduler 使用 planner token thresholds、scheduling headroom、hard limit 与 safe online
boundary；scene/episode、artifact age 与 profile-specific trigger 仍是未来扩展。coherent roles
共享同一 coverage epoch。

producer 可基于同一 `SourceRawHead` 并行运行。完成时：

- 结果作为带 provenance 的 candidate 保存。
- 只有满足 coherence/publication policy 的组合才能发布为 immutable ArtifactSet。
- partial failure 不破坏上一版可用 set。
- raw branch 已前进不篡改 candidate 的 source head；planner 只需在使用时追加更长 raw suffix。
- prompt-tuning 可以针对既有 epoch 重跑单个 role，不产生新的 role-local split。

host 通过 exact role executions 完成 provisioning；shared config/epoch ledger、partial-success
settlement、restart resume、atomic publication 与 online backpressure 均已实施。原功能缺口已归档为
[历史备忘](done/memory-maintainer-provisioning-planner-gap.md)。

### 10.4 EADR V4 target：per-block cursor 与 rolling catch-up

V4 以更小的 durable shape 替换 current epoch/job orchestration：

- source inputs 与 manifest 先于 Maintainer 调用，冻结 common `SetAdmissionAnchor`；
- 每个 block 保存独立 `AbsorbedThrough`，`Inherit` 不推进，`Maintain` 推进；
- 落后 block 使用 ordered endpoints 与单个 rolling checkpoint分段 catch up，中间 endpoint不成为 set；
- frozen prior context 不读取同一 building set 的 partial results；
- CanPublish 后 atomic rename `building/<anchor>` 为 `published/<anchor>`；
- Published directory固定 strict ordinal membership；payload damage由 Store defects +
  Planner bounded Restore在同一 directory恢复，不回落旧 ordinal。

具体定义以 [EADR 核心概念](event-addressed-derived-recap-concepts.md)和
[V4 target](event-addressed-derived-recap-v4-target-design.md)为准。

## 11. 项目边界与 Legacy 迁移

### 11.1 两个独立系统，不升级旧 ChatSession

新的 SessionJournal 从建立之初就是独立的 raw-event authority，不是把旧 ChatSession 的内部
StateJournal 原地改造成 EventJournal。边界原则是：

- 旧 `prototypes/ChatSession` 与其 StateJournal message deque 保持 frozen，只承担归档读取与迁移
  数据导出；不继续加入 SessionJournal execution、memory 或 planner 新功能。
- 新功能和新架构只进入 `prototypes/SessionJournal` 及其 target companion projects 与
  `SessionJournal.Cli`；current companion 名称及其 EADR replacement 见 §4.1。
- 新 SessionJournal 不读写旧 deque，不与旧 StateJournal 双写，也不把旧 store 当作自己的
  projection/cache。
- StateJournal 在其他领域仍可继续使用；这里冻结的是旧 ChatSession storage 模型，不是否定
  StateJournal 本身。

因此这里不存在“先双写、再把旧 ChatSession 的 SSOT 切到 EventJournal”的迁移期。新 session 直接在
SessionJournal 创建；旧 session 如需延续，则经过一次性、可审计的数据迁移。

### 11.2 当前 Legacy 迁移管线

当前迁移边界是显式文件协议：

```text
ChatSession.LegacyExportCli
    -> versioned legacy JSON
    -> SessionJournal.Cli anti-corruption DTO
    -> new SessionJournal repository
```

[`ChatSession.LegacyExportCli`](../../prototypes/ChatSession.LegacyExportCli/README.md) 是唯一理解旧
ChatSession storage/types 的 exporter；[`SessionJournal.Cli`](../../prototypes/SessionJournal.Cli/README.md)
只解析自己的 anti-corruption DTO，不引用 ChatSession 产品程序集。旧 repo 始终只读，导入结果写入
新的 SessionJournal repo，因而迁移失败不会把旧数据改成半升级状态。

current importer 只迁移能诚实映射为新 raw facts 的基本 observation/action/setup 历史。旧
compaction/recap 属于 derived 信息，会被跳过；包含 tool execution 或 revert-turn 的历史因缺少足够
correlation、operation/checkpoint 或 branch 语义而 fail-fast，不能伪造为新的 SessionJournal
事实。迁移是有损边界时必须显式报错的 upgrade/import，不是持续同步或兼容读取层。

### 11.3 Current baseline：Memory 代码归属与 split policy

旧 ChatSession 中曾经实现一半的 memory substrate 已拆除。新 contracts 位于 SessionJournal，
concrete `MemoryMaintainer` 位于 companion assembly `Atelia.SessionJournal.Maintainers`；能力不会回迁
到旧 ChatSession，也不以引用旧类型的方式“复用”。

旧 `HistoryWindowSplitPolicy` 属于旧 ChatSession/backtest 语境，不是新架构的可复用资产。
CS-5-lite 过渡期的 `MemoryMaintainerHistorySplitPolicy` / synthetic half-context split 也已删除。
current `SessionJournal.Cli run-memory-maintainer --epoch <epoch-id>` 只消费 durable epoch；切分责任已
归 DM-5 DerivedMemory scheduler/epoch planner。

### 11.4 Compaction 的新语义

旧 ChatSession compaction 的语义近似：

```text
messages = recap + recent suffix
```

新 SessionJournal 不执行这种 destructive history mutation。新架构中的对应能力是：

```text
raw SessionJournal events 保持不变
current P6: DerivedMemory producer 生成带 provenance 的 recap/artifacts
EADR target: DerivedRecap Maintainers 生成 Recap blocks，Store 发布 DerivedRecapSet
Context Selection / Request Materialization 按 exact ordinal 拼接 Recap + dependency-closed raw suffix
```

因此新系统中的“compaction”只能是可删除、可重建的 derived projection/maintenance，不是 raw
SessionJournal core 的写前缀、删前缀或旧 deque 升级逻辑。这一边界也使 §12 后续阶段应被理解为在新
SessionJournal 项目族中建立能力，而不是改造旧 ChatSession。

## 12. 分阶段路线图

`CS-*` 是早期实施过程中形成的阶段标识。为保持代码、测试和既有设计文档的引用连续性，本文继续
使用这些编号；它们不表示新功能属于 ChatSession，也不构成“升级旧 ChatSession”的计划。

### 12.1 已建立的 SessionJournal 基线

以下能力已经在新的 SessionJournal family 中建立，后续工作应把它们当作 current baseline，而不是
重新领取的待办：

- **CS-0 / CS-1：raw core 与 replay。** SessionJournal 已具备领域 event schema、canonical
  codec、append-only parent chain、基础 reducer/replay，以及 observation/action/setup 和
  completion/tool execution identity。EventJournal branch primitives 已存在，但完整 SessionJournal
  branch UX 仍属于后续能力。具体 current wire 以
  [SessionJournal 主干设计基线](session-journal-trunk-design.md)和代码为准。
- **CS-2：单向 legacy import。** `ChatSession.LegacyExportCli` 可把旧 repo 导出为 versioned
  JSON/Markdown，`SessionJournal.Cli import-legacy-json` 通过自身 anti-corruption DTO 创建新的
  SessionJournal repo。普通 observation/action/setup 历史可映射；旧 compaction/recap 被视为
  derived 信息而跳过，tool execution 与 `revert-turn` 因无法无损表达 current correlation、
  checkpoint 或 branch 语义而 fail-fast。该管线不修改旧 repo、不双写，详见
  [CLI 拆分与迁移边界](../ChatSession/legacy-export-and-sessionjournal-cli-split.md)。
- **CS-2.5 / CS-5-lite：derived recap 试验基线。** 已建立可删除、可重建的 sidecar recap store，
  addressed replay provenance，以及由 `SessionJournal.Cli run-memory-maintainer` 驱动 concrete
  `MemoryMaintainer` 的开发入口。它证明了 raw authority、artifact lineage 和 tail anchor；当时的
  store/split/runner 已由 current DerivedMemory/epoch runner 取代，详见
  [CS-5-lite 完成记录](done/cs-5-lite-sessionjournal-derived-recap-store.md)。
- **CS-3 / CS-3D0～D7：coherent-only request 与 tail recovery。** 该阶段实现了
  `CompletionRequestPrepared` v3、Prepared/Started attempt 对称性、exact reopen、raw-only
  `SessionExecutionTailResolver`、durable tool identity/checkpoint，以及不随冷历史线性增长的 online
  recovery。它当时仍以 raw activation 取得 coherent context；该过渡边界现已由 DM-0～DM-4
  拆除。实施事实和历史决策分别见
  [Tail-only Execution Recovery Design](tail-execution-recovery-design.md)、
  [Coherent-only Manifest 完成计划](done/coherent-request-manifest-simplification-plan.md)与
  [Prepared / Provider Attempt 对称化](done/prepared-provider-attempt-symmetry-design.md)。

这些完成记录保留历史 wire 与阶段名，用于解释 current code 为什么如此；它们不覆盖下节已经批准的
长期依赖方向。

### 12.2 已完成主路线：DM-0～DM-8

当前实施权威是
[DerivedMemory 可替换子系统与 Shared Epoch 实施方案](done/derived-memory-subsystem-implementation-plan.md)。
已按其中的依赖顺序逐片实施、审阅和提交；本文只保留路线级摘要，避免复制 exact contract 或
migration 细节。

1. **DM-0 — Cross-assembly contracts（已完成）**：在 SessionJournal contracts 中定义 store-neutral
   candidate、selection 与 materialization 边界，先固定正确的依赖方向。
2. **DM-1 — Neutral request materialization（已完成）**：让 raw core 从中立 candidate/materialized input
   构造 request，不再认识 concrete recap store shape。
3. **DM-2 — Self-contained Prepared（已完成）**：把 exact canonical context 与 raw-start setup
   provenance 固定在 Prepared 中；DM-8 current wire 为 v5，并能 self-contain zero-input
   bootstrap request。
4. **DM-3 — 独立 DerivedMemory assembly 与 provider cutover（已完成）**：建立单向依赖 SessionJournal
   contracts 的 concrete derived store/provider，并由 CLI/Host composition root 注入。
5. **DM-4 — 删除 raw activation（已完成）**：移除 raw derived-set activation 及其
   validators；raw chain 不再引用 derived artifact/set identity。
6. **DM-5 — Shared epoch planner（已完成）**：在 DerivedMemory 中持久化统一的 history epoch、计划配置和
   ledger，使多个 roles 消费同一 exact coverage boundary。
7. **DM-6 — Epoch-bound maintainer runner（已完成）**：maintainer 只执行既定 epoch；split/threshold
   ownership 从 runner 收回 planner，支持独立 prompt-tuning 与重试。
8. **DM-7 — Orchestration 与 publication（已完成）**：协调多个 producer、partial settlement 和原子
   ArtifactSet publication；只有完整 coherent set 才成为候选。
9. **DM-8 — Online lifecycle 与 selection（已完成，selection 由 P3 supersede）**：由 Host 组合
   planning、maintenance 与 provider；历史实现曾支持 latest/Nth/budgeted。current P3 只保留
   durable exact nth、native fresh-genesis bootstrap、restart resume、canonical request byte guard
   与 explicit backpressure；P4 已完成 §7.2 topology 与 budget 化简。

这条路线的关键不是把文件机械搬到新项目，而是先解除 raw materialization 和 Prepared 对 concrete
derived shape 的依赖，再建立独立 DerivedMemory，最后删除 raw activation。不得为了缩短过渡期让
SessionJournal raw core 反向引用 Maintainers/DerivedMemory，也不得把 concrete derived identity
重新写进 raw wire。

### 12.3 后续能力

DM-0～DM-8 建立正确的 authority、ownership 与 online composition 后，再推进以下能力：

- **EADR V4 replacement。** 新建 DerivedRecap Store/Planner/Maintainers projects，以
  event-addressed Building/Published directories、strict ordinal、per-block cursor、rolling
  catch-up 和窄 Restore protocol替换 current P6 transaction workflow；不迁移 v2/v3 data。
- **CS-4 后续：tool capability 与 uncertain hardening。** current trunk 已有在幂等/operation-id
  去重假设下可恢复的 tool loop，以及 reserved sequence/operation identity、Started/Result 和 tail
  recovery；后续不是从零重建，而是扩展 capability declaration、provider/result lookup、reconcile
  policy，以及非幂等且不可查询工具的 paused/uncertain 人工处置。
- **Branch-aware durable ordinal selection（current 已建立，V4 保持语义）。** Engine lifetime
  绑定 selected active branch 的稳定 `RefId`，Agent 通过
  `RuntimeConfigSetup.nthPrevious` 选择 exact set；V4 改变 publication/storage mechanism，但不恢复
  automatic candidate fallback，并继续区分 planner backpressure 与 final request hard guard。
- **Retrieval read models。** 先落一个可从 raw/derived provenance 重建的真实 backend，再评估
  full-text、entity/open-thread、vector 与 graph 的组合。索引失效不得破坏基本 session recovery。
- **Branch UX 与 derived reuse。** 明确 rewind/fork 的用户模型、branch-aware candidate selection、
  跨 branch artifact reuse 与多 Parent/merge 的取舍；branch 仍由 raw parent relation 表达，而非删除
  或改写旧历史。
- **持续性的 legacy interop / 归档。** 维持旧 exporter 和迁移验证，记录 unsupported history 的
  显式限制，并在迁移完成后归档旧 ChatSession。不会把旧 ChatSession 切换为 SessionJournal 权威源，
  不建立双写，也不让新 Host 依赖旧项目。

## 13. 每个后续会话的交付模板

为避免多会话递归推进时重新扩大范围，每个任务应说明：

1. **所属阶段**：例如 `DM-2` 或后续 `CS-4` hardening。
2. **输入文档**：引用本文与更窄的 Decision/Spec。
3. **唯一核心假设**：本次改动试图验证什么。
4. **持久化边界**：哪些 bytes/events 成为新的 Canonical Source。
5. **失败矩阵**：至少覆盖写入前、写入后、flush 前、flush 后。
6. **兼容策略**：新项目早期优先彻底重构，不默认保留兼容 wrapper。
7. **可执行验收**：focused tests、reopen、replay、failpoint 或 backtest。
8. **未解决问题**：只记录，不在任务外顺手扩张。
9. **Ownership**：本次变更属于 raw core、DerivedRecap、future Memory、current baseline，还是
   CLI/Host composition；若跨层，说明最小 contract 和依赖方向。
10. **Legacy guard**：确认是否误改旧 ChatSession、引入 SessionJournal → ChatSession 产品依赖，
    或建立任何双写/持续同步路径；除修复迁移阻断缺陷外，三者都应为“否”。

推荐一次会话只闭合一个可运行垂直切片。例如
“Observation → RequestPrepared → AttemptStarted → Action → reopen replay”优于一次性创建十几个空接口。

## 14. 已解决边界与开放问题

### 14.1 已解决的边界

- SessionJournal 已拥有自己的 event schema、per-kind codec 与 canonical persistence contract；它不是
  ChatSession envelope 的新版本。
- 基础 reducer、completion attempt topology、tool sequence/operation/correlation identity 和
  tail execution recovery 已落地。
- 普通 legacy observation/action/setup 已有单向 JSON 映射；无法诚实迁移的 tool/revert history
  fail-fast，不再以“以后补 metadata”掩盖语义缺口。
- raw SessionJournal events 是唯一 correctness source；derived sidecar、indexes、recap，以及
  DerivedMemory 中的 ArtifactSet 可删除、可重建。raw inventory 不再包含 derived-set
  definition/activation。
- concrete companion 的依赖方向已经确定：`SessionJournal.Maintainers` 已单向依赖 SessionJournal
  contracts；current `SessionJournal.DerivedMemory` 同样单向依赖 raw core。EADR target 由
  `DerivedRecap.Store / Planner / Maintainers` 组成，并与 future Memory concrete
  implementations 一样保持 raw-core 单向依赖；CLI/Host 是 composition root。

### 14.2 仍开放的问题

1. Prepared v5 exact context snapshots 与 canonical request snapshot 方案的空间成本、重复数据、
   reopen reads 与敏感内容处理取舍。
2. provider-side request/result lookup 与 reconcile 的统一抽象，尤其是 crash window 中 provider
   已完成但 host 未落 durable result 的情况。
3. 非幂等、不可查询工具的最终 uncertain/paused 操作协议与人工介入 UX。
4. 第一个 retrieval backend，以及 provenance、降级、rebuild 和 quality/cost evaluation 的共同
   验收形状。
5. branch UX、跨 branch derived reuse、多 Parent merge，以及超出单 active domain 的
   branch-aware durable selection policy。

## 15. 架构成功标准

当路线完成到 Context Selection / Request Materialization 与可恢复 tool-loop 时，系统应具备以下
性质：

- 任意 compaction 或 maintainer 都不会抹去原始经历。
- 任意进入 V4 context 的 block 都能如实区分 set admission 与自身 absorbed cursor；producer/prompt
  audit 若需要则由独立能力提供，不伪装成 raw correctness。
- 任意 completion 都能回答“当时到底看到了什么”。
- 任意 tool-loop 崩溃都能落入明确的可继续、可查询、失败或 uncertain 状态。
- rewind / reroll 通过 branch 表达，不靠删除历史伪造过去。
- retrieval index 全部丢失后，session 仍可从 journals 恢复基本运行。
- 新 maintainer、retriever 或模型版本可以重算解释层，而不改写 raw truth。
- 所有新能力都在 SessionJournal family 及其附属新项目中实现，不要求修改旧 ChatSession。
- SessionJournal 产品依赖图不包含 ChatSession；迁移只经 versioned exchange format 单向发生且不双写。
- SessionJournal raw core 不反向引用 concrete Recap/Memory implementations；CLI/Host 通过
  contracts 组合 concrete implementations。
- 删除整个 derived store 不会破坏 raw audit，也不会使已经持久化的 Prepared 无法 exact reopen。

这组性质比“当前 prompt 是否更聪明”更重要：它们构成长期自主 Agent 能持续演化而不失去可追溯性的工程地基。
