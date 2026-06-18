# Micro-Wizard History Route Comparison

状态：draft v2  
范围：综合比较两条“进入 Micro-Wizard 后保留最终结果、忘掉详细中间过程”的技术路线，并给出当前阶段推荐。

相关文档：
- `docs/Agent/micro-wizard-history-forked-context-route-analysis.md`
- `docs/Agent/micro-wizard-history-pop-route-analysis.md`
- `docs/Agent/micro-wizard-runtime-draft.md`
- `docs/Agent/thinking-stack-draft.md`
- `docs/Agent/agent-core-micro-wizard-readiness-review.md`
- `prototypes/MutableContextAgentProto/Phase2Commands.cs`

## 0. 一句话结论

如果讨论的是**目标设计的语义纯度**，我仍更偏向“fork 临时上下文，结束后只把结果拼回主上下文”。  
但在 StateJournal 新增：

- `DurableDeque<T>` / `DurableDeque` 的 `ForkCommittedAsMutable()`
- `Repository.ReplayCommitted(..., LoadMaterializationMode.ForceMutable)` 这条通用 committed clone fallback

之后，问题已经不再是“fork-context 还差关键容器能力”，而更像是：

- **短流程、低摩擦落地**时，`same-context pop route` 更轻、更快
- **长流程、递归分支、需要本地压缩/恢复**时，`forked-context route` 更正形、更强大

也就是说：

- **目标设计与长程能力**：更偏向 forked-context route
- **短流程首步落地**：更偏向 same-context pop route
- **当前更合理的态度**：按场景分工，而不是强行二选一

---

## 1. 两条路线的核心差异

### 1.1 Forked-context route

核心直觉是：

- Wizard 是一块临时工作区
- 主上下文在进入 Wizard 时被冻结为锚点
- Wizard 多步过程在侧向上下文里运行
- 退出时主上下文恢复到进入点，只吸收结果性产物

它更像：

- 局部 branch
- 临时工作副本
- 侧向执行腔室

### 1.2 Same-context pop route

核心直觉是：

- Wizard 仍然在同一份 `RecentHistory` 上运行
- 进入 Wizard 时记录 savepoint
- 退出时把 savepoint 之后的细节整体折叠掉
- 再把最终需要保留的结果性条目重新写回

它更像：

- 同账本上的受控尾部重写
- savepoint + truncate + retain-result

---

## 2. 从目标设计看，哪条更漂亮

从目标设计看，forked-context route 更漂亮，原因有三点：

1. 它天然符合“Wizard 是临时工作区，不是主时间线的一段脏尾巴”这个直觉。
2. 它与“结果固化、过程可清理、审计可旁路保留”的语义更一致。
3. 它和 `Phase2Commands.RunViewFileWizardAsync` 已经验证过的原理更接近。

所以如果只问“我们最终想把系统长成什么样”，我认为答案更偏向：

- **局部工作区模型**

而不是：

- **主账本里写进去再删出来**

---

## 3. 从当前代码现实看，哪条更顺着地基

从当前 `Agent.Core` 现实看，same-context pop route 仍更顺着现有地基，  
但它对 forked-context route 的领先幅度已经明显缩小。

当前已经明确的事实是：

- `Micro-Wizard` v0 先走宿主侧 orchestrator
- 当前真实运行时仍建立在同一份 `RecentHistory` 上
- 现成原语已经是 `InjectActionContent(...)`、`ActionProduced`、`ToolExecutionCompleted`、`ToolAccessOverride`
- 还没有 built-in 的 `WizardInstance` / 独立 timeline / durable branch workspace

在这种情况下，same-context pop route 的施工切口更小，因为它主要要求补：

- `ContextFrame`
- `WizardSavepoint`
- tail rewrite primitive
- Wizard 期间的 compaction suppression
- 退出时 retained result contract

而 forked-context route 一旦认真做，当前主要会碰到：

- 除 `RecentHistory` 外还要复制哪些运行态
- `ToolSession` / app state 如何隔离
- 是否先用 snapshot clone，还是把现有 StateJournal fork/replay 能力更深地接进 `Agent.Core`
- durable wizard checkpoint 长什么样

因此当前真正的差距，已经从“底层容器会不会 fork”转移到：

- `Agent.Core` 有没有 branch-aware runtime API
- app/tool 有没有 branch participation contract
- 结果如何并回父时间线

---

## 4. 与上下文压缩的关系

这是两条路线差异非常大的一点。

### 4.1 Forked-context route

主上下文几乎天然更省心：

- Wizard 过程不进入主 `RecentHistory`
- 主上下文 compaction 不必面对“之后还要把这段 pop 掉”的语义冲突

但 Wizard 自己的临时上下文如果变长，仍然需要考虑局部压缩。

### 4.2 Same-context pop route

这里 compaction 是硬问题，因为：

- Wizard 中间过程先写进主账本
- 一旦被 compaction 吃进 `RecapEntry`
- 退出时即使 pop 掉尾部，也无法保证“详细过程真的被忘掉”

所以如果选这条路线，v0 我认为应当明确接受一条纪律：

- **Wizard active 期间禁止触发上下文压缩**

这会牺牲一部分可用上下文容量，但会显著降低实现复杂度，也更符合“先做一条稳主链”的阶段目标。

---

## 5. 与 StateJournal 的关系

### 5.1 Forked-context route

它与 StateJournal 的长期方向更契合，尤其是：

- sibling fork
- 共享 committed 前序历史
- 临时工作区演化

而且现在底层已经明显前进了一步：

- `DurableDeque<T>` / `DurableDeque` 已支持实例级 `ForkCommittedAsMutable()`
- `DurableOrderedDict` / `DurableText` 虽然还没有实例快路径，但已可走 `Repository.ReplayCommitted(..., ForceMutable)` fallback

因此“容器能力不够”已经不再是 forked-context route 的主要阻碍。

所以：

- **它与 StateJournal 的理念契合，而且底层可用性已明显提升**
- **但不等于今天就能自动得到完整的 branch workspace runtime**

### 5.2 Same-context pop route

它对 StateJournal 的要求仍更低。  
第一阶段甚至可以先不引入新的 durable fork 语义，只要把 savepoint / active wizard metadata 正式纳入宿主持久化边界即可。

因此从“尽快施工”角度看，它对当前持久层与宿主协议缺口仍更宽容。

---

## 6. 真正的复杂点都不只是 `RecentHistory`

这也是两份独立报告最一致的一点。

无论走哪条路线，真正需要保存/恢复的都不只是 history，还包括：

- `_pendingToolResults`
- `_turnRuntime`
- pending compaction 状态
- tool session 执行序号
- 某些 app-local runtime state

所以这次选型不应误读成：

- “A 路线是 history clone”
- “B 路线是 history pop”

更准确的说法应是：

- **A 路线是“临时工作区 + 结果回拼”**
- **B 路线是“同一运行态内的 savepoint + 受控尾部折叠”**

---

## 7. 审计与调试

### 7.1 Forked-context route 更自然

它更容易同时保留：

- 干净的主上下文
- 完整的 Wizard 审计过程

调试、复盘、训练数据采集都更舒服。

### 7.2 Same-context pop route 需要额外的审计旁路

因为它会主动从活跃上下文里删除细节。  
所以如果采用它，建议同步保留：

- debug log
- 独立 wizard trace
- 宿主层 telemetry

否则“主上下文干净”会以“调试能力变弱”为代价。

---

## 8. 当前阶段推荐

我的推荐是一个分层答案，不是单选题式的一刀切。

### 8.1 短流程首步推荐

如果目标是尽快做出一个：

- 单 active wizard
- 短流程
- 中间步骤较少
- 退出后只留一条结果

的第一版能力，我仍更推荐：

- **same-context pop route**

但要明确接受下面几条 v0 纪律：

1. 同时只允许一个 active wizard。
2. Wizard 期间禁止触发 context compression。
3. Wizard 中间 phase 尽量只读，或只做可重算的局部操作。
4. 真正外部副作用尽量推迟到 final commit phase。
5. savepoint 必须覆盖引擎运行态边界，而不只是 `RecentHistory.Count`。
6. 退出时必须走一个正式的 tail rewrite primitive，而不是宿主散拼内部 API。

### 8.2 长流程 / 强分支场景推荐

如果目标更接近下面这些形态，我现在更推荐直接认真考虑 forked-context route：

- 分支轨迹可能很长
- 可能经历多轮局部 compaction
- 需要递归 push / pop 或兄弟分支
- 需要更强的 branch audit / recovery
- 需要把“过程不污染父上下文”做成强语义

此时它的优势会明显超过额外复杂度。

### 8.3 两条路线的共同前置

无论最后优先落哪条路线，下面这些前置都值得先补：

- 已有稳定的 `WizardResultEnvelope`
- 已有 route-neutral 的 `ContextFrame` / `ContextSavepoint`
- 已清楚区分哪些 tool/app state 可隔离、哪些不可隔离
- 已有“结果进入父时间线前”的正式拦截/并回协议
- 已有 branch trace / recovery metadata

---

## 9. 若同时考虑 Thinking-Stack，推荐是否变化

把 Thinking-Stack 也放进来后，我的推荐会进一步偏向“按场景双路线并存”。

Thinking-Stack 的特点是：

- 更强调递归 push / pop
- 更可能要求多层 frame stack
- 更像模型自己操作的一组认知工具

这会带来两个影响：

1. 它进一步强化了“需要通用 savepoint / retained-result 原语”的必要性。
2. 它也提醒我们，长期来看系统会更像 branch workspace / thinking tree，而不只是单个 wizard 的清理机制。

因此综合 Micro-Wizard 与 Thinking-Stack 后，我的判断是：

- **共享基础原语**：仍应优先 route-neutral 的 `ContextFrame` / `ContextSavepoint` / `RetainedResultEnvelope`
- **短流程首步**：same-context pop route 仍然有优势
- **长流程与递归分支**：forked-context / branch-workspace 的优势现在更明确
- **命名与抽象层次**：不要只围绕 `WizardSavepoint` 命名，应优先抽 `ContextFrame` / `ContextSavepoint` / `RetainedResultEnvelope`

也就是说：

- Thinking-Stack 不要求所有事情一开始都用 fork-context
- 但它会更强烈地提醒我们：`pop-route` 不是长期唯一底座

---

## 10. 推荐的实施顺序

### 阶段 1

先在当前 `Agent.Core` 上实现 route-neutral 前置：

- `ContextFrame`
- `ContextSavepoint`
- retained result contract
- branch-aware audit metadata

### 阶段 2

并行选择一个真实场景压 shape。  
若场景是短流程 selective remember / repair，可先走：

- tail rewrite primitive
- Wizard active 期间 suppress compaction

若场景是长流程 branch workspace / recursive branch，可先走：

- forked snapshot workspace
- branch checkpoint / resume

例如：

- `view_file` selective remember
- `MemoTree` split 后 gist/summary repair
- Thinking-Stack 风格的局部分类讨论

### 阶段 3

补宿主层审计旁路：

- debug trace
- wizard summary
- crash recovery metadata

### 阶段 4

等行为 shape 稳定后，再把底层进一步优化到：

- 更深接入现有 StateJournal fork/replay 能力的 branch workspace
- 最终的 StateJournal sibling fork 形态

---

## 11. 最终推荐

如果你现在就要决定“下一项基础功能增强先做什么”，我的推荐是：

> **先补 route-neutral 的 frame/savepoint/result/branch-merge 基础设施；然后按场景选路：短流程优先 `same-context pop route`，长流程与递归分支优先 `forked-context route`。**

更具体地说：

- **轻量短流程**：savepoint + 同账本受控尾部折叠
- **重型长流程**：临时工作区 / forked context / 结果回拼
- **共同主线**：不要再把“底层容器能否 fork”当作主要瓶颈，应把重心转向 branch runtime API、app/tool participation contract、以及结果并回协议

这样既保留了 `pop-route` 的轻量优势，也承认了 `fork-context` 在长轨迹、递归分支、局部压缩与恢复上的明显优势。
