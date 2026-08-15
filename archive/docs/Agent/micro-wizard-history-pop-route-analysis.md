# Micro-Wizard History Pop Route Analysis

状态：draft v2  
范围：专门分析“在同一份上下文上运行 Wizard，记录进入 Wizard 时的刻度/游标，退出时 pop 掉 Wizard 区间详细步骤，只把最终结果留在主上下文中”这一技术路线。  
非目标：本文**不**分析“创建临时上下文后再拼接回主上下文”的路线。

相关背景：
- 原理验证原型：`prototypes/MutableContextAgentProto/Phase2Commands.cs`
- 当前运行时收口：`docs/Agent/micro-wizard-runtime-draft.md`
- DSL 草图：`docs/Agent/micro-wizard-dsl-sketch.md`
- 持久化对象模型：`docs/StateJournal/usage-guide.md`
- 施工前审阅：`docs/Agent/agent-core-micro-wizard-readiness-review.md`

## 0. 一句话结论

这条路线在当前阶段**可接受，而且在短流程场景下仍然是更轻、更快的一条路**。  
它与“`Agent.Core` 以同一份 `RecentHistory` 事件账本为核心”的设计方向是相容的，但真正的复杂点不在“记录一个游标”，而在：

- 如何让 `RecentHistory` 的回退与引擎短生命周期状态一起恢复
- 如何避免 Wizard 中间步骤被 context compression 提前吃进摘要
- 如何处理 Wizard 期间工具与运行时对外部状态产生的副作用

如果愿意接受一组明确收紧的 v0 纪律，这条路线是能做的；  
但在 StateJournal 的 fork / replay 基础能力明显增强之后，它更应该被理解为：

- **短流程、低摩擦、快速落地的路线**
- 而不是未来所有 branch / wizard / thinking stack 统一共用的唯一终局底座

## 1. 这条路线的核心机制

核心思路很直接：

1. 进入 Wizard 时，不复制 `RecentHistory`，而是在当前账本上打一个 savepoint。
2. Wizard 后续多步仍然正常写入同一份 `RecentHistory`。
3. Wizard 成功退出时，回到 savepoint，把 savepoint 之后的细节步骤整体移除。
4. 然后只追加一小段“最终保留结果”到主时间线。

如果把它抽象成一个宿主侧协议，大致会像这样：

```text
EnterWizard:
  capture WizardSavepoint
    - history anchor
    - engine runtime anchor
    - wizard bindings / scratchpad
    - compaction suppression flag

DuringWizard:
  steps append into same RecentHistory
  phase gating / injection / tool loop all still use Agent.Core primitives

CommitWizard:
  validate savepoint still valid
  synthesize kept result entries
  truncate tail back to anchor
  append kept result entries
  clear wizard runtime state
```

这里最关键的不是“记 index”，而是要把 savepoint 设计成一份**可验证、可恢复、可持久化的边界对象**。

## 2. 与当前 `Agent.Core` / Micro-Wizard 收口后的设计是否相容

### 2.1 相容，而且直觉上很顺

当前 `Agent.Core` 已明确收口为：

- `full-feature-only runtime`
- `RecentHistory` 是事件账本，不是 provider message log
- `Micro-Wizard` v0 先走宿主侧 orchestrator
- 第一版真实运行时仍建立在同一份 `RecentHistory` 上

在这个前提下，“同一份上下文内运行，再把中间步骤 pop 掉”并不违背主方向，反而顺着当前地基：

- `InjectActionContent(...)` 本来就是往同一份账本里加 `InjectionEntry`
- `PrepareInvocationAsync.ToolAccessOverride` 本来就是同一引擎内的 phase 级工具收紧
- `ActionProduced` / `ToolExecutionCompleted` 本来就是主时间线事件

也就是说，这条路线并不要求先引入第二条 wizard timeline。

### 2.2 但它要求 `RecentHistory` 从“追加式账本”进一步升级为“支持受控尾部重写的账本”

当前 `AgentState` 已经有一个重要事实：

- 它不是 provider message log，而是可投影、可反射的事件账本

但当前真正已有的“改写历史”能力只有一类：

- `ReplacePrefixWithRecap(...)` 把 prefix 替换成一条 `RecapEntry`

这说明设计方向并不排斥“受控重写”，但也说明：

- 现有原语偏向“压缩前缀”
- 还没有“回退尾部到某个 savepoint，再追加替代结果”的正式 primitive

所以这条路线与当前设计**相容**，但它不是“拿现有 API 拼一下就行”，而是需要新增一类明确的 history rewrite primitive。

## 3. 最自然的实现形状

### 3.1 建议把 savepoint 设计成比“history index”更强的对象

单纯记一个 `RecentHistory.Count` 不够稳。  
更合适的是：

```text
WizardSavepoint
  AnchorEntrySerial
  AnchorHistoryCount
  EntryState
  TurnLock
  PendingCompactionSuppressed
  RuntimeCheckpoint
```

原因：

- `AnchorEntrySerial` 比纯 index 更适合做一致性校验
- `AnchorHistoryCount` 适合快速检测是否出现意外改写
- `EntryState` 需要知道进入 Wizard 时引擎正处在哪个主状态
- `TurnLock` 需要知道当前 turn 的 `CompletionDescriptor`
- `RuntimeCheckpoint` 需要覆盖 `_pendingToolResults`、`_turnRuntime`、以及可能的 pending compaction 状态

### 3.2 建议提供一个原子化的 history rewrite primitive

如果这条路线要正式落地，我认为需要类似下面这种原语：

```text
RewriteRecentHistoryTail(
  anchorSerial,
  replacementEntries
)
```

其语义应该是：

- 验证 `anchorSerial` 仍位于当前尾部之前
- 删除 anchor 之后的所有条目
- 重新校验追加顺序
- 一次性追加替代结果条目

这样 Wizard 退出不会退化成宿主随手拼几次内部 API。

### 3.3 更适合“只保留结果”的 retained result 形状

这条路线的关键，不是简单“删掉尾巴”，而是“删掉尾巴后留什么”。

从当前设计看，v0 更适合留下的是：

- 一条结果性 `ToolResultsEntry`
- 或一条结果性 `ObservationEntry`
- 必要时追加一条结果性 `InjectionEntry`

而不适合把整段 wizard 细节再压成一长串伪历史。  
否则就失去了“把多步局部流程折叠成一个最终结果”的意义。

## 4. 实现复杂度分析

### 4.1 `RecentHistory` 本身的复杂度：中等

只看 history 层，这条路线并不算离谱，原因是：

- `AgentState` 已经承认 history 可以被投影和受控改写
- 已有 `Serial`
- 已有 `ReplacePrefixWithRecap(...)` 这类 rewrite precedent

真正要补的主要是：

- savepoint 数据结构
- tail rewrite primitive
- savepoint 有效性校验

所以如果只谈 `RecentHistory`，复杂度是**中等**。

### 4.2 加上引擎运行态后，复杂度会升到中高

真正容易低估的，是 `AgentEngine` 里并不只有 history：

- `_pendingToolResults`
- `_turnRuntime.ResolvedProfile`
- `_turnRuntime.ActiveToolExecutionProfile`
- `_turnRuntime.LockedCompactionSplitIndex`
- `_turnRuntime.CurrentTurnFullFeatureEnabled`
- `_compactionRequest`

只 pop `RecentHistory` 并不自动恢复这些状态。  
因此这条路线若要可靠，需要把“保存并恢复 RecentHistory”升级成：

- **保存并恢复 Wizard 进入点的运行时边界**

这会让实现复杂度比看上去高一截。

## 5. 与同一份 `RecentHistory` 账本模型的契合度

### 5.1 很契合

这条路线最强的一点，就是它完全尊重当前的核心事实：

- `RecentHistory` 是唯一的活跃上下文事实源
- wizard 过程本身也是真实发生过的事件
- 只是其中一部分事件在 commit 后不再保留在活跃上下文里

这和当前 `Agent.Core` 的心智模型比“再造第二条临时时间线”更统一。

### 5.2 但它会把账本从“自然增长”推进到“显式可回退”

这不是坏事，但需要正式承认：

- `RecentHistory` 不再只是支持 append 与 prefix recap
- 它还要支持“受控的尾部折叠”

一旦接受这点，后续很多语义都要跟上：

- savepoint 如何编号
- 回退后 serial 是否允许出现空洞
- 调试面是否需要记录“被折叠掉的区间”

我认为 serial 出现空洞并不是问题。  
真正需要的是不要偷偷做，而要把“尾部折叠”写成正式语义。

## 6. 与上下文压缩的协作问题

这是这条路线最关键的专题之一。

### 6.1 为什么 compaction 是硬问题

如果 Wizard 在同一份 `RecentHistory` 上运行，而压缩同时仍可触发，那么可能出现：

1. Wizard 中间步骤已经写入 history
2. auto/manual compaction 在 Wizard 期间触发
3. compaction 把一部分 Wizard 中间步骤摘要进 `RecapEntry`
4. Wizard 结束时虽然 pop 掉了尾部细节，但早先摘要里已经残留了 Wizard 过程

这样就破坏了“离开 Wizard 后只留下结果，忘掉中间过程”的目标。

### 6.2 “Wizard 期间禁用上下文压缩（不触发）”是非常有效的简化策略

这是这条路线里我最赞同的一条简化纪律。

它的直接收益是：

- savepoint 不会被 prefix recap 穿透
- 不用处理“要不要从 Recap 里挖掉 Wizard 痕迹”
- `anchorSerial` / `anchorHistoryCount` 的稳定性更强
- Wizard exit 的忘却语义变得可信

换句话说，这个简化策略并不是小修小补，而是明显降低实现复杂度的主开关。

### 6.3 代价也很明确

代价是：

- Wizard 期间可用上下文容量变窄
- 长 Wizard 更容易顶到 soft context cap
- 某些本来希望“边做边压缩”的流程不能这么干

特别是在当前实现里：

- auto compaction 是由 `SoftContextTokenCap` 命中后触发的
- 如果 Wizard 期间完全不允许触发 compaction，就需要宿主提前预估 Wizard 长度

这意味着 v0 更适合：

- 短流程
- 少量工具往返
- 很快就能折叠回一个结果的 Wizard

而不适合超长的 repair / research 流程。

### 6.4 对当前阶段的判断

在当前阶段，我认为：

- **Wizard 期间禁用压缩，是值得采用的 v0 规则**

它不是最优雅的最终方案，但很适合当前“先做创新点、先做一条稳主链”的收口。

### 6.5 与当前 StateJournal 新能力结合后的判断

当底层已经具备：

- `DurableDeque<T>` / `DurableDeque` 的 `ForkCommittedAsMutable()`
- `Repository.ReplayCommitted(..., ForceMutable)` 这条 committed clone fallback

之后，`pop-route` 的优势就更清楚地收敛为：

- 不需要额外 branch workspace
- 初始实现切口更小
- 对短流程 selective remember / repair 非常顺手

而它的劣势也更清楚地暴露为：

- 长轨迹期间很难与 compaction 优雅协作
- 嵌套 / 递归 frame 会快速抬高复杂度
- 它不擅长承载“局部分支自己也可能跑很久、甚至反复压缩”的工作区语义

## 7. 对 tool / session / runtime state 的影响

### 7.1 Tool/session 层本身不难共享

这条路线的一个优点是：

- 不需要为 Wizard 另建一套 tool session
- 仍然使用同一个 `ToolRegistry` / `ToolSession`
- phase gating 继续靠 `ToolAccessOverride`

也就是说，工具可见性本身不是它的难点。

### 7.2 真正的难点是“工具结果缓存”和“退出时的主时间线形状”

当前引擎有 `_pendingToolResults`。  
如果 Wizard 是在“模型刚发出一个工具调用”后接管，那么 Wizard 内部可能还会产生更多 action/tool 往返。

退出时需要回答：

- 这些 wizard 期间的 `_pendingToolResults` 如何清空
- 主时间线最终保留的是哪一个 `ToolResultsEntry`
- 原始触发 Wizard 的那个 action/tool call，在主时间线里如何看起来像“只发生了一次工具往返”

这说明同一账本路线虽然不需要复制上下文，但仍然需要一套很清楚的“折叠后主时间线形状”协议。

### 7.3 外部副作用是比 history 更大的风险

这是这条路线最容易踩坑的地方。

pop `RecentHistory` 只会忘掉上下文里的过程，**不会自动回滚外部副作用**。  
如果 Wizard 中间工具已经：

- 改了文件
- 改了 `Memory Notebook`
- 改了 `StateJournal` durable object
- 发出了真实外部 I/O

那么即使 history 被 pop，世界状态也不会跟着恢复。

因此这条路线天然要求：

- Wizard 中间 phase 尽量只做只读、可重算、或可撤销操作
- 真正有副作用的写入尽量推迟到 final commit phase
- 如果必须中途写 durable state，就要靠领域对象自身支持 savepoint/rollback/fork

所以，**它解决的是上下文折叠问题，不是通用事务回滚问题**。

## 8. 持久化与恢复语义

### 8.1 单进程内短流程时，问题还不大

如果 v0 假设：

- 单 active wizard
- 宿主进程持续存活
- wizard 生命周期很短

那么很多复杂性可以暂时留在内存里。

### 8.2 一旦要支持 crash recovery，就必须持久化 savepoint

因为当前 repo-backed 持久化会在 step 稳定边界提交。  
如果 Wizard 进行到一半进程崩掉，那么恢复时可能看到：

- 已经持久化的 wizard 中间 history
- 但宿主内存里的 wizard state 已经没了

这样系统就不知道这些中间步骤是：

- 应该继续执行
- 应该折叠回滚
- 还是应该当作正常主历史保留

所以如果想要“可靠恢复”，至少要持久化：

- wizard active 标志
- savepoint anchor
- 进入时的运行态 checkpoint
- 当前 phase / scratchpad / bindings

### 8.3 最难的是“退出时的原子性”

Wizard exit 不是单纯的 append，而是：

1. 回退 history
2. 写入 retained result
3. 清理 wizard metadata

这几步如果不在同一个持久化边界里完成，崩溃后会留下半成品。  
所以真正工程化时，需要把“exit rewrite”视为一次单独的状态事务。

## 9. 审计与调试

### 9.1 对主上下文很友好

如果目标是让主上下文保持干净，这条路线很有效：

- 主 `RecentHistory` 最终只留下可消费结果
- 不会把 Wizard 细节长期留给后续 LLM 反复看到

### 9.2 对调试与审计不够天然友好

因为它会主动删掉细节。  
一旦 Wizard 出问题，单靠主 history 很难还原：

- 中间看了什么
- 哪一步失败
- 为什么 repair

因此如果采用这条路线，建议同步保留一种**脱离主上下文的调试审计面**，例如：

- debug log
- 独立 wizard trace
- 持久化的 host telemetry

重点是：

- 审计可以保留
- 但不要继续污染主 `RecentHistory`

## 10. 未来扩展性

### 10.1 适合单实例、短流程的 v0

这条路线最适合：

- 单 active wizard
- 不嵌套
- 不并发
- 生命周期短

### 10.2 一旦要多实例或嵌套，复杂度会明显上升

因为那时需要处理：

- savepoint 栈
- 哪个 wizard 可以 pop 到哪里
- compaction suppression 的嵌套计数
- 外部副作用的作用域

所以我不建议把这条路线直接设计成面向并发/嵌套/长轨迹的通用机制。  
它更像一条适合 v0 以及短流程场景的受控主线。

## 11. 主要风险与容易踩坑处

最主要的风险有这些：

1. 只回退 `RecentHistory`，却忘了恢复 `_pendingToolResults` / `_turnRuntime` / `_compactionRequest`。
2. Wizard 期间触发 compaction，导致被“想忘掉的中间过程”提前混入 `RecapEntry`。
3. Wizard 工具已经改了外部 durable state，但 history pop 并不会自动回滚它们。
4. 退出时 retained result 形状不清楚，导致主时间线不再像一次正常的工具往返。
5. savepoint 只记 index，不记 serial / lock / engine state，恢复校验太脆。
6. 崩溃恢复时只有污染过的 history，没有 wizard metadata，系统无法判定应继续还是应折叠。
7. 为了保留调试能力，把完整 wizard trace 又塞回主 history，抵消了这条路线的核心收益。

## 12. 最适合的实施阶段

这条路线不适合一上来就做成“最终态基础设施”。  
它更适合：

- **Micro-Wizard v0.5 到 v1 之间**

也就是：

- 已经有单实例 host-side orchestrator
- 已经有真实 wizard 场景
- 已经确定“主时间线最后应该保留什么结果”
- 但还不想为“临时上下文 + fork”体系付出更多状态克隆工程

如果现在直接做，我建议把目标收得很窄：

- 只支持单 active wizard
- 只支持短流程
- wizard 期间禁用 compaction
- 中间 phase 尽量只读
- 退出时只保留一条结果性 observation/tool-result

## 13. 推荐落地顺序

1. 先定义 `WizardSavepoint`，把需要保存的 history / runtime 边界写清楚。
2. 在 `AgentState` 增加正式的 tail rewrite primitive，而不是让宿主拼内部细节。
3. 在 `AgentEngine` 或宿主 orchestrator 层增加“wizard active 时 suppress compaction”的硬规则。
4. 先只支持单 active wizard，且不支持嵌套。
5. 先挑一个中间过程基本只读、最终结果容易合成的 wizard 场景试点。
6. 跑通后再决定是否要把 wizard savepoint / phase metadata 纳入 repo-backed 持久化恢复。

## 14. 简洁结论

在当前阶段，这条路线的结论是：**可接受，而且对短流程仍然很有吸引力**。  
它与当前 `Agent.Core` 的同账本设计是相容的，也有机会比“复制一份临时上下文”更直接；但前提是要接受一组收紧纪律，尤其是：

- Wizard 期间禁用上下文压缩
- 只做单实例短流程
- 明确区分“上下文折叠”和“外部副作用回滚”不是一回事

如果不愿意接受这些纪律，或者目标场景本身是：

- 很长的局部分支轨迹
- 会经历多次局部压缩
- 递归 push / pop 很重
- 需要更强的 branch recovery / audit

那么此时更值得认真考虑 `fork-context route`。
