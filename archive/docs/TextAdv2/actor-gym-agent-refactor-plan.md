# TextAdv2 Actor-Gym-Agent Refactor Plan

> 状态：draft，作为本轮重构的外部锚点
> 适用范围：`prototypes/TextAdv2/`、`prototypes/TextAdv2.DefaultAgent/`、`tests/TextAdv2.Tests/`

本文记录当前 `TextAdv2` 的 Actor-Gym-Agent 收口计划，目的不是大改架构，而是在现有方向基本正确的前提下，把几个已经开始影响演化速度的边界问题收干净。

## 为什么现在做

这一轮刚新增了：

- durable `ActorEmbodiedState`
- world-side tick executor
- `currentActivity` / `carriedResources` observation
- `AgentTurnHost` 与 `DefaultAgent` 初版骨架

整体方向是对的，但也因此更容易看清几个“先不收口，后面会越长越歪”的接缝：

- Gym resolver 与 host 同时在解释 action
- `IsInterruptible` 已进入状态与 observation，但还不是 authoritative rule
- route / inventory 的 durable invariant 在 load 时不够强
- 文档与测试对新边界的承诺还不够精确

项目仍处于早期草稿阶段，没有兼容包袱，所以这里优先做及时重构，而不是再叠一层兼容表面。

## 本轮目标

本轮只做三个小收口包：

1. `Gym contract` 收口包
2. `WorldTruth invariants` 收口包
3. `docs/tests boundary` 收口包

它们的共同目标是：

- 让 agent-facing contract 更单一
- 让 world truth 的 durable 约束更早失败
- 让文档、命名、测试与真实边界重新对齐

## 包 1：Gym contract 收口

### 背景

当前 `ResolvedAgentAction` 名义上像“已裁决、可执行的 plan”，但 `AgentTurnHost` 仍然会再次对原始 `AgentActionIntent` 做 `switch` 并决定实际 runtime mutation。

这会带来两个问题：

- resolver 与 host 对 action surface 的理解重复
- `AgentTurnResolutionStatus` 同时承载“是否被 resolver 接受”和“执行时是否失败”的两种语义

### 目标

引入真正的 executable action / resolved operation，使 resolver 产出的东西已经足够接近“host 只需执行”的描述，host 不再直接 `switch` 原始 intent。

### 预期落点

- `prototypes/TextAdv2/Gym/AgentTurnContracts.cs`
- `prototypes/TextAdv2/Gym/DefaultSequentialAgentTurnConflictResolver.cs`
- `prototypes/TextAdv2/Gym/AgentTurnHost.cs`
- `tests/TextAdv2.Tests/AgentTurnHostTests.cs`

### 验收标准

- host 不再对 `AgentActionIntent` 做二次解释
- resolver 产出的是 executable operation，而不是“只是把原 intent 包一层”
- unsupported intent 的拒绝，发生在 resolver 阶段
- 执行失败与 resolver 拒绝的消息边界更清楚
- 现有 host 行为测试仍可表达 move / start-follow-route / start-mining / cancel / keep

### 本包明确不做

- 不在这一步重新设计更高层的 agent intent taxonomy
- 不在这一步做 richer simultaneous conflict semantics
- 已将 `StartMiningAgentActionIntent` 收口为高层 intent：只保留 `worksiteId`，具体 mining config 下沉到 world-side worksite profile

## 包 2：WorldTruth invariants 收口

### 背景

`ActorEmbodiedState` 已经进入 durable world truth，但几条关键规则还没有完全 authoritative：

- `IsInterruptible` 目前只是状态字段与 observation 字段
- route 的合法性在 start 时校验比 load 时更强
- inventory 负值等问题仍可能延迟到 observation 才暴露

### 目标

让 embodied process 和 inventory 的 durable invariant 更像真正的 world truth 规则：

- 非可打断 process 不能被任意打断或覆盖
- route 的 durable 形状在 load/open 时就被完整校验
- carried resource ledger 在 actor load 时 eager 验证

### 预期落点

- `prototypes/TextAdv2/WorldTruth/WorldState.cs`
- `prototypes/TextAdv2/WorldTruth/Actor.cs`
- `prototypes/TextAdv2/WorldTruth/WorldTurnExecutor.cs`
- 视需要新增一个小的共享规则 helper
- `tests/TextAdv2.Tests/EmbodiedProcessTests.cs`
- `tests/TextAdv2.Tests/SerialWorldRuntimeTests.cs`

### 验收标准

- `MoveActor(...)`、`StartActorRouteFollowing(...)`、`StartActorMining(...)`、`CancelActorEmbodiedState(...)` 对非 interruptible 状态有一致规则
- route load-time validation 与 start-time validation 不再明显失配
- world load/open 时，坏 inventory 会立即失败
- executor 不再承担“悄悄帮坏 durable state 擦屁股”的主要职责

### 本包明确不做

- 不在这一步引入完整物品系统或背包容器模型
- 不在这一步实现复杂 process scheduler
- 不在这一步把 occupancy / read-model 索引全面重做

## 包 3：docs/tests boundary 收口

### 背景

代码已经向前走了一大步，但文档与测试还保留了若干“旧说法”或“半对齐”状态：

- 文档里仍有一些“下一步再做”的表述，实际上代码已经落地
- cross-host parity 对非 idle activity 的验证还偏浅
- `DefaultAgentAssemblyTests` 这个名字会让人误以为它在测试 assembly boundary，本质上更像 cross-layer behavior smoke tests

### 目标

把对外承诺和内部测试边界重新对齐，让后续继续迭代时不再被这些轻微错位绊住。

### 预期落点

- `docs/TextAdv2/agent-turn-gym-host.md`
- `docs/TextAdv2/worldtruth-embodied-processes.md`
- `docs/TextAdv2/default-agent-assembly.md`
- `tests/TextAdv2.Tests/CrossHostMachineContractParityTests.cs`
- `tests/TextAdv2.Tests/E2eCliBlackBoxTests.cs`
- `tests/TextAdv2.Tests/GameServerIntegrationTests.cs`
- `tests/TextAdv2.Tests/DefaultAgentAssemblyTests.cs` 及其文件名/类名

### 验收标准

- 文档对当前已落地能力的描述与代码一致
- 至少有一个非 idle activity 的 cross-host parity 用例
- DefaultAgent 相关测试的名字与位置更准确表达它实际覆盖的边界

### 本包明确不做

- 不在这一步把全部文档做成最终版
- 不在这一步引入完整 black-box matrix

## 实施顺序

顺序固定：

1. 先写本文档并作为外部锚点
2. 先做 `Gym contract`
3. 再做 `WorldTruth invariants`
4. 最后做 `docs/tests boundary`

原因：

- `Gym contract` 是当前最强的跨层重复解释点
- `WorldTruth invariants` 需要在更清晰的 host/runtime contract 之上收规则
- 文档与测试边界应最后同步，避免前两包尚未稳定时反复修改

## 测试策略

每包至少做对应的近身测试：

- 包 1 重点看 `AgentTurnHostTests`
- 包 2 重点看 `EmbodiedProcessTests` 与 world/runtime 相关测试
- 包 3 重点看 cross-host parity、host integration 与 DefaultAgent 相关测试

在本轮结束时，至少应跑：

- `tests/TextAdv2.Tests/TextAdv2.Tests.csproj`

如果某一步出现 staged/working-tree 边界不清的情况，优先保证代码与测试结果正确，再决定是否需要重新整理 staged 状态。

## 这轮暂不推进的方向

下面这些方向是合理的，但不放进本轮：

- actor presence 的 occupancy/read-model 索引进一步强化
- 批量 `step` / 批量 `observe` 的 Gym machine surface
- 更高层的 mining/work intent 抽象
- richer DefaultAgent cognition 与 LLM loop
- 真正的插件发现与外部程序集加载

这些都更适合建立在本轮收口后的 cleaner seam 之上继续演化。
