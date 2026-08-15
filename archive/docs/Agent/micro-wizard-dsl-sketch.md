# Micro-Wizard DSL Sketch

状态：draft v1  
定位：为 `Micro-Wizard Runtime` 提供一套可持久化、可审计、可声明的 workflow IR 草图。

配套文档：
- `docs/Agent/micro-wizard-runtime-draft.md`
- `docs/Agent/agent-core-capability-system-draft.md`

## 0. 一句话结论

DSL 的目标设计是合理的，但 DSL 文档应该表达的是**最终想要的流程对象模型**，而不是把所有未来能力都提前伪装成 v0 已实现的运行时合同。

当前收口应是：

- DSL 继续面向声明式 workflow IR
- 但 v0 IR 只表达当前 `Agent.Core` 真正能稳定执行的东西
- `Agent.Core` 当前默认只服务 `SupportsAgentCoreFullFeatures == true` 的 surface

---

## 1. 设计目标

这份 DSL 草图要回答的是：

1. 如何声明一个 wizard
2. 如何表示它的阶段、触发器、转移与清理策略
3. 如何让 recipe / instance 最终可持久化、可恢复、可审计

当前**不**追求：

- 通用脚本语言
- 图灵完备表达式系统
- 一开始就支持用户自定义文本 DSL
- 先把多实例并发调度写进主模型

换句话说：

它首先是一套 **workflow IR**，不是通用编程语言。

---

## 2. 目标设计与阶段实现要分开

这是这轮最需要写清的一点。

### 2.1 目标设计

从目标设计上，wizard 当然可以长成：

- 有 recipe
- 有 instance
- 有 trigger
- 有 phase
- 有 commit / cleanup policy
- 有 durable 恢复语义

### 2.2 v0 实现现实

但当前 `Agent.Core` 最自然的施工路线是：

- 单实例
- 宿主侧 orchestrator
- 同一份 `RecentHistory` 账本
- 事件驱动 + injection + tool gating

因此 DSL 文档不应把下面这些东西写得像 v0 已经是正式合同：

- 独立 wizard timeline
- 多实例并发
- 丰富的模型语义分类事件
- 强约束的 `AllowedActions`

---

## 3. 直觉模型

一个 wizard 可以先被理解成：

```text
当某个事件发生
  若触发条件满足
    进入某个 phase
      注入阶段目标
      临时收紧允许工具
      等待 action / tool result / host event
      根据结果跳转、提交或放弃
退出时
  固化结果
  清理中间痕迹
```

它像：

- 迷你工作流
- 会话态状态机
- 受控短流程协议

它不像：

- 单个超级工具
- 后台线程
- 只有 prompt 的一次性模板

---

## 4. 建议分层

### 4.1 Primitive Layer

最底层原语，例如：

- `SplitBodyBlockByText`
- `SplitNode`
- `SetGist`
- `SetSummary`

### 4.2 DSL / IR Layer

这一层定义：

- `WizardRecipe`
- `WizardTrigger`
- `WizardPhase`
- `WizardTransition`
- `CommitPolicy`
- `CleanupPolicy`

### 4.3 Runtime Layer

解释执行 IR：

- 创建实例
- 推进阶段
- 应用事件
- 更新状态
- 提交结果
- 执行清理

### 4.4 Persistence Layer

未来把 recipe 引用、instance 状态、审计记录写入持久层。

### 4.5 Builder Layer

提供更舒服的 authoring API，但它只是写法层，不是真相层。

---

## 5. v0 最小对象模型

### 5.1 `WizardRecipe`

```text
WizardRecipe
  Id
  DisplayName
  Description?
  Trigger
  EntryPhaseId
  Phases[]
  CommitPolicy
  CleanupPolicy
  TelemetryPolicy?
```

### 5.2 `WizardTrigger`

v0 推荐只保留当前真需要的字段：

```text
WizardTrigger
  EventKind
  Predicate?
  CooldownTurns?
  Priority?
```

`MaxConcurrent` 这类字段不是不能有，但在当前“单实例 active wizard”前提下，不应占据主舞台。

### 5.3 `WizardPhase`

```text
WizardPhase
  Id
  Kind
  Goal
  PhasePrompt?
  RepairHint?
  AllowedTools[]
  RetryPolicy?
  Transitions[]
```

这里要特别强调：

- `AllowedTools` 是当前最硬的约束
- `PhasePrompt` / `RepairHint` 是软引导
- 不建议把 `AllowedActions` 写成 v0 的强约束合同

### 5.4 `WizardTransition`

```text
WizardTransition
  OnEvent
  Guard?
  Effects[]
  ToPhaseId?
  TerminalOutcome?
```

### 5.5 `WizardInstance`

```text
WizardInstance
  InstanceId
  RecipeId
  Status
  CurrentPhaseId
  Bindings
  Scratchpad
  RetryCounters
  LastEvent?
```

注意：

- v0 不建议把 `WizardTimelineRef` 写成必备字段
- 第一版真实运行时仍然建立在同一份 `RecentHistory` 上

---

## 6. 事件模型

DSL 是否顺手，关键不在语法，而在事件面是否诚实。

### 6.1 v0 应优先建模的硬事件

- `WizardStarted`
- `WizardAborted`
- `WizardCommitted`
- `ActionProduced`
- `ToolExecutionCompleted`
- `HostEvent`

这里的 `ActionProduced` / `ToolExecutionCompleted` 对应当前代码里已经真实存在的事件边界。

### 6.2 v0 不应假装已经内建的事件

下面这些概念可以保留为 future extension，但不应在 v0 IR 中占据核心地位：

- `AssistantProducedIrrelevantResponse`
- `AssistantNoProgress`
- 复杂的自动“动作语义分类”

原因很简单：

- 当前引擎没有对这些概念提供可靠的原生判定面
- 若某个 recipe 真需要它们，v0 应把它们放在宿主 heuristic 层

---

## 7. 建议的最小 Phase 类型

为了避免 DSL 一开始太开放，推荐先只支持少数 phase archetype：

- `Gather`
- `Act`
- `Repair`
- `Finalize`

这四类已经足够覆盖第一批目标场景。

---

## 8. 注入文本的生命周期应显式区分

在 wizard 里，注入文本至少分三类：

### 8.1 Main Context Text

主会话长期可见的结果性文本。

### 8.2 Phase Prompt

当前阶段的局部目标说明。

例如：

- “现在只确认切分边界，不要提前写 summary。”

### 8.3 Repair Hint

失败后的局部纠偏文本。

例如：

- “刚才失败，因为 after_text 不唯一。请给更短且更独特的锚点。”

这三类文本生命周期不同，DSL 应显式区分。

---

## 9. Bindings 与 Scratchpad

这两个概念值得继续保留。

### 9.1 Bindings

更像外部输入参数，例如：

- `target_node_id`
- `target_block_id`
- `user_goal`

### 9.2 Scratchpad

更像过程中的短生命周期工作内存，例如：

- `latest_tool_error`
- `candidate_after_text`
- `candidate_right_title`

---

## 10. Guard 与 Effect

为了不把 DSL 过早做成脚本语言，建议一开始只支持最小集合。

### 10.1 Guard

- `ToolNameIs(name)`
- `RetryCountBelow(n)`
- `ScratchExists(key)`
- `BindingExists(key)`
- `LastToolSucceeded`
- `LastToolFailedRecoverable`

### 10.2 Effect

- `SetScratch(key, value)`
- `CopyLastToolResultToScratch(key)`
- `IncrementRetryCounter(key)`
- `EmitRepairHint(text)`
- `CommitArtifact(key)`
- `EmitSummaryToMainTimeline(text)`

---

## 11. Commit / Cleanup Policy

这是 wizard 和普通工具最该拉开差距的地方。

### 11.1 Commit Policy

决定 wizard 成功后，哪些结果需要固化。

### 11.2 Cleanup Policy

决定哪些中间痕迹要移除、压缩或只保留摘要。

### 11.3 v0 收口

在 v0 中，这两类 policy 应优先面向：

- 主时间线结果性痕迹
- 轻量 wizard 摘要
- 必要调试审计

而不是一开始就绑定完整 durable wizard timeline。

---

## 12. 推荐的最小落地路线

1. 先把 recipe / runtime / trigger 三个概念从实验代码里拆清楚
2. 只支持一个 active wizard
3. 只落一个真实业务场景
4. 等 shape 稳定后，再决定哪些对象值得持久化成 durable schema

---

## 13. 一句话总结

Micro-Wizard DSL 仍然值得朝“声明式 workflow IR”发展；  
但 v0 文档应更诚实地表达：

> **先让 IR 只描述当前 full-feature-only `Agent.Core` 真正能执行的东西，再把 durable、多实例、独立时间线这些更强目标留到后续阶段。**
