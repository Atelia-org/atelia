# Agent-OS Core Review Summary

> 汇总对象：
> - [agent-os-core-review-architecture.md](./agent-os-core-review-architecture.md)
> - [agent-os-core-review-concurrency.md](./agent-os-core-review-concurrency.md)
> - [agent-os-core-review-dotnet-async.md](./agent-os-core-review-dotnet-async.md)
> - [agent-os-core-review-persistence.md](./agent-os-core-review-persistence.md)
> - [agent-os-core-review-state-machine.md](./agent-os-core-review-state-machine.md)

## 1. 总体结论

当前 draft 的大方向是成立的，而且已经抓住了几条真正该守住的主张：

- `LlmSession` 是状态机，不是能力容器。
- completion 与 tool 应作为两类不同资源域分开调度。
- 外部回调不直接改 session，而是先转成 `WakeEvent` 再由统一入口应用。
- `StateJournal` 是 checkpoint / recovery store，不是 runtime lease 镜像。
- MVP 应建立在 `.NET async/await + ThreadPool` 之上，而不是自造线程调度器。

真正需要尽快收口的，不是“是否继续借鉴 OS”，而是：

- 哪些状态是 durable working state
- 哪些只是 runtime overlay
- 哪些字段表达 phase，哪些字段表达 scheduling disposition
- 哪些事件必须带强关联标识，才能避免 stale result 污染新状态

如果这几件事不先写死，后续实现最容易踩的坑不是性能，而是语义错误。

## 2. 五份评审的强共识

### 2.1 先分清 durable phase 与 runtime overlay

这是最强共识。当前 draft 里：

- `RunningCompletion`
- `RunningTool`
- `PersistPending`
- `WaitReason`
- `ReadyQueueKey`
- `InFlightLeaseId`

这些事实还没有彻底分层，导致“工作阶段”和“调度处境”混在一起。

更稳的方向是：

- durable phase 只表达 session 的稳定工作阶段
- runtime overlay 承载 ready membership、active lease、timer、checkpoint-in-progress 等瞬时运行态

### 2.2 MVP 应明确：每个 session 同时最多一个 active step

这在并发、状态机、恢复、.NET async 四个视角里都被反复强调。

原因不是保守，而是当前 draft 的形状本来就更适合单 step in flight：

- 一个 `WaitReason`
- 一个 `ReadyQueueKey`
- 一个 `InFlightLeaseId`

如果 MVP 早早放开“一个 session 多个 in-flight tool step”，复杂度会立刻跳升。

### 2.3 `WakeEvent` 必须带强关联标识

只带 `SessionId` 不够。至少需要能够判定：

- 它属于哪个 `StepId`
- 属于第几次 `Attempt`
- 属于哪一次具体 dispatch / lease

否则 timeout、cancel、retry、restart recovery 后的晚到结果都可能误落到新状态上。

### 2.4 kernel 仍需保留唯一 arbitration / commit point

completion/tool 分开调度并不意味着没有统一裁决点。

共识是：

- completion/tool 子系统各管自己的 capacity / queue policy
- 但 session 状态提交、lease retirement、late result 判废、timeout/cancel 折叠，仍应由 kernel 统一完成

也就是说，MVP 应是“不同子系统 + 单一状态提交点”，而不是“一个大一统内核”或“完全去中心化状态改写”。

### 2.5 恢复必须坚持“诚实原则”

恢复相关评审最强烈的一点是：

- 不能证明安全重试的外部操作，恢复后就不能自动重放
- `Running* -> Attempt++` 不能作为默认通用恢复策略
- side-effecting tool 更不能默认重试

建议把“未知是否已发生外部效果”明确表达出来，而不是隐含进普通 `Runnable` 或 `Faulted`。

### 2.6 .NET async 路线是可行的，但要落实成 single-reader mailbox loop

对 MVP，大家并不建议固定线程亲和，而建议：

- 一个 single-reader kernel mailbox
- 多个 producer 从 callback / timer / executor 往里面投事件
- kernel 串行消费事件并同步改状态

安全性来自 single-reader discipline，不来自固定线程。

## 3. 当前 draft 最需要先改的 5 个点

### 3.1 重写状态模型：从“单一大状态枚举”转向“双轴模型”

建议优先把：

- durable phase
- runtime disposition / overlay

明确拆开。最少也要在文档中写清：

- `RunningCompletion` / `RunningTool` 更偏 runtime execution view
- `PersistPending` 更偏 persistence mechanism view
- 它们不应再和 durable business phase 混在同一层表达

### 3.2 明确 `single active step per session` 是 MVP 前提

不要再把它留在待决问题里。建议直接升格成第一版硬约束。

### 3.3 为 step / dispatch / wake event 补一套身份模型

建议至少区分三层：

- `StepId`：session 逻辑上的第几步
- `Attempt`：该步第几次尝试
- `DispatchId` / `LeaseId` / `DispatchToken`：某一次具体外发实例

completion/tool 的完成、取消、超时、persist 完成等事件，都应该和这层身份模型对齐。

### 3.4 明确 recovery / persistence 的操作边界

建议新增 durable `PendingOperation` 或等价结构，表达：

- 这一步是什么操作
- 是否可能已对外发出
- 结果是否已收到
- 结果是否已 durable 吸收
- side effect 是否未知 / 幂等 / 可补偿 / 不可补偿

没有这一层，恢复策略会长期漂浮在注释里。

### 3.5 把 .NET 运行时形状改写为 `RunAsync + mailbox` 叙事

当前 `TickAsync` / `TryDispatch` 这组接口太像“局部轮询 + 布尔 admission”，容易让实现退化成 re-entrant polling。

建议文档改成更像：

- `RunAsync` 或 `ProcessLoopAsync`
- mailbox / channel ingress
- 显式 dispatch receipt / outcome envelope

这样更贴近真正的 MVP 实现形态。

## 4. 值得直接吸收到主文档的收口建议

### 4.1 先讲 invariants，再讲 analogy

架构视角一致建议把 OS analogy 降级成解释辅助，而不是主叙事。

更稳的顺序是：

1. system invariants
2. durable / runtime / mechanism / policy 四分法
3. completion 与 tool 两个子系统
4. 最后再讲 OS 借鉴

### 4.2 completion/tool 需要最小公共 outcome envelope

不必强求同一个 request 类型，但 kernel 边界应至少能统一理解：

- succeeded
- failed
- cancelled
- timed out
- rejected
- lost-after-recovery

否则错误和恢复处理会在两个子系统里各写各的。

### 4.3 fairness 先给最低可执行版本

可以先定：

- queue 内 FIFO
- queue 间 round-robin
- requeue 到队尾
- 每 tick 对单 session 的 event 处理设上限

不追求全局最优，但至少让“不明显饿死”变成可验证规则。

### 4.4 token/text delta 不应直接淹没 kernel 主邮箱

流式 UI 事件和 durable state 事件应区别对待。MVP 中主邮箱优先承载：

- completion finished
- tool finished
- timeout elapsed
- cancel requested / acknowledged
- persist finished

而不是高频文本增量。

## 5. 目前存在但不妨后放的分歧或开放项

这些问题重要，但都不应先于核心边界定稿：

- completion queue key 最终是否只用 `model-id`
- tool mutex group 由静态 metadata 还是 host policy 注入
- checkpoint 是事件驱动、时间驱动还是混合
- 是否需要单独 persistence worker
- durable session state 是否直接用 `StateJournal` 对象图表达，还是先经 DTO
- 是否需要更细粒度 lane capacity / fairness / provider budget

## 6. 推荐的下一轮修文顺序

### 第一步

先重写主文档的“状态模型”和“边界模型”：

- durable phase
- runtime overlay
- identity model
- kernel arbitration point

### 第二步

再重写“恢复与持久化”：

- `PendingOperation`
- recovery disposition
- commit boundary
- tool side effect 分类

### 第三步

最后把“.NET async MVP 实现形状”和“OS analogy”收尾：

- `RunAsync` mailbox loop
- async executor / callback ingress
- 哪些 OS 类比保留，哪些降温

## 7. 主线程推荐判断

如果只问“这条路线值不值得继续往下做”，我的结论是：**值得，而且已经相当接近一个稳的 MVP 架构了。**

但如果只问“当前 draft 是否已经可以直接作为实现蓝图”，我的结论是：**还差一轮关键收口。**

那一轮收口的核心，不是再发明更多概念，而是把现有概念放回各自该在的层次里。
