# Agent.Core Live Durable Workspace Migration Draft

状态：draft v0
定位：讨论 `Agent.Core` 如何从当前的 snapshot persistence 模式，迁移到以 StateJournal 为主真相的 live durable workspace 模式。

> Progress note
> - 阶段 B 已基本完成：`AgentState` / `AgentWorkspaceRoot` 已能围绕 repo-backed workspace 作为主持久化载体工作。
> - 阶段 C / D 已部分落地：history append、pending notifications、pending tool results、turn runtime 的 live mutation 路径已经存在。
> - `pendingCompaction` live path 已收口到稳定 durable slot 内的字段级 mutation；新 schema 要求预先 seed `pendingCompaction` record，不再兼容缺 key 读取或懒创建；snapshot / full-runtime replace 仍保留 whole-replace 语义。
> - 阶段 E 的最小收口已落地：repo-backed live 创建路径改为 born-bound，`AgentState`/`AgentEngine` 不再依赖 `AttachWorkspaceRoot(... syncExistingState:true/false)` 或 `AttachRepositoryPersistence(...)` 这类 post-attach 主路径。
> - `AgentWorkspaceSession` 现已成为 repo-backed live path 的单一 mutation/commit owner：history/meta/runtime 的 live 写入与 `Commit/Dispose` 都直接站在 `AgentWorkspaceRoot` 上；`AgentEngineStateRoot` 被压回 public snapshot compatibility / diagnostic / import-export 视图。
> - 阶段 C 的一个行为保持型前置切口也已落地：`AgentState` 的 `recentHistory / pendingNotifications / lastSerial` 已收成内部 working-set cache，并建立了整包 `LoadStateSnapshot -> RestoreSnapshot -> ReplaceWorkingSet` seam，为后续把 durable history/workspace 提升为真相预留了 cache reload 边界。
> - 阶段 C 已继续向 durable truth 推进一步：repo-backed live path 下，`pending notifications` 的 append / drain、`ReplacePrefixWithRecap(...)`，以及 `AppendAction` / `AppendObservation` / `AppendToolResults` / `InjectActionContent` 这些 recent-history 主链入口，现已围绕 `AgentWorkspaceSession` authoritative mutation 工作；其中 recap / drain 与 observation / tool-results append 走 `LoadStateSnapshot -> ApplySnapshot`，action append / injection 走 targeted recent-history snapshot reload，notification append 走 targeted pending-cache refresh。
> - 阶段 C 的 recent-history 主链实现也已进一步收口：`AgentState` 中原先为 live/local 双用保留的 generic history write-through primitive（如统一 `AllocateNextSerial` / `AppendHistoryEntry` seam）已开始回退为 non-live local-path 专用 helper，repo-backed live path 不再通过这些“伪通用”入口直接写 durable history。
> - 相邻的小尾巴也在继续收口：`AppendNotification(...)` 与 `SetSystemPrompt(...)` 的 repo-backed live path 已改成 operation-specific session mutation，不再依赖 `SyncSession* + Reload*` 形式的旧桥接。
> - 阶段 D 也已开始向 authoritative cache-backfill 收口：`pending tool results` 的 live path 现已通过 `AgentWorkspaceSession` mutation 返回 durable snapshot，并回填 `AgentEngine` 本地 `_pendingToolResults`；但 `ResolvedProfile / turn runtime` 仍缺少运行期可复用的 profile-resolver apply seam，因此尚未完全采用同一模式。
> - 当前 public snapshot path 仍保留，但定位应视为 compatibility / diagnostic / import-export 边界，不再是推荐主路径。
> - 小尾修已继续收口：`AgentEngineHost` 不再暴露可写 `StateRoot` adapter，live host 仅保留显式 `LoadSnapshot()` 查询口；默认 state seeding 也已回收到 `AgentWorkspaceRoot.Create(...)` 创建期，不再以普通 helper 形式承担隐式 reset 语义。

相关文档：
- `docs/Agent/agent-core-branching-infrastructure-backlog.md`
- `docs/Agent/micro-wizard-history-route-comparison.md`
- `docs/Agent/micro-wizard-history-forked-context-route-analysis.md`
- `docs/Agent/micro-wizard-runtime-draft.md`
- `docs/Agent/LlmSession-Execution-Kernel-Draft.md`
- `docs/StateJournal/usage-guide.md`

相关代码：
- `prototypes/Agent.Core/AgentEngine.Persistence.cs`
- `prototypes/Agent.Core/Persistence/AgentEngineStateRoot.cs`
- `prototypes/Agent.Core/Persistence/AgentEngineStateCodec.cs`
- `prototypes/Agent.Core/History/AgentState.Persistence.cs`
- `prototypes/Agent.Core/AgentEngineHost.cs`

## 0. 一句话结论

当前 `Agent.Core` 的持久化模式，本质上仍是：

- 内存里运行真正的 `AgentEngine`
- 在稳定边界导出 snapshot
- 再把 snapshot 编码进 StateJournal
- 恢复时从 snapshot 重建新引擎

这在早期探索阶段是合理的，但如果把 `Agent.Core` 视为一个可能持续运行几十年的 Agent 内核，这个模式并不够好。
更合适的长期方向应是：

> **把 `Agent.Core` 升级为“围绕 live durable workspace 工作的运行时”，让 StateJournal 承担 durable working state 的主真相，而不是继续只扮演 snapshot 落盘容器。**

这不等于“一切都 durable 化”。
更准确的边界是：

- **必须跨重启连续存在的工作态**：直接建模在 StateJournal 中
- **可重建、短命、调度现场型状态**：继续保留为普通内存对象

---

## 1. 为什么现在值得谈这个重构

这不是单纯的“代码洁癖”问题，而是主骨架问题。

当前 snapshot persistence 模式有四个长期缺点：

1. **双真源倾向**
   - 业务语义首先活在内存对象里
   - durable 侧只是它的编码投影
   - 一旦两边边界漂移，就容易出现“到底谁是真相”的问题
   - 注意：迁移到 live durable workspace 后，若采用 §11.2 的 read-through cache，内存镜像与 durable object 仍是两份表示。真正消除的不是“双表示”，而是“真相位置不明确”——迁移后真相唯一锚定在 durable object，cache 只是带明确失效边界的从属副本（参见 §11.2）。
2. **branch / fork 吃不满 StateJournal 红利**
   - 底层对象即使会 fork / replay
   - `Agent.Core` 仍先把自己拍扁成 snapshot，再重建
   - 很多 branch workspace 语义无法顺着 durable graph 自然长出来
3. **恢复语义不够直观**
   - 恢复的是“某次导出的解释结果”
   - 而不是“当时正在活着的 durable 工作区”
4. **演进成本转移到 codec**
   - 每增加一种真正重要的新状态
   - 都要同步改 live object、snapshot record、codec、restore path
   - 复杂度会越来越集中到转换层，而不是语义层

对一个寿命很长、需要反复恢复、需要 branch/fork、需要诚实地保留工作态连续性的系统来说，这种模式会越来越别扭。

---

## 2. 当前模式到底哪里“不彻底”

当前真实结构可以简化理解成：

```text
live AgentEngine / AgentState
  -> ExportStateSnapshot()
  -> AgentEngineStateRoot.Save(snapshot)
  -> StateJournal durable graph

restart
  -> AgentEngineStateRoot.Load()
  -> AgentEngineStateSnapshot
  -> CreateFromStateSnapshot(...)
  -> new AgentEngine
```

这意味着 StateJournal 当前在 `Agent.Core` 里承担的是：

- durable serialization target
- not durable working state host

因此现在的 `AgentEngineStateRoot` 更像：

- snapshot codec 容器

而不是：

- live workspace root

这就是“不彻底”的核心。

---

## 3. 迁移后的目标形态

迁移后的长期目标，不应再是：

- `AgentEngine` 有一份内存态
- StateJournal 只是它的镜像

而应是：

- StateJournal 中的 durable workspace 是工作态主真相
- `AgentEngine` 是围绕这份 durable workspace 运转的 coordination runtime

也就是：

```text
StateJournal durable workspace
  = Agent.Core 的 durable truth

AgentEngine
  = 围绕 durable truth 工作的执行协调器

runtime overlay
  = 进程内短命调度现场
```

这和 [LlmSession-Execution-Kernel-Draft.md](LlmSession-Execution-Kernel-Draft.md) 里“durable model + runtime overlay”的取向是一致的。

---

## 4. durable truth 与 runtime overlay 的建议边界

这是整个迁移里最关键的设计判断。

### 4.1 应直接 durable 建模的部分

这些状态如果在 crash / restart 后丢失，会破坏工作连续性，因此应进入 live durable workspace：

- system prompt
- `RecentHistory`
- `pending notifications`
- `pending tool results`
- turn lock / resolved profile checkpoint
- compaction checkpoint
- history serial / step identity
- 未来的 `ContextFrame` / branch metadata
- retained result / merge policy metadata
- active wizard / branch 的 durable lifecycle 元数据

### 4.2 继续保留为内存态的部分

这些状态更适合作为 runtime overlay，而不是 durable business state：

- provider client / `ICompletionClient`
- `ToolRegistry`
- `CancellationTokenSource`
- in-flight task handle
- ready queue membership
- active dispatch / active lease
- timer / timeout object
- projection cache / token estimate cache
- app window render cache
- debug observer / telemetry sink handle

> 说明：上表中 `ready queue membership` / `active dispatch / active lease` / `timer / timeout object` 等属于 kernel-forward 概念（来自 [LlmSession-Execution-Kernel-Draft.md](LlmSession-Execution-Kernel-Draft.md)），当前 `Agent.Core` 尚未实现。列在此处是为了预先确定它们未来归属 runtime overlay，而不是描述现状。

### 4.3 一条核心原则

判断某个状态是否该 durable 化，可以问一句：

- **如果进程现在崩溃，我是否希望重启后仍然能诚实地知道它处于什么工作语义位置？**

若答案是“希望”，它更应该进入 durable truth。
若答案是“重建即可”，它更适合保留在 runtime overlay。

---

## 5. 建议的 live durable workspace 形状

### 5.1 第一阶段不必追求“全新 durable 自定义对象系统”

迁移的重点是：

- 让 StateJournal 成为工作态主真相

而不是：

- 第一时间把所有东西都改写成复杂的自定义 durable class

因此比较务实的第一阶段做法是：

- 保留当前以 `DurableDict` / `DurableDeque` / `DurableHashSet` / `DurableOrderedDict` / `DurableText` 为主的 durable graph
- 但停止“先导出 snapshot，再整体重写 durable graph”的路径
- 改为直接在这些 durable object 上进行 live mutation

> 澄清：这里的 live mutation 指直接修改 durable object（StateJournal 语义下只是把对象标脏，停留在内存 working state），**不等于每次改动都落盘**。真正的磁盘写入仍发生在显式 `Repository.Commit(root)` 边界，commit 节奏可与现在的 stable-boundary 提交保持一致。这次迁移改变的是“谁是真相”，不是“多频繁写盘”。

### 5.2 推荐引入 workspace façade，而不是让业务层到处摸 string-key dict

即使底层 root 暂时仍是 `DurableDict<string>`，也建议尽快引入一层强语义 façade，例如：

```text
AgentWorkspaceRoot
  Meta
  History
  PendingNotifications
  PendingToolResults
  TurnRuntime
  CompactionState
  FrameState
```

它不一定一开始就必须是新的 `DurableObject` 子类。
更实际的第一步可以是：

- 一个包裹 `DurableDict<string>` 的 typed façade
- 对外暴露强语义属性与方法

这样可以避免：

- `Agent.Core` 业务逻辑到处写 key string
- 持久化层细节反向污染状态机代码

### 5.3 一个更理想的 durable root 分层

可以考虑把 durable root 拆成几个语义块：

```text
AgentWorkspaceRoot
  MetaRoot
  HistoryRoot
  RuntimeStateRoot
  BranchingRoot
  AuditRoot
```

其中：

- `MetaRoot`
  - schema version
  - system prompt
  - durable session id
- `HistoryRoot`
  - recent history deque
  - pending notifications
  - history serial allocator
- `RuntimeStateRoot`
  - pending tool results
  - resolved profile checkpoint
  - locked compaction split index
  - pending compaction
  - tool session execution sequence
- `BranchingRoot`
  - context frames
  - active frame id
  - retained result metadata
  - branch workspace refs
- `AuditRoot`
  - optional trace / branch merge log / dropped-span diagnostics

这比“一个大 snapshot record + 一堆 codec helper”更适合长期演化。

---

## 6. 对 `AgentState` / `AgentEngine` 的影响

### 6.1 `AgentState` 不应继续只是 `List<HistoryEntry>` 持有者

当前 `AgentState` 的心智模型更像：

- 内存 RecentHistory 管理器

迁移后更合适的方向是：

- `AgentState` 变成 durable history workspace 的 façade / coordinator

也就是说，`AgentState` 不应再默认把：

- `_recentHistory`
- `_pendingNotifications`
- `_lastSerial`

仅仅看成普通内存字段。
它应逐步变成：

- 直接面向 durable history root 的操作层

### 6.2 `AgentEngine` 应从“持有状态”转向“围绕 workspace 运转”

迁移后更理想的关系是：

- `AgentEngine` 不再是状态真相拥有者
- `AgentEngine` 是围绕 `AgentWorkspaceRoot` 协调 completion / tool / projection / recovery 的执行器

这会让很多事情更自然：

- branch workspace
- fork agent
- retained result merge
- crash recovery
- checkpoint / commit boundary

### 6.3 `AttachPersistenceSession()` 这类模式应逐步退出主骨架

当前 repo-backed host 是：

- 先有 `AgentEngine`
- 再 attach 一个 persistence session

当前实现已经往前收了一步：

- 引入了 internal `AgentWorkspaceSession`
- `AgentState` 的 durable history / system prompt / notifications 写链与 `AgentEngine` 的 runtime / commit 写链已统一挂到同一个 session 宿主
- repo-backed born-bound live engine 持有真实 session
- public `CreateFromRoot(...)` / `CreateFromStateSnapshot(...)` 仍明确走 snapshot / non-live 路径，不持有 session

长期看，这个方向是反着的。
更合理的关系应是：

- 先打开 `AgentWorkspaceRoot`
- 再创建围绕它工作的 `AgentEngine`

也就是：

- persistence is not an attachment
- persistence is the substrate

---

## 7. 对 branch / fork / wizard 的直接收益

这是这次重构最值得做的地方之一。

### 7.1 fork-context 更容易吃到 StateJournal 红利

一旦 `Agent.Core` 的 durable truth 本身就活在 StateJournal workspace 里，那么：

- branch workspace 可以直接围绕 durable subgraph 展开
- `ForkCommittedAsMutable()` / `ReplayCommitted(...)` 不再只是底层能力
- 而能更自然进入 `Agent.Core` 语义层

### 7.2 `pop-route` 也会受益

即使先落 `pop-route`，live durable workspace 仍然有价值，因为：

- savepoint / frame metadata 更容易诚实持久化
- tail rewrite 的原子边界更容易和 durable commit 对齐
- crash recovery 更容易知道“当前到底处于哪个 frame”

### 7.3 fork agent 会从“技巧”变成“自然能力”

当前 fork agent 更像：

- 导出快照
- 重建子引擎
- 宿主散拼 parent/child 关系

迁移后它更容易变成：

- durable workspace 层的一等 branch / child session 能力

这会大幅提升后续设计空间。

---

## 8. 为什么我不建议“大爆炸式彻底重写”

虽然我认为方向应该改，但不建议一次性重写全部。

原因很现实：

- 现有 `AgentEngine` 状态机、history projection、tool loop 都已经有不少真实语义沉淀
- snapshot 路线虽然不理想，但它不是“错误到必须立刻推倒”
- 大爆炸重写很容易同时把 durable truth、runtime overlay、branching、tool contract 一起搅乱

更稳的方式应是：

- 先改主真相边界
- 再逐层减少 snapshot round-trip
- 再把 branch / frame 语义长出来

---

## 9. 建议的迁移阶段

### 阶段 A：正式承认 snapshot persistence 是过渡层

先在文档和代码心智模型上收口：

- `AgentEngineStateSnapshot` / `AgentEngineStateRoot` 是过渡方案
- 它们主要服务当前 host、迁移、诊断、测试
- 不再把它们视为长期主骨架

这一阶段不一定大改代码，但会帮助后续判断不再反复摇摆。

### 阶段 B：引入 `AgentWorkspaceRoot` façade

先不急着推翻现有 StateJournal graph shape。
先引入一层强语义 façade，包住当前 root：

- history
- notifications
- pending tool results
- turn runtime
- compaction checkpoint

目标是先把：

- durable root 的语义访问
- snapshot codec helper

从“同一层”拆开。

### 阶段 C：把 `AgentState` 改为直接操作 durable history workspace

这是第一个真正重要的迁移点。

要做的是：

- 让 append / inject / recap / future tail rewrite 直接作用于 durable history root
- 内存里允许有 cache，但 cache 不再是真相

此阶段完成后，`RecentHistory` 才算真正进入 live durable workspace 模式。

> 主要风险：当前 `HistoryEntry` 是富 C# 对象（`ActionEntry.Message.Blocks` 等），让 `AgentState` 直接面向 durable history root 后，cache（C# 对象）与 durable history deque 必须在 append / inject / **recap / tail-rewrite** 等所有路径上保持一致。recap 替换、尾部回写这类非追加操作尤其容易让两者漂移，应在本阶段就明确 cache 失效与重建策略，这是阶段 C 的首要工程难点（与 §11.2 呼应）。

### 阶段 D：把 pending tool results / turn runtime / compaction state 迁入 live durable model

这一步把目前 snapshot record 里另外几块关键工作态也转过去：

- pending tool results
- resolved profile checkpoint
- locked compaction split index
- pending compaction
- tool session execution sequence（当前已由 `AgentEngineStateSnapshot.ToolSessionExecutionSequence` 持久化，迁移时不要遗漏）
- future frame metadata

完成后，`AgentEngineStateSnapshot` 的存在价值会明显下降。

### 阶段 E：重构 `AgentEngineHost` / engine 创建方式

把创建关系改成：

- open repo
- checkout branch / open workspace
- create engine around workspace

而不是：

- create engine
- attach persistence

这一步会让“persistence is substrate”真正成立。

### 阶段 F：保留 snapshot import/export，但降级用途

此时 snapshot 路线仍可保留，但用途应降级为：

- 迁移旧仓库
- 诊断导出
- 测试 fixture
- 离线备份/恢复

而不是日常主运行路径。

---

## 10. 对 schema 与兼容性的建议态度

因为项目仍处在早期快速迭代阶段，我不建议为了迁移保留过多兼容层。

更合适的策略是：

- 可以接受 durable schema version 升级
- 可以接受一次性迁移脚本 / 打开时迁移
- 可以接受旧 snapshot root 只作为导入源，而不继续成为长期兼容负担

也就是说：

- **及时重构优于长期背双体系兼容层**

这和项目当前总体节奏是相符的。

---

## 11. 几个值得提前写清的开放问题

### 11.1 `HistoryEntry` 继续以 record codec 落盘，还是进一步 typed durable 化

第一阶段我更倾向：

- 先保留 `HistoryEntry -> DurableDict<string>` 这套编码形状
- 但停止“先导出完整 snapshot 再重写 root”

因为迁移的关键问题是：

- 主真相位置

而不是：

- 第一时间把每个历史条目都改造成新的 durable 类型体系

### 11.2 live durable mutation 的性能与 cache 怎么做

长期主真相在 durable object 上，不等于每次都要无缓存地从 durable graph 生读。
更合理的做法是：

- write-through durable truth
- read-through cache
- 明确 cache 失效边界

也就是说可以有内存镜像，但必须承认：

- cache is not truth
- 因此 §1 所说的“消除双真源”应理解为“真相位置唯一、优先级明确”，而不是“内存里不再有第二份表示”。cache 与 durable object 仍是两份数据，迁移真正消灭的是“谁是真相不清楚”；cache↔durable 的一致性维护成本依然存在，需要明确失效边界（尤其是 recap / tail-rewrite 路径，见阶段 C）。

### 11.3 branch workspace 是 fork root，还是 fork subgraph

迁移后需要决定：

- branch 是直接 fork 整个 session root
- 还是只 fork history/runtime 的某个子图

我倾向于：

- 先允许 fork 整个 session workspace 语义
- 后续再为更细粒度 branch 优化布局

### 11.4 app/tool participation contract 何时并入

这次迁移不必第一天就把所有 app/tool 也 durable 化。
但至少要预留：

- 哪些 app state 未来会进入 branch/durable workspace
- 哪些工具只读
- 哪些工具 commit-phase only

否则上层 branch 语义仍然会被工具世界拖住。

---

## 12. 最终判断

如果把 `Agent.Core` 当成一个可能连续运行几十年的 Agent 内核来看，
我不认为当前的 snapshot persistence 形态应该继续被当作长期主骨架。

更好的长期方向是：

> **让 `Agent.Core` 成为“围绕 live durable workspace 工作的运行时”，而不是“会定期把内存状态导出成 durable snapshot 的运行时”。**

这次重构最重要的不是：

- 立刻把所有东西都换成 fancy durable 类型

而是：

- 明确 durable truth 在哪里
- 明确 runtime overlay 在哪里
- 让 `Agent.Core` 主路径真正吃到 StateJournal 的 branch / fork / recovery 红利

我会把这件事看成：

- **主骨架纠偏**

而不是：

- **可做可不做的持久化小优化**
