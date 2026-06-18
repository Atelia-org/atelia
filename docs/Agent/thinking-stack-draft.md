# Thinking-Stack Draft

状态：draft v0  
定位：记录一种尚处于早期想法阶段的能力设想，即让 LLM Agent 能主动管理自己的局部分支探索过程，把 Context 组织成可递归 push/pop 的 thinking stack / thinking tree。

相关文档：
- `docs/Agent/micro-wizard-runtime-draft.md`
- `docs/Agent/micro-wizard-history-route-comparison.md`
- `docs/Agent/agent-core-capability-system-draft.md`
- `prototypes/MutableContextAgentProto/Phase2Commands.cs`

## 0. 一句话结论

Thinking-Stack 与 Micro-Wizard 在底层语义上是高度相容的。  
它们都需要一种能力：

- 进入局部探索前保存当前上下文边界
- 在局部探索中追加若干中间步骤
- 退出时忘掉这些详细步骤
- 只把结果性结论带回父上下文

最大的区别不在“历史怎么保存”，而在：

- **Micro-Wizard 更像宿主驱动的受控短流程**
- **Thinking-Stack 更像模型自己驱动的递归分支探索机制**

---

## 1. 直觉模型

可以把 Thinking-Stack 想成：

- 模型在解决一个复杂问题时
- 主动把某个局部假设、子问题、分类讨论分支压入一个栈帧
- 在这个局部上下文里进行若干步探索
- 得出结论后把这段细节折叠掉
- 仅把“结论性结果”返回到父帧

它像程序里的：

- call stack
- recursion stack
- depth-first search 的显式分支栈

也像你说的：

- 解组合问题时做分类讨论
- 探索推箱子、走迷宫、汉诺塔一类递归结构问题
- 在回到父问题时，只保留“这一支为什么可行 / 不可行”的总结

---

## 2. 它解决的是什么问题

当前普通 LLM 上下文有一个常见困难：

- 一旦认真展开局部讨论
- 所有中间推理都会持续堆在活跃上下文里
- 很快就会挤占后续推理空间
- 模型也更难保持“我当前到底在第几层分支、回来了没有”的整体结构感

Thinking-Stack 希望解决的就是：

- **保留总体认识**
- **遗忘具体枝叶**
- **让当前活跃的局部分支始终保持前台**

换句话说，它追求的是：

- not full history retention
- but structured branch-return cognition

---

## 3. 与 Micro-Wizard 的关系

### 3.1 共同点

两者共享同一个底层语义模板：

1. 进入局部过程前保存一个边界
2. 在局部过程里发生多步 action / tool / thought
3. 退出时不把全过程长期留在父上下文
4. 只保留结果性产物

因此它们明显可以共享一批基础原语：

- savepoint / anchor
- tail rewrite / result retain
- audit trace
- compaction coordination
- active frame metadata

### 3.2 不同点

它们的主要差异在控制面：

#### Micro-Wizard

- 通常由宿主或 trigger 启动
- 有 recipe / phase / tool gating
- 更像外部编排的局部工作流
- v0 很适合先限制为单 active wizard

#### Thinking-Stack

- 更像模型主动调用的一组认知工具
- 不一定有固定 recipe
- 常常表现为递归 push / pop
- 十之八九需要支持多层嵌套

所以更准确的说法不是：

- Thinking-Stack 是 Micro-Wizard 的一个特例

而是：

- **Micro-Wizard 与 Thinking-Stack 是两类共享底层上下文折叠原语的上层能力**

## 3.5 与当前 capability 边界的关系

Thinking-Stack 不应被理解成一个“任意模型都可挂上的小技巧”。  
按当前项目已收紧的边界，它应直接建立在与 Micro-Wizard 相同的前提上：

- `Agent.Core` 当前只接受 `SupportsAgentCoreFullFeatures == true` 的 profile
- 因而 Thinking-Stack 可以直接依赖明文 thinking、可续写 actor-side continuation、以及非防篡改上下文语义

这意味着现阶段更合理的口径是：

- **Thinking-Stack 是 full-feature-only `Agent.Core` 的上层能力**

而不是：

- **先把它做成广泛兼容、再处处写降级分支**

---

## 4. 建议的共享底层抽象

如果我们只为 Micro-Wizard 命名基础对象，很容易把设计做窄。  
更好的做法是抽出一个更通用的层，比如：

### 4.1 `ContextFrame`

```text
ContextFrame
  FrameId
  Kind: Wizard | ThinkingBranch | Other
  ParentFrameId?
  Savepoint
  Status
  CreatedAt
  AuditPolicy
```

### 4.2 `ContextSavepoint`

```text
ContextSavepoint
  AnchorEntrySerial
  AnchorHistoryCount
  EntryState
  TurnLock
  RuntimeCheckpoint
  CompactionSuppressed
```

### 4.3 `RetainedResultEnvelope`

```text
RetainedResultEnvelope
  Summary
  ResultEntries[]
  Outcome
  Notes?
```

这样就可以把：

- Micro-Wizard 看成一种 `Wizard` frame
- Thinking-Stack 看成一种 `ThinkingBranch` frame

两者共享：

- enter frame
- explore
- retain result
- pop frame

---

## 5. Thinking-Stack 的更强要求

Thinking-Stack 比 Micro-Wizard 更强的地方在于：

### 5.1 需要递归嵌套

它不是“偶尔进一次局部流程”，而是很可能：

- branch A 里面再分 A1 / A2
- A1 里又再分 A1a / A1b
- 每层都要能 push / pop

这要求底层不要只支持：

- 一个 active wizard

而是至少在未来支持：

- 一条 active frame stack

### 5.2 需要模型自己操作

Micro-Wizard 常常是 host-orchestrated。  
Thinking-Stack 很可能更像一个 `IApp`：

- 展示当前 frame stack / branch tree 状态
- 提供 `push_branch` / `return_branch` / `fail_branch` / `commit_branch` 一类工具

因此它要求的不是“宿主帮它管理一切”，而是：

- 模型能在推理中显式调用这些结构化原语

### 5.3 需要保留分支树而不只是线性摘要

对于复杂问题，父上下文可能不只需要知道：

- “刚才有一条分支失败了”

还需要知道：

- 哪个分支失败
- 失败原因是什么
- 哪些兄弟分支还没试
- 某个分支的中间结论能否复用

因此 Thinking-Stack 很可能最终需要一份：

- branch tree metadata

而不仅是单条 retained result。

---

## 6. 哪条历史管理路线更适合同时承载两类需求

### 6.1 只看当前首步实现

如果只看“下一项基础增强先做什么”，我更推荐：

- **same-context pop route**

原因是它更容易先抽出一套共享原语：

- `ContextFrame`
- savepoint
- tail rewrite
- retained result
- compaction suppression

而这四样东西同时对：

- Micro-Wizard
- Thinking-Stack

都有直接价值。

尤其是 Thinking-Stack 的“像函数栈那样 push/pop”，从直觉上就更像：

- 在同一运行态里压栈
- 再在同一运行态里弹栈

### 6.2 只看长期目标设计

如果看更长期的最终设计，我认为：

- **forked-context route 更像真正的 thinking tree workspace**

因为一旦 Thinking-Stack 发展到下面这些程度：

- 可暂停的兄弟分支
- 深层递归
- 分支间复访
- 每个分支都有独立较长的探索过程

那它会越来越像：

- 一棵局部工作区树

而不是：

- 一份主账本上的尾部折叠技巧

### 6.3 综合判断

因此我的判断与 Micro-Wizard 的路线选择其实一致：

- **当前共享首步原语**：更适合同账本 savepoint/pop
- **长期完整形态**：更偏向 forked workspace / sibling branch

---

## 7. 为什么 same-context pop 更适合做共享首步原语

主要有四个原因。

### 7.1 它与当前 `Agent.Core` 地基更一致

当前 `Agent.Core` 已经明确：

- `RecentHistory` 是活跃上下文事实源
- v0 Micro-Wizard 先不做独立时间线

所以先把“保存边界、退出折叠结果”做在同一份账本里，切口更小。

### 7.2 它更容易先验证 `retained result` 契约

不管是 wizard 还是 thinking branch，真正难的都不是“怎么开始”，而是：

- 退出时到底保留什么

same-context pop route 可以先逼我们把这个契约做实。

### 7.3 它更自然地支持“栈”

Thinking-Stack 的早期需求是：

- push
- recurse
- pop

在实现上，savepoint 栈就是最自然的第一版载体。

### 7.4 它暂时不要求完整隔离 tool/app state

forked-context route 一旦认真做，就很快要回答：

- `ToolSession`
- `IApp` mutable state
- durable branch workspace

这些问题对第一步共享原语来说太重了。

---

## 8. same-context pop 的关键约束

如果把它当成 Micro-Wizard 和 Thinking-Stack 的共享第一步，就必须诚实接受几条纪律：

1. v0 先不追求任意并发 frame，只允许一条 active frame stack。
2. frame active 期间禁止触发 context compression。
3. 外部副作用尽量推迟到最终 commit / return phase。
4. 保存/恢复范围不能只看 `RecentHistory`，还要覆盖运行态边界。
5. 需要正式的 tail rewrite primitive，而不是宿主随手删尾巴。

对 Micro-Wizard 来说，这些纪律是可接受的。  
对 Thinking-Stack 来说，它们也够用来支撑第一版递归 push/pop。

---

## 9. 一个更具体的演进建议

### 阶段 1：先抽通用原语

先不要只为 Wizard 命名。  
建议直接抽：

- `ContextSavepoint`
- `ContextFrame`
- `RetainedResultEnvelope`
- tail rewrite primitive

### 阶段 2：先让 Micro-Wizard 用起来

因为它的行为边界更窄、场景更容易验证。

### 阶段 3：再把 Thinking-Stack 作为第二个上层能力接上

此时新增的主要是：

- frame stack/tree 元数据
- `IApp` 展示层
- push/pop/return 一类工具

### 阶段 4：最后再评估是否升级到 forked workspace

当出现这些信号时，可以考虑升级：

- Thinking-Stack 分支深度明显增加
- 需要暂停并恢复多个兄弟分支
- 同账本禁用 compaction 的代价变大
- 需要更强的 durable branch recovery

---

## 10. 最终判断

Thinking-Stack 与 Micro-Wizard 是高度相容的，而且非常值得尽早把它记录进项目设计里。  
它们不该各自发明一套历史管理机制，而应共享一组更通用的上下文折叠原语。

如果问“哪条技术路径更适合同时承载两类需求”，我的答案是：

> **当前首步实现，优先 same-context pop route；长期目标设计，保留向 forked-context / branch-workspace 演进的方向。**

这样既顺着当前 `Agent.Core` 的地基，也不会把未来的 thinking tree 设计空间过早锁死。
