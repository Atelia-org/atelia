# TextAdv2 WorldTruth Embodied Processes

> 状态：draft，最小实现已接入运行时主链
> 适用范围：`prototypes/TextAdv2/WorldTruth`

> 2026-06 更新：`ActorEmbodiedState` 已最小接入 durable `Actor`，并已由 world-side tick executor 驱动
> `route-following` / `mining` 会随 `AdvanceLogicalTime` 自动推进。

本文收口一个关键边界：

- Actor 的 goal、memory、budget、prompt context 不属于 `WorldTruth`
- Actor 当前“身体正在执行什么”这类会持续影响世界演化的状态，应进入 `WorldTruth`

## 核心判断

一个 actor 的状态，如果同时满足下面几条，就应被视为 world truth：

- 不靠 agent 再次思考，也会继续推进
- 会被世界规则消费
- 会影响或暴露给其他 actor
- host 重启后仍应成立

满足这些条件的，不是“主观念头”，而是“具身执行态”。

## 为什么这在 TextAdv2 Gym 中合理

TextAdv2 当前已经明确：

- `AgentTurn` 是基本时间粒度
- 每个清醒 agent 每 turn 都会被激活一次推理
- `keep` 是显式动作

这意味着 agent 不必每回合都发出新的细粒度动作，世界仍然可以继续演化。

因此，当 actor 进入一个持续过程后，`keep` 的合理含义不只是“无事发生”，而是：

- 允许当前 embodied process 继续运行

这正适合下面这些例子：

- 开始一次导航式移动后，之后多个回合里身体自动沿路径前进
- 进入挖矿状态后，每若干回合推进一次工作进度并产出资源

## 不应放进 WorldTruth 的东西

下面这些不应进 `WorldTruth`：

- 我想做什么
- 我为什么这么做
- 我记得什么
- 我的提示词上下文怎么拼
- 我本轮还剩多少思考预算

这些都属于 agent cognition 或 host-side context building。

## 应放进 WorldTruth 的东西

当前应进入 `WorldTruth` 的，是 actor 的最小具身执行状态。

例如：

- `idle`
- `route-following`
- `mining`

它们不需要一开始就做成万能统一系统，但应至少共享一个边界判断：

- 这些是持续过程
- 它们会随着世界 tick 推进
- 它们应可被观察、可被中断、可跨重开恢复

## 与当前 world primitive 的关系

当前 `MoveActor(passageId)` 仍然是有价值的即时 primitive。

但在 Gym 语义里，更自然的 agent-facing action 将逐步演化为：

- `keep`
- `cancel-current-process`
- `start-follow-route(...)`
- `start-mining(worksiteId)`

也就是说：

- agent 负责选择、切换、取消 process
- `WorldTruth` 负责持有 process state
- 每回合 process 如何推进，由世界规则决定

## 为什么不现在就做巨型 ActorActivity 基类

因为不同 process 的推进语义差异很大：

- route-following 主要是位置和路径推进
- mining 主要是周期性进度与资源产出；启动时只引用 worksite，具体产出规则来自该地点的 durable mining worksite profile
- crafting / combat / dialogue 后续还会更不同

因此当前最好的做法不是抢先做一个“能包一切”的复杂框架，而是：

- 先钉住“具身执行态属于 `WorldTruth`”这条边界
- 再用最小 draft 类型把 route-following / mining 两类 process 的 durable 状态需求显式化

## 当前实现落点

当前最小实现已经包含：

- `Actor` durable embodied state
- world-side tick executor
- `route-following` 的自动逐 passage 推进
- `mining` 的周期性资源产出
- actor-facing observation 中的 `currentActivity` / `carriedResources`

目前仍然刻意保持克制：

- 只接了最小 carried-resource ledger，还没有完整物品/背包系统
- cognition / memory / budget / context builder 仍然在 world 外
- 更复杂的 embodied process 还没有统一抽象成通用 activity framework

## 当前边界补强

在最小实现接通之后，当前又额外收了三条 world truth 规则：

- `IsInterruptible` 已从“可见字段”收成 authoritative rule
- route-following 的 durable path 会在 world load 时做整条路径校验
- carried-resource ledger 会在 actor load 时 eager 校验，而不是等到 observation 才暴露

这使得 embodied process 更接近真正的 durable invariant，而不是“运行起来大概没问题”的草稿状态。

## 下一步建议

后续最自然的实现顺序是：

1. 把 carried-resource ledger 演进为完整物品/背包建模
2. 继续把 embodied process 的 world-side rule 抽成更清晰的共享规则模块
3. 视需要抽象更多 embodied process（crafting / combat / dialogue）

总结起来，这条设计的关键不在于“Actor 有没有状态”，而在于：

- 只有会驱动世界持续演化的状态，才进入 `WorldTruth`
- 认知、记忆、预算、上下文构建，仍然放在 agent / host 侧
