# Agent.Core Branching Infrastructure Backlog

状态：draft v0  
定位：整理一批**无论最终偏向 `fork-context route` 还是 `same-context pop route`，都值得补齐**的基础设施与运行时能力。它们不仅服务 Micro-Wizard，也服务 Thinking-Stack、fork agent、旁路推演、选择性保留结果、以及更强的运行时审计与恢复。

相关文档：
- `docs/Agent/micro-wizard-runtime-draft.md`
- `docs/Agent/micro-wizard-history-route-comparison.md`
- `docs/Agent/micro-wizard-history-forked-context-route-analysis.md`
- `docs/Agent/micro-wizard-history-pop-route-analysis.md`
- `docs/Agent/thinking-stack-draft.md`
- `docs/Agent/agent-core-micro-wizard-readiness-review.md`

相关代码：
- `prototypes/Agent.Core/AgentEngine.Persistence.cs`
- `prototypes/Agent.Core/Persistence/AgentEngineStateRoot.cs`
- `prototypes/Agent.Core/History/AgentState.cs`
- `prototypes/Agent.Core/IApp.cs`
- `prototypes/Completion.Tools/ToolSession.cs`

## 0. 一句话结论

如果只看某一条具体技术路线，很多基础设施看起来像“可选优化”。  
但如果把目标放宽到：

- Micro-Wizard
- Thinking-Stack / Thinking-Tree
- fork agent / branch workspace
- 旁路推演后只带结果回主上下文
- 更强的 crash recovery / audit / replay

那么会发现有一批基础设施几乎是**路线无关的公共投资**。  
它们越早补齐，设计空间越大，后续选路越从容。

---

## 1. 为什么值得单独立项

这批基础设施的价值，不在于“让某一条路线能勉强跑起来”，而在于：

1. 它们能把 `Agent.Core` 从“普通 tool loop + history”推进到“可显式管理局部工作区”的运行时。
2. 它们能减少后续在 Micro-Wizard、Thinking-Stack、fork agent 三条线上各自重复发明一遍机制。
3. 它们能把很多现在只能靠宿主散拼的行为，提升为正式 runtime 语义。
4. 它们能把“临时上下文 / 分支上下文 / 结果回拼”从技巧变成一等能力。

更直接地说：

- **哪怕最后主线先落 `pop-route`，这些基础设施也不会白做。**
- **哪怕最后主线转向 `fork-context`，这些基础设施也仍然是必需品。**

---

## 2. 这批基础设施想共同解锁什么

### 2.1 Micro-Wizard

- 多步局部流程
- 退出时忘掉中间细节，只保留结果
- 更可靠的修复、预演、摘要、选择性保留

### 2.2 Thinking-Stack / Thinking-Tree

- 递归 push / pop
- 保留分支结论而遗忘中间细节
- 维护当前活跃 frame stack / branch tree

### 2.3 fork agent / branch workspace

- 从当前运行态派生子 agent
- 在子分支中继续探索、调用工具、修改局部状态
- 最后选择丢弃、摘要返回、或结构化并回主上下文

### 2.4 更强的调试、恢复与训练数据

- 保留干净主时间线
- 旁路保留完整 branch trace
- crash 后恢复 active frame / branch
- 更清楚地区分“主线发生了什么”和“旁路试探了什么”

---

## 3. 路线无关、优先级高的基础设施缺口

下面这些能力，不管最终主线偏向哪条历史管理路线，都值得补。

### 3.1 `ContextFrame` / `ContextSavepoint` / `RuntimeCheckpoint`

这是最核心的一组共享抽象。

当前无论讨论 Wizard 还是 Thinking-Stack，都已经隐含需要：

- 一个 frame 身份
- 一个进入点边界
- 一份运行态 checkpoint
- 一个退出后的 retained result 契约

建议正式抽象出：

```text
ContextFrame
  FrameId
  Kind
  ParentFrameId?
  Savepoint
  Status
  AuditPolicy

ContextSavepoint
  AnchorEntrySerial
  AnchorHistoryCount
  TurnLock
  RuntimeCheckpoint
  CompactionPolicy

RuntimeCheckpoint
  PendingToolResults
  TurnRuntime
  PendingCompaction
  ToolSessionCheckpoint
```

它的价值是：

- 给 `pop-route` 提供正式 savepoint
- 给 `fork-context` 提供 branch entry contract
- 给 Thinking-Stack 提供递归 frame stack 的统一底座

### 3.2 正式的 `RetainedResultEnvelope`

很多真正复杂的问题，不是“怎么进局部流程”，而是：

- 退出时到底保留什么

因此建议尽早统一出一份结果契约，而不是每个场景都临时散拼：

```text
RetainedResultEnvelope
  Summary
  ResultEntries[]
  Outcome
  Notes?
  AuditRef?
```

它至少要能承接：

- Wizard 最终结果
- Thinking branch 的结论性返回
- branch workspace 的“带回父上下文的最小痕迹”

### 3.3 “结果进入主时间线前”的拦截 / 改写点

这是当前非常关键、但容易被低估的缺口。

现在工具结果一旦准备就绪，就会直接 append 到主时间线。  
这对普通 tool loop 没问题，但对：

- `view_file -> selective remember`
- 先看完整结果，再筛选保留
- branch 内部多步试探后只保留结论

就不够了。

建议正式提供一类拦截点，例如：

- `BeforeToolResultsAppend`
- `RewriteToolResultsForParentTimeline`
- `CommitRetainedResult`

否则很多高价值场景都会先污染主时间线，再谈清理。

### 3.4 app/tool 的 branch participation contract

当前 `IApp` / `ITool` 更像普通注册与执行接口。  
但如果 runtime 要开始支持 frame / branch / fork agent，就必须回答：

- 某个 app state 是共享只读，还是可分叉
- 某个 tool 是否允许在 branch 中运行
- 某个 tool 的副作用是否必须推迟到 commit phase
- 某个 app 是否需要自己的 fork / clone / checkpoint 参与协议

建议引入一层明确 contract，例如：

- `SharedReadOnly`
- `Forkable`
- `BranchLocal`
- `CommitPhaseOnly`
- `NotBranchSafe`

没有这层 contract，很多 fork agent 只能停留在“history fork 了，但真实工具世界没 fork”。

### 3.5 副作用分级与 commit discipline

无论走哪条路线，真正不能含糊的都是副作用问题。

至少应区分：

- 纯读取
- 本地可丢弃试算
- 本地 durable 但可 branch-local
- 外部不可回滚副作用

并给出清晰纪律：

- 哪些工具可在 branch 中自由执行
- 哪些工具只能在 final commit phase 执行
- 哪些工具必须由宿主额外事务化

否则“可以回到父上下文”很容易制造回滚幻觉。

### 3.6 branch / frame 的审计与调试旁路

如果系统开始支持“父上下文干净、旁路过程可丢弃”，那就必须同步补：

- branch trace
- frame tree metadata
- result merge log
- dropped span / discarded branch 的诊断线索

否则可用性会被“调试能力骤降”拖住。

### 3.7 branch / frame 的 crash recovery 语义

一旦 frame / branch 成为正式 runtime 能力，就不能只停留在“内存里先跑一跑”。

至少要回答：

- crash 后如何知道有一个 active frame
- 如何知道它是 wizard、thinking branch 还是 fork agent
- 如何恢复它的 entry point / runtime checkpoint / pending phase
- 恢复后是继续、回滚、还是标记中断

这并不要求第一天就做完整 durable workflow engine，  
但必须尽早定义恢复语义边界。

---

## 4. 更偏向 `fork-context` 的关键基础设施

如果项目往 fork agent / branch workspace 方向继续推进，下面这些能力会变得尤其重要。

### 4.1 branch workspace 的一等 API

当前 `AgentEngine` 已能导出快照并重建，但这还不是正式的 branch workspace API。  
更理想的形态应接近：

```text
ForkBranch(...)
ResumeBranch(...)
CommitBranchResult(...)
AbandonBranch(...)
```

这层 API 应该表达的是 runtime 语义，而不是只暴露底层 snapshot 技巧。

### 4.2 从 snapshot codec 走向更 branch-friendly 的 durable shape

当前持久化更像：

- 导出 `AgentEngineStateSnapshot`
- 重新编码整套 durable 对象图

这让 StateJournal fork 即使补齐了容器能力，也暂时吃不满红利。

长期更理想的方向是：

- branch workspace 直接建立在 durable graph root 上
- 子分支可共享 committed 前序历史
- branch checkpoint 更像一等 durable 对象，而不是纯 codec 往返

### 4.3 app-local durable state 的 sibling fork 协议

如果 fork agent 要真的强大，历史之外的 durable state 也应尽量进入同一分支语义：

- Memory Notebook 类数据
- 其他 StateJournal-backed app data
- 未来的 memo / derived memory / local repositories

这要求这些对象能参与：

- branch fork
- branch-local mutation
- commit / discard

### 4.4 frame stack / branch tree 元数据

Thinking-Stack 与 fork agent 都不只需要“当前有一个局部流程”，而是很可能需要：

- 父子 frame 关系
- 活跃栈
- 已完成兄弟分支
- branch outcome 索引
- 可恢复的 branch tree

这比“单个 active wizard”更强。

---

## 5. 更偏向 `pop-route` 的关键基础设施

即使最后先走 `pop-route`，下面这些能力也不应忽略。

### 5.1 正式的 tail rewrite primitive

当前 `AgentState` 已有 prefix recap，但没有正式 tail rewrite。  
如果要把 `pop-route` 做成可靠能力，而不是宿主手工删尾巴，建议显式提供：

```text
RewriteRecentHistoryTail(anchorSerial, replacementEntries)
```

### 5.2 savepoint 有效性校验

`pop-route` 下不能只记 `RecentHistory.Count`。  
至少要校验：

- anchor serial
- history count
- 当前 turn lock
- pending compaction 状态
- runtime checkpoint

否则 savepoint 很容易在复杂流程里悄悄失效。

### 5.3 与 compaction 的协作纪律

`pop-route` 下 compaction 是第一等问题。  
如果不提前制度化，很容易出现：

- wizard 中间过程被 recap 吃掉
- 退出时历史虽然回退，但 recap 中残留了过程痕迹

因此即使只是阶段性主线，也应把：

- frame active 时是否 suppress compaction
- 哪些 frame 允许局部 compaction
- 何时恢复自动 compaction

写成正式运行时语义。

---

## 6. 当前代码现实下，最值得优先补的重构点

### 6.1 把“运行态边界”从散落私有字段提升为正式对象

当前很多关键运行态散落在：

- `_pendingToolResults`
- `_turnRuntime`
- `_compactionRequest`
- `ToolSessionExecutionSequence`

它们已经能被 snapshot 保存，但还没有一个更直接的“branchable runtime bundle”抽象。

建议重构出一层更明确的对象，例如：

- `EngineRuntimeCheckpoint`
- `ActiveFrameRuntime`
- `BranchWorkspaceState`

这会同时帮助：

- savepoint
- fork branch
- crash recovery
- 调试与测试

### 6.2 给 `IApp` / tool host 增加 branch 参与协议

当前 `IApp` 只有 render + tools，不足以表达：

- 如何 fork 自己的局部状态
- 是否允许 branch 内调用
- 是否需要 commit/discard 回调

建议先不急着把它做得很重，但至少应给出一条参与边界。

### 6.3 把“普通 tool loop”与“局部工作区 runtime”正式区分

现在 `Agent.Core` 已经有强工具循环基础。  
接下来值得做的不是推翻它，而是额外抽一层：

- 普通主线 tool loop
- branch/frame-local tool loop

这样 Micro-Wizard、Thinking-Stack、fork agent 都能以同一 runtime idiom 生长出来。

### 6.4 给宿主明确的 branch-safe 扩展点

当前事件面已经不错，但还可以继续往“branch runtime 友好”方向长：

- 进入 frame 前
- 子分支提交结果前
- 工具结果进入父时间线前
- branch abandon 后
- frame 状态切换后

这些扩展点越明确，宿主越不需要靠内部 API 散拼。

---

## 7. 哪些能力一补上，fork agent 就已经“够强了”

如果先不追求最终 durable sibling fork，而是追求“伸手可够到的第一版 fork agent”，我认为做到下面这些就已经很强：

1. 有正式 `ContextFrame` / `ContextSavepoint` / `RetainedResultEnvelope`。
2. `AgentEngine` 能从当前状态派生一个 branch engine，并保留 parent/child 关系元数据。
3. 有 branch-aware 的结果并回协议，而不是只会把完整结果直接写进父时间线。
4. 有 app/tool 的最小 branch participation contract。
5. 有副作用分级纪律。
6. 有 branch trace / audit / recovery metadata。

做到这里，即使底层一开始还是：

- snapshot clone
- 宿主侧 branch manager
- 轻量 durable checkpoint

它也已经不只是“实验技巧”，而是可以真正支持：

- fork agent
- selective remember
- 旁路试探
- 思维分支折叠

的强能力。

---

## 8. 推荐的施工顺序

### 阶段 1：先统一抽象名词与协议

优先补：

- `ContextFrame`
- `ContextSavepoint`
- `RuntimeCheckpoint`
- `RetainedResultEnvelope`
- 副作用分级
- app/tool branch participation contract

这是最值得先做的一步，因为它最能避免后续各条能力线各起炉灶。

### 阶段 2：补主时间线并回与审计能力

优先补：

- tool result append 前拦截点
- retained result commit protocol
- branch trace / audit metadata

这一步一旦到位，很多“先污染后清理”的 awkward 流程就会明显改善。

### 阶段 3：补 branchable runtime bundle

优先补：

- runtime checkpoint 对象化
- frame lifecycle
- crash recovery metadata
- 宿主侧 branch manager

到这里，第一版 fork agent 就已经很近了。

### 阶段 4：先落一个语义级 fork agent

先不要求最终 durable sibling fork。  
可以先用：

- snapshot clone
- branch-local tool gating
- result merge protocol

做出一个真正可用的 fork agent / branch workspace。

### 阶段 5：最后再把持久层推进到真正的 branch workspace 正形

当上层 shape 稳定后，再投入到：

- StateJournal sibling fork
- app-local durable branch participation
- 更 branch-friendly 的 durable root shape
- 完整 branch recovery

这时工程投入会更有把握。

---

## 9. 最终判断

当前最值得推进的，不是先把“到底选 `fork-context` 还是 `pop-route`”辩到极致，  
而是先把那些**无论哪条路线都能放大设计空间**的基础设施补出来。

尤其是下面这几类：

- frame / savepoint / checkpoint 统一抽象
- retained result 契约
- tool result 进入主时间线前的拦截点
- app/tool 的 branch participation contract
- 副作用分级
- branch trace / recovery metadata

这些能力一旦补齐，系统就会自然更接近：

- 更强的 Micro-Wizard
- 可递归的 Thinking-Stack
- 真正可用的 fork agent / branch workspace

而且到那时，路线选择本身也会变得更从容。
