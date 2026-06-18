# Micro-Wizard History Forked-Context Route Analysis

状态：draft v1  
范围：仅分析“进入 Micro-Wizard 时保存主上下文，Wizard 在临时上下文中运行，退出时恢复主上下文并只留下最终结果”这一路线。  
不包含：同一份上下文内记录游标并在退出时 pop 回退的路线。

相关背景：
- `prototypes/MutableContextAgentProto/Phase2Commands.cs`
- `docs/Agent/micro-wizard-runtime-draft.md`
- `docs/Agent/micro-wizard-dsl-sketch.md`
- `docs/StateJournal/usage-guide.md`
- `docs/Agent/agent-core-micro-wizard-readiness-review.md`

## 0. 一句话结论

这条路线在**目标语义**上很强，也与“Wizard 过程不长期污染主 Context”的设计目标高度一致。  
但按当前代码现实看，它更适合作为**第二阶段增强路线**，而不适合直接当作第一步最低成本实现。

如果要用一句最简短的话概括我的判断：

**当前阶段结论：可接受。**

不是不推荐；但也不应误判为“最短施工路径”。

## 1. 核心机制

这条路线的核心，不是“在主 `RecentHistory` 里做可逆编辑”，而是把 Wizard 视为一个临时执行分支：

1. 进入 Wizard 前，保存主会话状态。
2. 基于该状态创建一份临时上下文。
3. Wizard 的多步 LLM / tool / repair 流程只发生在临时上下文中。
4. Wizard 完成后，从临时上下文抽取一个结果性产物。
5. 主上下文恢复到进入 Wizard 前的状态。
6. 只把最终结果重新拼回主上下文。

从上下文语义上看，它等价于：

```text
主时间线
  -> 进入 wizard
  -> 派生临时上下文并运行多步过程
  -> 丢弃中间细节
  -> 只把结果性痕迹写回主时间线
```

`Phase2Commands.RunViewFileWizardAsync` 已经给出了这个思路的原理验证版：

- 主时间线先产生 `view_file` 调用
- Wizard 侧拿完整文件内容继续做 `select_remember`
- 主时间线最后只保留“被改写后的精简 tool result”
- Wizard 内部那段“完整查看 -> 选择保留”的中间历史不会进入主时间线

因此，这条路线的本质不是“压缩主历史”，而是“**把 Wizard 过程放到侧向执行上下文里，再把结果合并回主历史**”。

## 2. 与当前 Agent.Core / Micro-Wizard 收口设计的相容性

### 2.1 与目标设计相容

它与当前 Micro-Wizard 的目标设计是相容的，原因很直接：

- 目标设计本来就强调 Wizard 是受控短流程
- 目标设计希望结果固化、过程可清理
- 目标设计不希望主 `RecentHistory` 长期背负大量局部修复细节

从这个角度看，forked-context route 甚至比“同一账本内再回退”更贴近“局部工作区”的直觉模型。

### 2.2 与当前 v0 落地策略不完全同路

但它与当前 `micro-wizard-runtime-draft.md` 里偏向的 v0 施工路线并不完全一致。

当前运行时草案更偏向：

- 宿主侧 orchestrator
- 同一份 `RecentHistory`
- `InjectionEntry` + tool gating
- 单实例 active wizard

换句话说：

- **目标设计层面**，这条路线是相容的
- **第一步施工层面**，它不是当前文档所偏向的最短路径

如果采用这条路线，应明确把它定位成：

- Wizard runtime 的中期增强形态
- 或某些“强隔离型 wizard”的专用运行模式

而不应把它误写成“所有 wizard 都必须先这样实现”。

## 3. 实现复杂度判断

这条路线的复杂度，关键不在“复制一段历史”本身，而在“**除了历史，还要复制什么才算真隔离**”。

按当前代码看，复杂度可以拆成两层：

| 层次 | 当前可行性 | 复杂度判断 |
|---|---|---|
| 内存级临时分支 | 已有相当基础 | 中等 |
| Durable / StateJournal 级共享前序历史的兄弟分支 | 还需补基础设施 | 中高 |

### 3.1 内存级临时分支并不难

当前 `AgentState.ExportSnapshot()` / `RestoreSnapshot()` 已经提供了完整内存快照能力。  
`AgentEngine.ExportStateSnapshot()` / `CreateFromStateSnapshot(...)` 也已可工作。

因此最朴素的实现甚至不要求立刻引入 StateJournal fork：

1. 主引擎导出 snapshot
2. 用 snapshot 构造一个临时 `AgentEngine`
3. 在临时引擎里跑 wizard
4. 产出 `WizardResult`
5. 回到主引擎，仅写入结果性条目

这一层的工程量主要在：

- Wizard result contract
- 临时引擎生命周期
- host-side orchestrator
- 哪些 tool / app state 需要一起复制

### 3.2 Durable fork 不是“顺手就有”

如果进一步要求：

- 主上下文和 Wizard 上下文在内存中是两个兄弟对象
- 它们共享已落盘前序历史
- Wizard 还能 durable 暂存并恢复

那么复杂度会明显抬升。

原因是当前 `AgentEngineStateRoot.Save(...)` 的持久化方式仍是“把快照重新编码成一套新的 Durable 对象图”，而不是直接把 `RecentHistory` 维护为天然可 fork 的 durable 主结构。

尤其要注意当前 StateJournal 的公开 fork 能力边界：

- `DurableDict` 支持 `ForkCommittedAsMutable()`
- `DurableHashSet` 支持 `ForkCommittedAsMutable()`
- `DurableDeque` 当前**没有** public `ForkCommittedAsMutable()`
- `DurableOrderedDict` / `DurableText` 当前也没有 public `ForkCommittedAsMutable()`

而 `AgentEngineStateRoot` 当前恰好把 history 和 pending notifications 写进 `DurableDeque`。  
这意味着“直接依赖现成 Durable fork 能力，把整个 AgentEngine durable root 顺手 fork 出去”并不成立。

因此需要明确区分两个概念：

- **路线语义本身可行**
- **立刻靠现有 StateJournal public fork API 实现整条路线**，目前还不够顺手

## 4. 与 StateJournal fork 能力的契合度

这条路线与 StateJournal 的设计理念总体是契合的，但契合点更多在“未来方向”，而不是“今天直接调用两个 API 就结束”。

### 4.1 契合点

StateJournal 的 fork 语义很适合这类场景：

- 共享 committed 前序历史
- 派生可编辑兄弟对象
- 允许在分支工作区里继续演化
- 最终只把需要的结果再提交或回写

这与“临时 Wizard 工作区”的抽象非常接近。

### 4.2 当前不契合的点

真正的阻力在于 `Agent.Core` 现在还没有把自己的持久状态形状设计成“天然适配 fork 的 durable 对象图”。

当前现实更像：

- `AgentState` 主体仍是内存对象
- 持久层是 snapshot codec
- `Save(...)` 每次重新写出 history deque / notifications deque / pendingToolResults map / turnRuntime record

这意味着如果现在强行走 durable fork 路线，会很快碰到三个问题：

1. 需要补齐 `DurableDeque` 一类容器的 fork 能力，或者改写状态布局。
2. 需要决定 Wizard 分支的 graph root 长什么样。
3. 需要决定“最终结果拼回主上下文”是回写 delta，还是重新物化为主上下文的一条结果性历史。

### 4.3 最现实的收口

因此最合理的理解应是：

- **短期**：先用 `AgentEngineStateSnapshot` 完成“语义级 fork”
- **中期**：再让持久层演进到更像真正的 StateJournal sibling fork

也就是说，StateJournal fork 更适合作为这条路线的**优化与 durable 化方向**，而不是第一步就压上的前置条件。

## 5. 与 tool / session / runtime state 的协作

这条路线最容易被低估的，不是历史复制，而是运行态复制。

### 5.1 ToolRegistry 没问题，ToolSession 需要边界

当前 `ToolRegistry` 是定义集合，适合共享。  
但 `ToolSession` 带有执行序号与调用时状态，不应该与主上下文直接混用。

临时 Wizard 上下文更适合：

- 共享工具定义
- 拥有自己的 `ToolSession`
- 由宿主决定是否继承某些只读 registry 级元数据

否则会出现：

- Wizard 中的 tool execution sequence 污染主会话
- 调试日志与审计序号交叉
- 恢复时难以判断哪些执行属于主会话

### 5.2 IApp state 是真正的难点

如果某些 `IApp` 只有工具定义，没有会话态，那么问题不大。  
但如果 `IApp` 在宿主内维持可变运行态，fork route 就必须回答：

- Wizard 是否需要复制这份 app state
- 复制是浅拷贝、深拷贝，还是根本不复制
- Wizard 对 app state 的修改是否允许外溢

这直接决定了这条路线是否只是“history fork”，还是“完整 runtime fork”。

### 5.3 对外部世界有副作用的工具不能自动享受回滚幻觉

这是最重要的现实约束之一。

临时上下文能回滚的，首先只是：

- `RecentHistory`
- Wizard scratch state
- 某些可克隆的本地 durable 数据

它不能自动回滚：

- 已发送的网络请求
- 已写入外部文件的副作用
- 已修改主 Memory Notebook 的真实提交
- 已操作真实外部系统的结果

所以这条路线天然更适合：

- 认知型 Wizard
- 读取型 / 规划型 Wizard
- 对可 fork durable 数据做局部试算的 Wizard

而不适合在没有额外事务层的前提下，被描述成“通用可回滚执行环境”。

## 6. 持久化与恢复语义

这条路线在恢复语义上有明显优势，但前提是宿主愿意把 Wizard 当作一等状态持久化对象来管理。

### 6.1 优势

如果 Wizard 在临时上下文里运行，那么 crash / restart 后理论上可以恢复：

- 进入 Wizard 前的主上下文锚点
- Wizard 临时上下文快照
- 当前 phase
- 等待中的 tool result 或 repair 状态

这比“在主上下文里做一堆半完成步骤，退出时再回删”更自然。

### 6.2 当前缺口

但当前 `Agent.Core` 还没有 built-in 的：

- `WizardInstance`
- `WizardResultEnvelope`
- `WizardCheckpoint`
- “主上下文锚点 + 临时上下文引用”的 durable schema

因此若现在实施，恢复语义仍需由宿主层负责。

### 6.3 推荐恢复模型

比较稳的恢复模型是：

1. 主上下文照常 durable 保存。
2. Wizard 进入时生成一个独立的 wizard checkpoint。
3. 其中记录：
   - 主上下文进入点身份
   - 临时上下文 snapshot 或 durable root
   - 当前 recipe / phase / bindings
   - 最终 merge policy
4. 恢复时先恢复主引擎，再决定是继续 wizard，还是中止并丢弃临时上下文。

这个模型和当前 host-side orchestrator 的方向是相容的，只是需要额外的持久层对象。

## 7. 与上下文压缩的协作

这是这条路线的一个强项。

### 7.1 主上下文压缩协作更简单

因为 Wizard 的详细过程不进入主 `RecentHistory`，所以主上下文不需要处理：

- Wizard 中途压缩后如何再 pop
- pop 后 recap 边界如何解释
- 主账本内局部删除与 compaction 的相互作用

主上下文只需看到：

- 进入 Wizard 前的历史
- Wizard 结束后的一条结果性痕迹

因此它与主上下文 compaction 的耦合度天然更低。

### 7.2 Wizard 自己仍然可能需要压缩

如果 Wizard 很长，临时上下文自身仍可能触发 compaction。  
但这是一个更局部、更容易接受的复杂度，因为它只影响临时上下文，不影响主上下文账本语义。

### 7.3 仍需明确一条纪律

如果采用这条路线，建议明确规定：

- 主上下文在 Wizard active 期间暂停推进
- 主上下文 compaction 不与 Wizard 并发改写同一份运行态

这样可以避免“主上下文已经前进，但 Wizard 还准备把结果拼回旧锚点”的歧义。

## 8. 审计与调试

这条路线在审计和调试上是明显占优的。

它允许同时拥有两层痕迹：

- 主时间线里的结果性痕迹
- Wizard 临时上下文里的完整过程痕迹

这样可以同时满足两种需求：

- 对 LLM 主上下文保持干净
- 对研发与回放保留完整中间过程

尤其是出了问题时，forked-context route 的可诊断性很好：

- 可以单独重放 Wizard 分支
- 可以比较“原主历史”和“Wizard 合并结果”
- 可以把失败案例留作 recipe 调优样本

这比“在同一份主历史里一边写一边删”更容易调试。

## 9. 未来扩展性

如果未来真要做更强的 durable wizard runtime，这条路线的扩展空间很好。

比较自然的演进方向包括：

- durable `WizardInstance`
- wizard timeline 独立持久化
- 可选择把某些 wizard 标记为“保留完整侧向审计”
- 对可 fork 的 durable 数据结构做试算式编辑
- 未来支持“提交结果而不是提交全过程”的事务化宿主协议

从长期演化看，这条路线比“主账本内 pop 回退”更容易长成真正的“局部工作区”模型。

## 10. 主要风险与容易踩坑处

### 10.1 容易误以为只需要复制 `RecentHistory`

真正需要回答的是：

- 是否复制 `PendingToolResults`
- 是否复制 `ToolSessionExecutionSequence`
- 是否复制 turn runtime 中的 resolved profile / compaction checkpoint
- 是否复制 app-local mutable state

如果这些边界不先钉住，临时上下文很容易看起来像副本，实际上仍在共享主运行态。

### 10.2 容易误以为 Wizard 工具副作用也会自动回滚

这条路线只能天然回滚“上下文与某些可控局部状态”。  
对真实外部副作用没有自动事务保证。

### 10.3 容易过早押宝 DurableObject fork

路线本身不等于“第一天就必须用 StateJournal sibling fork 实现”。  
如果过早把 Durable fork 当作前提，当前 `DurableDeque` 等缺口会直接拖慢整体推进。

### 10.4 Merge contract 很容易设计得太弱

临时上下文退出时，不应只返回一段自由文本。  
更稳的做法是返回结构化 `WizardResultEnvelope`，至少包含：

- 主时间线可见结果
- 是否成功
- 失败时的回退策略
- 可选的审计摘要
- 可选的后续 host action

否则后面不同 wizard 很快会长出彼此不兼容的回拼逻辑。

### 10.5 容易把“侧向上下文”做成隐形第二主引擎

如果临时上下文能随意注册工具、随意修改 app state、随意直接提交 durable 数据，那么它很快会从“短流程工作区”膨胀成“另一个主会话”。

所以需要纪律：

- Wizard 是临时工作区，不是平行长期人格
- 退出后默认丢弃过程，只保留结果
- 只有明确声明保留审计时，才把侧向过程长期保存

## 11. 最适合的实施阶段与推荐落地顺序

我不建议直接从“完整 Durable fork + 完整恢复语义”开工。  
更稳的顺序是分五步：

### 阶段 1：先落语义，不先落 StateJournal sibling fork

先基于：

- `AgentEngine.ExportStateSnapshot()`
- `AgentEngine.CreateFromStateSnapshot(...)`

做出一版宿主侧临时 Wizard 引擎。

目标是先验证：

- 进入 / 退出协议
- 结果回拼 contract
- 过程不污染主上下文

### 阶段 2：定义统一的结果合并协议

补出稳定的 `WizardResultEnvelope` 形状，至少定义：

- 主时间线留下什么条目
- 是否保留审计摘要
- 失败如何回退
- 哪些 tool result 允许被改写后写回主时间线

### 阶段 3：把可 fork / 不可 fork 的 tool 与 app state 分类

不要一开始就假设所有工具都能在临时上下文中安全运行。  
应先分出：

- 纯认知型 / 只读型
- 可在局部 durable 数据上试算型
- 不可回滚的外部副作用型

### 阶段 4：为 Wizard 加独立 checkpoint

当语义稳定后，再补：

- Wizard checkpoint
- crash / restart 恢复
- 审计保留策略

### 阶段 5：最后才考虑真正的 StateJournal durable fork 优化

等前面都跑顺以后，再决定是：

- 给 `DurableDeque` 等容器补 fork 能力
- 还是重构 `AgentEngine` durable state 形状，使其更天然适配 fork

这是优化层与 durability 增强层，不应倒置成第一步前提。

## 12. 最终判断

从设计纯度看，这条路线很漂亮：

- 结果留在主时间线
- 过程留在临时工作区
- 与上下文压缩的冲突较少
- 审计与调试体验很好

但从当前 `Agent.Core` 代码现实看，它还不是最低成本的第一步施工路径。  
最合理的态度应是：

- 先把它当作**中期增强目标**
- 先用 snapshot clone 落语义
- 再视需要演进到真正的 StateJournal durable fork

**简洁结论：当前阶段，这条路线“可接受”。**
