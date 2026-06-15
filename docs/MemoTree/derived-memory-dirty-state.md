# MemoTree Derived Memory Dirty State

状态：draft v0
定位：定义 MemoTree 中派生记忆的“脏状态”语义，为 split/merge、依赖传播、Micro-Wizard 修复流程提供统一基础。

## 1. 为什么需要这份设计

MemoTree 里并不是所有字段都地位相同。

大致可以分成两类：

- 基础事实
  例如：`title`、`body`、`children`、`tags`
- 派生记忆
  例如：`gist`、`summary`

基础事实是“权威真相”的一部分。派生记忆不是权威真相，而是围绕真相形成的压缩性、可读性、可回忆性更强的辅助层。

问题在于：

- 当 `body` 变化时，旧 `summary` 可能立刻过期
- 当 `SplitNode` 发生时，左右节点的 `gist` / `summary` 很可能都不再可靠
- 当 `MergeNodeWithNextSibling` 发生时，合并后的摘要几乎必然需要重算
- 当未来建模“认知依赖”后，如果节点 `B` 出现问题，依赖 `B` 的节点 `A/C/D` 也可能进入“受影响但尚未修复”的状态

因此，“派生记忆临时不干净”不是异常，而是系统级现实。

如果系统不显式建模这一点，就会出现两个坏结果：

- 要么强迫每次变更都同步补全全部派生字段，使操作原语过重
- 要么默认继续相信旧 `gist` / `summary`，从而制造静默错误

这份设计的核心主张是：

`gist` 和 `summary` 应被视为可失效、可缺失、可传播污染的派生记忆层。

## 2. 设计目标

这套状态设计要满足：

1. 允许节点暂时不完整
2. 明确区分“缺失”和“过期”
3. 支持将来从本地变更扩展到依赖传播
4. 支持 Micro-Wizard 在后台修复
5. 不让底层原语被迫一次性背负所有修复责任

不追求：

- 一开始就做成复杂知识图一致性系统
- 一开始就引入完整的依赖图查询语言

## 3. 术语

### 3.1 Base Fields

基础字段，属于当前节点的权威事实层：

- `title`
- `body`
- `children`
- `tags`

### 3.2 Derived Memory

派生记忆，属于围绕基础字段形成的压缩表达层：

- `gist`
- `summary`

### 3.3 Dirty

广义上的“脏”，表示派生记忆当前不应被无条件信任。

Dirty 是统称，不一定说明原因。

### 3.4 Local Invalidation

本地失效。

表示节点自己的基础字段发生了变化，因此派生记忆需要重算。

### 3.5 Dependency Invalidation

依赖失效。

表示节点自身未必直接变更，但它依赖的其他节点发生了变化，因此它的派生记忆可能受影响。

## 4. 建议的状态模型

不要只保留一个 `IsSummaryStale` 布尔值。

推荐把 `gist` 与 `summary` 都建模为独立的派生状态：

```text
Missing
Fresh
Stale
Invalidated
```

推荐语义：

- `Missing`
  字段为空，且当前尚未生成

- `Fresh`
  该派生记忆与当前依赖基线一致

- `Stale`
  本节点自身基础字段变化后，旧派生记忆还在，但已过期

- `Invalidated`
  该派生记忆因结构重组、依赖传播或显式失效而不应继续信任

其中：

- `Stale` 更偏“旧值还在，但版本落后”
- `Invalidated` 更偏“旧值可能已经语义错误，不应继续作为事实压缩层使用”

## 5. 为什么 `Stale` 和 `Invalidated` 要分开

两者的恢复难度和风险不同。

例子：

### 5.1 `body` 末尾追加一段说明

旧 `summary` 仍然大概率有参考价值，但不完整。

这更像：

- `summary = Stale`

### 5.2 `SplitNode`

原节点拆成左右两个 sibling。

旧 `summary` 即使文本还存在，也已经不再对应任何一个新节点。

这更像：

- 左节点 `summary = Invalidated`
- 右节点 `summary = Missing` 或 `Invalidated`

### 5.3 依赖节点被判定有误

例如节点 `A` 的推理建立在 `B` 之上，而 `B` 被修正。

这时 `A` 的 `summary` 可能并非一定错误，但已经不能直接信任。

这也更像：

- `summary = Invalidated`

## 6. 建议的最小字段形状

在 v0/v1 阶段，不一定要一次性做成非常复杂的对象图。

可以从下面这种最小形状开始：

```text
gist_text: string?
gist_state: Missing | Fresh | Stale | Invalidated
summary_text: string?
summary_state: Missing | Fresh | Stale | Invalidated
body_version: long
gist_body_version: long?
summary_body_version: long?
```

如果后续要追踪原因，再增加：

```text
gist_dirty_reason: BodyChanged | Split | Merge | DependencyInvalidated | Manual
summary_dirty_reason: ...
```

再后续如果要追踪来源，可继续增加：

```text
invalidated_by_node_ids: [...]
```

但这些都不应该是第一步。

## 7. 何时进入 Dirty State

### 7.1 本地正文编辑

例如：

- `RewriteBodyText`
- `AppendBodyBlock`
- `SetBodyBlockContent`
- `DeleteBodyBlock`
- `SplitBodyBlockByText`
- `MergeBodyBlockWithNext`

建议：

- `gist` 至少变成 `Stale`
- `summary` 至少变成 `Stale`

### 7.2 结构重组

例如：

- `SplitNode`
- `MergeNodeWithNextSibling`

建议：

- 左右节点的 `gist` / `summary` 默认进入 `Invalidated` 或 `Missing`
- 除非调用方显式提交新的派生记忆

### 7.3 子节点集合变化

这里要区分一件事：

当前 MemoTree 定义中，`summary` 只概括本节点正文，不包含子节点内容。

因此：

- 子节点变化不必自动让本节点 `summary` 过期
- 但可能影响用于 index / node card 呈现的某些附加说明

所以不要把“所有结构变化”都粗暴等价成 `summary stale`。

### 7.4 依赖传播

未来若引入认知依赖：

- `depends_on(A, B)`

当 `B` 被修正、失效或重组时：

- `A` 的派生记忆可被标记为 `Invalidated`

这是未来非常重要的扩展方向，但不必在 v0 先做完。

## 8. Split/Merge 与 Dirty State 的关系

### 8.1 SplitNode

推荐默认行为：

- 左节点保留原 `nodeId`
- 右节点创建新 `nodeId`
- 若未显式提供新的 `RightGist` / `RightSummary`
  则右节点相关派生记忆进入 `Missing`
- 左节点已有派生记忆默认进入 `Invalidated`
  因为它原本概括的是“拆分前的整体节点”

这是非常重要的设计点：

`SplitNode` 不应强制要求一次性填写完整的 `gist` / `summary`。

否则：

- 节点结构原语会变得臃肿
- LLM 必须在一次操作里同时完成结构重组和高质量派生记忆重算
- 工具调用会变长，失败面会变大

### 8.2 MergeNodeWithNextSibling

推荐默认行为：

- 左节点保留原 `nodeId`
- 右节点被删除
- 合并后的 `gist` / `summary` 默认进入 `Invalidated`
- 若调用方显式提供新的合并后派生记忆，则可直接标为 `Fresh`

## 9. 为什么必须接受“脏节点”

一旦系统要支持：

- 分步结构整理
- 长节点拆分
- 多轮认知修复
- 依赖传播
- 后台维护

就必须接受“节点在一段时间内不完整”。

这并不意味着系统混乱。

恰恰相反：

只有当“脏”被显式建模时，系统才真正可控。

否则系统只是在假装一切始终完整。

## 10. 与 Micro-Wizard 的关系

Dirty State 的重要意义之一，是它天然为 Micro-Wizard 提供了触发条件。

典型流程：

1. 主会话调用 `SplitNode`
2. 左右节点派生记忆进入 `Missing` / `Invalidated`
3. 运行时发现有满足条件的 dirty node
4. 触发某个预定义 Micro-Wizard
5. Wizard 读取相关节点正文与上下文
6. Wizard 生成新的 `Gist` / `Summary`
7. 结果固化后退出，主会话不需要长期背负中间过程

也就是说：

- Dirty State 是“问题表面”
- Micro-Wizard 是“问题修复机制”

两者是天然耦合的一对系统。

## 11. 面向 Agent 的使用建议

建议不要要求 Agent 每次都手动维护 dirty state。

更合理的策略是：

- 原语自动设置 dirty state
- 运行时按策略触发修复流程
- Agent 只在必要时显式补写

这更接近人类认知：

- 先发现“这部分不靠谱了”
- 再在合适时机回头整理

而不是要求每次变化都即时完成所有整理。

## 12. 建议的实现顺序

### 阶段 1

实现最小状态：

- `gist_state`
- `summary_state`
- `Missing/Fresh/Stale/Invalidated`

### 阶段 2

让本地编辑和 split/merge 原语自动修改状态。

### 阶段 3

实现一个最小的 dirty node 扫描与排序逻辑。

### 阶段 4

接入 Micro-Wizard，在后台修复派生记忆。

### 阶段 5

再考虑依赖关系与级联失效。

## 13. 一句话结论

MemoTree 不应强迫每次结构变更都立即补全 `gist` / `summary`。

更稳的路线是：

- 接受 dirty derived memory
- 显式建模 dirty state
- 用 Micro-Wizard 把“从脏到干净”的过程做成受控后台修复流程
