# Micro-Wizard Runtime Draft

状态：draft v1  
定位：把 Micro-Wizard 从单点实验机制提升为 `Agent.Core` 上的受控短流程运行时。

配套文档：
- 运行时定位与分层：本文
- DSL / IR 草图：`docs/Agent/micro-wizard-dsl-sketch.md`
- capability 收口：`docs/Agent/agent-core-capability-system-draft.md`
- 施工前审阅：`docs/Agent/agent-core-micro-wizard-readiness-review.md`

## 0. 一句话结论

Micro-Wizard 的目标设计是合理的，而且值得推进实施。  
但它的**目标设计**与**阶段化落地**应分开理解：

- 目标设计上，Micro-Wizard 是一种事件驱动、可编排、可清理的短时认知运行时
- 阶段落地上，v0 不应直接追求完整 durable workflow engine

当前最重要的前提是：

- **`Agent.Core` 当前只接受 `SupportsAgentCoreFullFeatures == true` 的 profile**
- **因此 Micro-Wizard 可以直接建立在 full-feature 语义上，而不必再背 non-full-feature 兼容分叉**

---

## 1. 为什么需要 Micro-Wizard

长寿命 Agent 不应该只有两种运行模式：

- 主会话正常推进
- 工具单步执行

现实里还存在第三类过程：

- 多步
- 有阶段
- 需要临时目标
- 需要中间修复
- 结果要固化
- 过程最好不要长期污染主上下文

Micro-Wizard 就是为这第三类过程准备的运行时基础设施。

典型例子包括：

- MemoTree split 后的 gist/summary repair
- context compression
- risky edit preflight
- dirty derived memory repair

---

## 2. 当前边界

### 2.1 `Agent.Core` 的运行时边界

这一轮最关键的收口是：

- `Agent.Core` 不再尝试同时服务 full-feature surface 和 non-full-feature surface
- 它当前就是一个 **full-feature-only runtime**

这意味着 Micro-Wizard 的文档和实现都可以直接假定：

- thinking 对 runtime 是透明的
- actor-side continuation 可以被 runtime 注入与续写
- assistant prefix continuation 可以被正式依赖

### 2.2 这不等于“一步到位实现所有野心”

运行时边界收紧，不等于实现范围要同时膨胀。

当前不应在 v0 里一起追求：

- non-full-feature 兼容层
- 多实例并发调度
- 独立 wizard timeline
- 完整 durable recipe/instance schema
- 丰富的模型语义分类事件

---

## 3. 目标设计

从目标设计上，Micro-Wizard 应被理解为：

- 一个宿主可编排的局部流程 runtime
- 一套围绕 trigger / phase / commit / cleanup 组织起来的执行协议
- 一层建立在 `Agent.Core` 原语之上的 runtime middleware

它像：

- 迷你工作流
- 会话态状态机
- 受控短流程调度器

它不像：

- 超重工具
- 后台线程
- 仅靠 prompt engineering 拼出来的技巧

---

## 4. v0 真实可施工的 shape

### 4.1 不做独立执行腔室

v0 最自洽的落地，不是另造一个“主会话外的独立小房间”，而是：

- 仍然运行在同一份 `RecentHistory` 账本之上
- 用 `InjectionEntry` 建立可续写的 actor-side continuation
- 用请求级 tool gating 暂时收紧本阶段工具可见性
- 用宿主侧对象维护当前 active wizard 的 phase / scratchpad / cleanup

也就是说：

- **目标设计上**可以继续讨论更强的“临时执行腔室”直觉
- **v0 实现上**先不要把它实现成第二条时间线

### 4.2 v0 的核心原语

当前代码里真正已经到位、适合直接开工的原语是：

- `ActionProduced`
- `ToolExecutionCompleted`
- `PrepareInvocationAsync.ToolAccessOverride`
- `ToolAccessSnapshot.AllowOnly(...)`
- `InjectActionContent(...)`
- `InjectionEntry`

这足以支撑第一版宿主侧 `WizardOrchestrator`。

### 4.3 v0 的调度纪律

Micro-Wizard 第一版应顺着 `AgentEngine.StepAsync()` 的单步纪律来：

- 同时最多一个 active wizard
- wizard 只是主会话旁边的受控局部流程
- phase 切换、tool gating、repair、cleanup 都由宿主侧 orchestrator 负责

---

## 5. v0 可依赖的事件面

这里要显式区分：

- **目标设计里可以想象的事件**
- **当前 runtime 真正稳定提供的事件**

v0 应优先建立在后者上。

### 5.1 当前稳定可用的硬事件

- `ActionProduced`
- `ToolExecutionCompleted`
- `StateTransition`
- `WaitingInput`
- 宿主自己的外部领域事件

### 5.2 当前不应写成内建 runtime 合同的事件

下面这些概念可以保留为未来扩展方向，但不应在 v0 里假装它们已经是引擎原生事件：

- `AssistantProducedIrrelevantResponse`
- `AssistantNoProgress`
- 细粒度“模型动作语义分类”
- 复杂的自动 completion condition 语义

如果某个 recipe 需要这类判断，v0 更诚实的做法是：

- 把它放在宿主 heuristic 层
- 或绑定到具体业务场景

而不是把它硬写成引擎级真相。

---

## 6. v0 运行时分层

### 6.1 Primitive Layer

底层保持小原语：

- `SplitBodyBlockByText`
- `SplitNode`
- `SetGist`
- `SetSummary`

### 6.2 Recipe Layer

声明：

- 何时触发
- 有哪些 phase
- 每个 phase 允许哪些工具
- 成功后如何 commit
- 退出时如何 cleanup

### 6.3 Host-Side Orchestrator Layer

第一版不塞进 `AgentEngine` 核心。  
宿主侧 orchestrator 负责：

- 订阅事件
- 决定是否启动某个 wizard
- 管理单个 active wizard 的 phase / scratchpad
- 在 `PrepareInvocationAsync` 中施加 tool gating
- 在适当时机调用 `InjectActionContent(...)`

### 6.4 Audit / Summary Layer

v0 可以先保留轻量的：

- 主时间线结果性痕迹
- wizard 摘要痕迹
- 必要的调试审计

但不急着把完整 durable wizard timeline 先制度化。

---

## 7. 一个典型场景：MemoTree Split Repair

一个自然的第一版真实场景是：

1. 触发 split 相关操作
2. Gather phase 确认切分边界
3. Act phase 调 `SplitBodyBlockByText`
4. Act phase 调 `SplitNode`
5. Repair / Finalize phase 重建左右节点 gist/summary
6. Commit 结果
7. Cleanup 中间痕迹

这个场景非常合适，因为它同时需要：

- 局部 phase 目标
- 工具范围收紧
- 失败后 repair
- 结果固化

而且不需要先发明并发调度器。

---

## 8. 分阶段落地建议

### 阶段 A：先做 host-side `WizardOrchestrator`

只做单实例。  
不要先把 recipe/instance durable schema 烧进核心。

### 阶段 B：只落一个真实业务场景

推荐优先做：

- MemoTree split 后 gist/summary repair

### 阶段 C：等 shape 稳定后，再下沉 durable 对象

例如未来再正式建模：

- `WizardRecipeRef`
- `WizardInstance`
- `WizardStatus`
- `WizardPhaseState`

### 阶段 D：最后再谈 richer trigger / telemetry / training loop

这些都值得做，但不应抢跑。

---

## 9. 一句话总结

Micro-Wizard 的目标设计并不是问题。  
当前真正要做的是：

> **在 full-feature-only 的 `Agent.Core` 上，先用现有事件、injection 与 tool gating 原语，做出一个宿主侧、单实例、可运行的第一版 orchestrator。**
