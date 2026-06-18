# Micro-Wizard History Route Comparison

状态：draft v1  
范围：综合比较两条“进入 Micro-Wizard 后保留最终结果、忘掉详细中间过程”的技术路线，并给出当前阶段推荐。

相关文档：
- `docs/Agent/micro-wizard-history-forked-context-route-analysis.md`
- `docs/Agent/micro-wizard-history-pop-route-analysis.md`
- `docs/Agent/micro-wizard-runtime-draft.md`
- `docs/Agent/thinking-stack-draft.md`
- `docs/Agent/agent-core-micro-wizard-readiness-review.md`
- `prototypes/MutableContextAgentProto/Phase2Commands.cs`

## 0. 一句话结论

如果讨论的是**目标设计的语义纯度**，我更偏向“fork 临时上下文，结束后只把结果拼回主上下文”。  
如果讨论的是**当前阶段最值得先落地的首步实现路线**，我更推荐“同一份上下文内运行 Wizard，记录 savepoint，退出时 pop 掉细节并只留下结果”。

也就是说：

- **长期目标设计**：更偏向 forked-context route
- **当前第一步施工**：更偏向 same-context pop route

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

从当前 `Agent.Core` 现实看，same-context pop route 更顺着地基。

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

而 forked-context route 一旦认真做，就会立刻碰到：

- 除 `RecentHistory` 外还要复制哪些运行态
- `ToolSession` / app state 如何隔离
- 是否先用 snapshot clone，还是直接上 StateJournal sibling fork
- durable wizard checkpoint 长什么样

因此在“下一项基础功能增强”这个节奏下，我认为首步不该直接跳到 forked-context route。

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

但问题在于当前 `AgentEngine` 的 durable state 还是 snapshot codec 形状，且 `history` / `pending notifications` 现在都落在 `DurableDeque` 上，而 `DurableDeque` 还没有 public `ForkCommittedAsMutable()`。

所以：

- **它与 StateJournal 的理念契合**
- **但不等于今天就能靠现成 public fork API 顺手做完**

### 5.2 Same-context pop route

它对 StateJournal 的要求更低。  
第一阶段甚至可以先不引入新的 durable fork 语义，只要把 savepoint / active wizard metadata 正式纳入宿主持久化边界即可。

因此从“尽快施工”角度看，它对当前持久层缺口更宽容。

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

### 8.1 首步实现推荐

当前第一步我更推荐：

- **same-context pop route**

但要明确接受下面几条 v0 纪律：

1. 同时只允许一个 active wizard。
2. Wizard 期间禁止触发 context compression。
3. Wizard 中间 phase 尽量只读，或只做可重算的局部操作。
4. 真正外部副作用尽量推迟到 final commit phase。
5. savepoint 必须覆盖引擎运行态边界，而不只是 `RecentHistory.Count`。
6. 退出时必须走一个正式的 tail rewrite primitive，而不是宿主散拼内部 API。

### 8.2 中期演进推荐

当以下条件成立后，再把系统推进到 forked-context route 会更自然：

- 已有稳定的 `WizardResultEnvelope`
- 已有一两个真实 wizard 场景跑通
- 已清楚区分哪些 tool/app state 可隔离、哪些不可隔离
- 持久化层愿意为 wizard checkpoint 或 sibling fork 付工程成本

到那个阶段，forked-context route 会更像系统的“长期正形”。

---

## 9. 若同时考虑 Thinking-Stack，推荐是否变化

把 Thinking-Stack 也放进来后，我的推荐不会反转，但表达要更精细一些。

Thinking-Stack 的特点是：

- 更强调递归 push / pop
- 更可能要求多层 frame stack
- 更像模型自己操作的一组认知工具

这会带来两个影响：

1. 它进一步强化了“需要通用 savepoint / retained-result 原语”的必要性。
2. 它也提醒我们，长期来看系统会更像 branch workspace / thinking tree，而不只是单个 wizard 的清理机制。

因此综合 Micro-Wizard 与 Thinking-Stack 后，我的判断是：

- **共享首步原语**：仍优先 same-context pop route
- **长期总设计方向**：更明确地保留向 forked-context / branch-workspace 演进的空间
- **命名与抽象层次**：不要只围绕 `WizardSavepoint` 命名，应优先抽 `ContextFrame` / `ContextSavepoint` / `RetainedResultEnvelope`

也就是说：

- Thinking-Stack 不会推翻当前首步推荐
- 但它会进一步要求我们不要把首步实现误写成终局

---

## 10. 推荐的实施顺序

### 阶段 1

先在当前 `Agent.Core` 上实现：

- `ContextFrame`
- `ContextSavepoint`
- tail rewrite primitive
- Wizard active 期间 suppress compaction
- retained result contract

### 阶段 2

只落一个真实场景，例如：

- `view_file` selective remember
- 或 MemoTree split 后 gist/summary repair

### 阶段 3

补宿主层审计旁路：

- debug trace
- wizard summary
- crash recovery metadata

### 阶段 4

等行为 shape 稳定后，再评估是否升级到：

- forked snapshot workspace
- durable wizard checkpoint
- 最终的 StateJournal sibling fork 形态

---

## 11. 最终推荐

如果你现在就要决定“下一项基础功能增强先做什么”，我的推荐是：

> **先做 same-context pop route，但把它明确设计成通往 forked-context route 的阶段性实现，而不是终局。**

更具体地说：

- **当前首步实现**：savepoint + 同账本受控尾部折叠
- **长期目标设计**：临时工作区 / forked context / 结果回拼

这样既顺着当前 `Agent.Core` 的施工地基，也不会把长期设计方向锁死在“主账本里写进去再删出来”的语义上。
