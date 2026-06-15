# Micro-Wizard DSL Sketch

状态：draft v0
定位：为 `Micro-Wizard Runtime` 提供一套可持久化、可审计、可声明、可逐步实现的 DSL / IR 草图。

---

## 1. 为什么还需要一份 DSL 草图

`docs/Agent/micro-wizard-runtime-draft.md` 已经给出了方向判断：

- Micro-Wizard 不应只是 prompt 技巧
- 它更适合被提升为 Agent 运行时基础设施
- 它天然适合承载局部、多步、临时、可清理的过程

但“运行时基础设施”仍然需要一个可定义流程的语言层。

这份文档回答的问题是：

1. 我们如何定义一个 wizard？
2. 我们如何持久化它的运行状态？
3. 我们如何把“触发器 + 阶段 + 工具反馈 + 清理策略”组织成一套显式对象图？
4. 我们如何避免依赖 C# 编译器隐式生成的不稳定状态机？

核心主张：

Micro-Wizard 应优先建模为一种**声明式、事件驱动、可持久化的局部流程 DSL / IR**，而不是语言级 continuation。

---

## 2. 设计目标

### 2.1 必须满足

- 配方是显式对象图，可序列化、可持久化
- 运行实例状态是显式的，可中断、可恢复
- 流程能响应工具成功/失败/无调用等事件
- 流程支持阶段切换、重试、放弃、结果固化、痕迹清理
- 可与 `StateJournal` 良好配合
- 可被 `Agent.Core` 解释执行

### 2.2 暂时不追求

- 图灵完备脚本语言
- 任意复杂表达式求值器
- 先做多实例并发调度器
- 一开始就支持用户自定义 DSL 语法文本

换句话说：

现在更像在设计一套 **workflow IR**，不是在设计一门通用编程语言。

---

## 3. 直觉模型

一个 wizard 可以先被理解成：

```text
当事件 E 发生
  若条件 P 满足
    进入阶段 A
      注入阶段指令
      暴露允许工具
      等待模型动作或工具结果
      根据结果跳到 B / C / Abort / Commit
退出时
  固化结果
  清理中间痕迹
```

它像：

- 一个迷你工作流
- 一个会话态状态机
- 一个短时认知过程协议

它不像：

- 一个普通工具
- 一个后台线程
- 一个一次性 prompt 模板

---

## 4. 分层

推荐把整个系统拆成五层：

### 4.1 Primitive Layer

最底层原语。

例如：

- `SplitBodyBlockByText`
- `SplitNode`
- `SetGist`
- `SetSummary`
- `ctx_compress`

特点：

- 单步
- 可审计
- 不内置复杂局部流程

### 4.2 DSL / IR Layer

这份文档主要关注这一层。

它定义：

- `WizardRecipe`
- `WizardPhase`
- `WizardTransition`
- `WizardTrigger`
- `Commit/Cleanup Policy`

### 4.3 Runtime Layer

解释执行 DSL / IR。

负责：

- 创建实例
- 推进阶段
- 应用事件
- 更新实例状态
- 触发提交与清理

### 4.4 Persistence Layer

把 recipe 的引用、instance 状态、审计记录放进 `StateJournal` 或其他持久层。

### 4.5 Builder Layer

提供更舒服的上层 authoring API。

例如：

- 流式 builder
- 声明式 helper
- 预定义 wizard 模板

Builder 是写法层；IR 是真实持久化与运行时消费层。

---

## 5. 核心对象

推荐的最小对象集如下。

### 5.1 `WizardRecipe`

静态配方，描述“某一类 wizard 应如何运行”。

建议字段：

```text
WizardRecipe
  Id
  DisplayName
  Description
  Trigger
  EntryPhaseId
  Phases[]
  AllowedToolsPolicy
  CommitPolicy
  CleanupPolicy
  TelemetryPolicy
```

### 5.2 `WizardTrigger`

描述何时进入 wizard。

建议字段：

```text
WizardTrigger
  Id
  EventKind
  Predicate
  Cooldown
  MaxConcurrent
  Priority
```

### 5.3 `WizardPhase`

描述某一个阶段。

建议字段：

```text
WizardPhase
  Id
  Goal
  Prompt
  AllowedTools[]
  AllowedActions[]
  CompletionCondition
  RetryPolicy
  Transitions[]
```

### 5.4 `WizardTransition`

描述状态转移规则。

建议字段：

```text
WizardTransition
  FromPhaseId
  Event
  Guard
  Effects[]
  ToPhaseId?
  TerminalOutcome?
```

### 5.5 `WizardInstance`

运行时实例。

建议字段：

```text
WizardInstance
  InstanceId
  RecipeId
  Status
  CurrentPhaseId
  Bindings
  Scratchpad
  RetryCounters
  TimelineRefs
  CreatedAt
  UpdatedAt
```

### 5.6 `WizardOutcome`

结束结果。

```text
WizardOutcome
  Succeeded
  Aborted
  Failed
  CommittedArtifacts
  CleanupSummary
```

---

## 6. 事件模型

DSL 是否顺手，核心不在语法，而在事件模型。

推荐把下面这些都建模成一等事件：

### 6.1 生命周期事件

- `WizardStarted`
- `WizardResumed`
- `WizardAborted`
- `WizardCommitted`
- `WizardCleanedUp`

### 6.2 模型动作事件

- `AssistantProducedToolCall`
- `AssistantProducedMessage`
- `AssistantProducedIrrelevantResponse`
- `AssistantNoProgress`

### 6.3 工具结果事件

- `ToolSucceeded`
- `ToolFailedRecoverable`
- `ToolFailedFatal`

### 6.4 外部状态事件

- `DirtyNodeDetected`
- `ContextPressureExceeded`
- `UserCancelled`
- `DependencyInvalidated`

这些事件并不是都要在 v0 落地，但 IR 层最好提前给它们留位置。

---

## 7. 建议的最小 Phase 类型

为了避免 DSL 一开始太开放，推荐先只支持少数 phase archetype。

### 7.1 Gather Phase

用于收集信息、确认意图、建立边界。

典型场景：

- 询问 LLM 更具体的切分点
- 收集目标节点 ID
- 确认要保留什么摘要

### 7.2 Act Phase

用于调用工具、执行原语。

典型场景：

- `SplitBodyBlockByText`
- `SplitNode`
- `SetSummary`

### 7.3 Repair Phase

用于在失败后局部修复。

典型场景：

- 切分点匹配失败后要求更短更独特的前后文本
- 文本替换失败后要求提供更稳定锚点

### 7.4 Finalize Phase

用于结果固化、摘要生成、临时痕迹压缩。

典型场景：

- 生成 wizard 执行摘要
- 将局部结果回写主时间线
- 标记 dirty state 已修复

很多流程只靠这四类阶段就够用了。

---

## 8. Prompt 注入不是一个概念

在 wizard 里，文本注入至少分三类：

### 8.1 Main Context Text

主会话长期可见的文本。

### 8.2 Phase Prompt

当前阶段的局部目标说明。

例如：

- “现在只确认切分点，不要提前写 Summary。”

### 8.3 Repair Hint

失败后的局部纠偏文本。

例如：

- “刚才失败，因为 after_text 在该 block 中未唯一匹配。请提供更短且更独特的前后文本。”

这三类文本的生命周期完全不同，因此 DSL 最好显式区分，而不是都塞进一个 `Prompt` 字段。

---

## 9. 推荐的 IR 草图

下面给一个偏中性的对象草图。

### 9.1 Recipe

```text
WizardRecipe
  Id: string
  DisplayName: string
  Description: string?
  Trigger: WizardTrigger
  EntryPhaseId: string
  Phases: WizardPhase[]
  CommitPolicy: WizardCommitPolicy
  CleanupPolicy: WizardCleanupPolicy
  TelemetryPolicy: WizardTelemetryPolicy
```

### 9.2 Trigger

```text
WizardTrigger
  EventKind: WizardEventKind
  Predicate: WizardPredicate?
  CooldownTurns: int
  MaxConcurrentInstances: int
  Priority: int
```

### 9.3 Phase

```text
WizardPhase
  Id: string
  Kind: Gather | Act | Repair | Finalize
  Goal: string
  PhasePrompt: string?
  RepairHint: string?
  AllowedTools: string[]
  AllowedActions: WizardActionKind[]
  CompletionCondition: WizardCondition?
  RetryPolicy: WizardRetryPolicy?
  Transitions: WizardTransition[]
```

### 9.4 Transition

```text
WizardTransition
  OnEvent: WizardEventPattern
  Guard: WizardCondition?
  Effects: WizardEffect[]
  ToPhaseId: string?
  TerminalOutcome: Succeeded | Failed | Aborted | null
```

### 9.5 Instance

```text
WizardInstance
  InstanceId: string
  RecipeId: string
  Status: Created | Running | Waiting | Succeeded | Failed | Aborted
  CurrentPhaseId: string
  Bindings: Map<string, Value>
  Scratchpad: Map<string, Value>
  RetryCounters: Map<string, int>
  MainTimelineRef: ...
  WizardTimelineRef: ...
  LastEvent: WizardEvent?
```

---

## 10. Bindings 与 Scratchpad

这两个概念最好分开。

### 10.1 Bindings

偏“外部输入绑定”，生命周期较长。

例如：

- `target_node_id`
- `target_block_id`
- `user_goal`

### 10.2 Scratchpad

偏“过程中的中间产物”，生命周期较短。

例如：

- `proposed_after_text`
- `latest_tool_error`
- `candidate_right_title`

Bindings 更像过程参数；Scratchpad 更像局部工作内存。

---

## 11. Transition 里的 Guard 和 Effect

为了不把 DSL 过早做成完整脚本语言，建议一开始只支持少量 Guard / Effect。

### 11.1 Guard

建议最小集合：

- `ToolNameIs(name)`
- `RetryCountBelow(n)`
- `ScratchExists(key)`
- `BindingExists(key)`
- `LastToolSucceeded`
- `LastToolFailedRecoverable`

### 11.2 Effect

建议最小集合：

- `SetScratch(key, value)`
- `CopyLastToolResultToScratch(key)`
- `IncrementRetryCounter(key)`
- `EmitRepairHint(text)`
- `CommitArtifact(key)`
- `MarkDirtyNodeRepaired(nodeId)`
- `EmitSummaryToMainTimeline(text)`

这已经足以承载很多流程。

---

## 12. Commit / Cleanup Policy

这是 wizard 和普通工具最容易拉开差距的地方。

### 12.1 Commit Policy

决定 wizard 成功后，哪些结果应该固化。

例如：

- 将新 `gist/summary` 写回 MemoTree
- 将一段摘要消息回写主会话
- 将某个 dirty state 标记为 repaired

### 12.2 Cleanup Policy

决定 wizard 中间痕迹如何处理。

例如：

- 保留结果摘要
- 丢弃全部中间 prompt
- 仅在审计日志中保留失败细节

推荐最小策略：

- `KeepFull`
- `KeepSummaryOnly`
- `KeepAuditOnly`

`KeepSummaryOnly` 很可能会是默认最有价值的策略。

---

## 13. Main Timeline 与 Wizard Timeline

推荐显式承认双时间线：

### 13.1 Main Timeline

主会话时间线。

保留：

- 用户任务推进
- 高价值结果
- 少量必要摘要

### 13.2 Wizard Timeline

临时过程时间线。

保留：

- 阶段 prompt
- 失败重试
- 工具调用与修复过程
- 更细的中间状态

运行时退出时：

- Main Timeline 保持轻量
- Wizard Timeline 仍可审计

这正是“避免痛苦回忆污染主上下文”的关键。

---

## 14. 一个具体示例：MemoTree Split Wizard

下面给一个概念化 recipe。

### 14.1 目标

把一个长节点拆成两个更合适的 sibling，并为新节点集补齐 `gist/summary`。

### 14.2 配方草图

```text
Recipe: memo.split-node

Trigger:
  OnToolCalled("memo.split_node_intent")

Phase A: gather-boundary
  Goal:
    确认切分点描述
  AllowedTools:
    memo.read_node
    memo.read_body_blocks
    memo.split_body_block_by_text
  On ToolSucceeded(memo.split_body_block_by_text):
    -> Phase B
  On ToolFailedRecoverable(memo.split_body_block_by_text):
    EmitRepairHint("请提供更短且更独特的前后文本。")
    -> Phase A

Phase B: split-node
  Goal:
    基于新的 block 边界执行结构切分
  AllowedTools:
    memo.split_node
  On ToolSucceeded(memo.split_node):
    -> Phase C
  On ToolFailedRecoverable(memo.split_node):
    EmitRepairHint("请重新检查切分边界与新标题。")
    -> Phase B

Phase C: repair-derived-memory
  Goal:
    为左右节点补全 gist / summary
  AllowedTools:
    memo.read_node
    memo.set_gist
    memo.set_summary
  Completion:
    左右节点 gist/summary 均达到 Fresh
  On Completion:
    -> Phase D

Phase D: finalize
  Goal:
    固化结果并输出摘要
  Commit:
    写回 MemoTree
    输出主会话摘要
  Cleanup:
    KeepSummaryOnly
```

### 14.3 观察

这里很清楚地体现了两层分工：

- 文本层 helper：负责找到并建立块边界
- 节点层原语：负责结构重组
- 派生记忆修复：由后续 phase 处理

这比一个超级工具要干净得多。

---

## 15. Builder 风格 API 草图

IR 适合持久化，但 authoring 可能更适合 builder。

可以给一个概念式 builder：

```csharp
WizardRecipeBuilder
    .Create("memo.split-node")
    .Trigger(OnToolCalled("memo.split_node_intent"))
    .Phase("gather-boundary", phase => phase
        .Kind(WizardPhaseKind.Gather)
        .Goal("确认切分点描述")
        .AllowTools("memo.read_node", "memo.read_body_blocks", "memo.split_body_block_by_text")
        .OnToolSucceeded("memo.split_body_block_by_text", gotoPhase: "split-node")
        .OnToolFailedRecoverable(
            "memo.split_body_block_by_text",
            repairHint: "请提供更短且更独特的前后文本。",
            stayInPhase: true))
    .Phase("split-node", phase => phase
        .Kind(WizardPhaseKind.Act)
        .Goal("基于新的 block 边界执行结构切分")
        .AllowTools("memo.split_node")
        .OnToolSucceeded("memo.split_node", gotoPhase: "repair-derived-memory"))
    .Phase("repair-derived-memory", phase => phase
        .Kind(WizardPhaseKind.Repair)
        .Goal("为左右节点补全 gist / summary")
        .AllowTools("memo.read_node", "memo.set_gist", "memo.set_summary"))
    .FinalizeWithSummaryCleanup();
```

这个 builder 不是最终 API 承诺，只是说明：

- 写起来可以像程序
- 存起来仍然应该是显式 recipe object graph

---

## 16. 为什么不直接用 C# `async` / `yield`

因为我们真正需要的是：

- 可持久化
- 可恢复
- 可版本化
- 可审计
- 可声明

而不是：

- 编译器帮我把一个函数偷偷变成 continuation

语言级状态机很适合：

- 本地异步编程
- 进程内控制流

但不天然适合：

- 持久化 Agent 局部认知流程
- 跨运行恢复
- 审计化解释执行

所以推荐策略是：

- 不用隐式 continuation 当 IR
- 可以用 builder API 提供“像写程序一样舒服”的 authoring 体验

---

## 17. 建议的最小落地路线

### 阶段 1

定义最小 IR：

- `WizardRecipe`
- `WizardPhase`
- `WizardTransition`
- `WizardInstance`

### 阶段 2

实现单实例 runtime：

- 只支持单活跃 wizard
- 只支持 `Gather / Act / Repair / Finalize`

### 阶段 3

实现最小事件集：

- `ToolSucceeded`
- `ToolFailedRecoverable`
- `ToolFailedFatal`
- `AssistantNoProgress`

### 阶段 4

接入第一个真实业务：

- MemoTree split / derived-memory repair wizard

### 阶段 5

再扩展：

- dirty state 触发器
- context compression wizard
- risky edit wizard
- 更丰富的 cleanup / telemetry

---

## 18. 一句话结论

Micro-Wizard 最适合被建模成：

一套围绕 `Trigger -> Phase -> Event -> Transition -> Commit/Cleanup` 展开的、显式可持久化的局部流程 DSL。

它不是 continuation 的替代品，而是 Agent 运行时中的“短时认知过程描述语言”。
