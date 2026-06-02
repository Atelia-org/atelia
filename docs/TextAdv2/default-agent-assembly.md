# TextAdv2 DefaultAgent Assembly

> 状态：draft，已配套落第一版独立 assembly
> 适用范围：`prototypes/TextAdv2.DefaultAgent/`

本文收口 `DefaultAgent` 的定位：

- `Gym` 层只提供稳定的 turn contract 与 host orchestration
- `DefaultAgent` 层承载默认 agent 的 context building、goal/memory/budget 接口与参考 policy

## 为什么应尽快独立成 assembly

因为这层变化速度会明显快于 `TextAdv2` 世界内核。

`DefaultAgent` 后续很可能持续演化：

- LLM 调用接法
- memory retrieval
- context builder
- goal shaping
- budget policy
- tool use

如果它继续和 `TextAdv2` world/gym 核心混在同一个 assembly 里，就容易出现：

- 依赖膨胀
- 实验性代码污染世界内核
- 插件化边界不清楚

因此当前建议尽快拆出：

- `Atelia.TextAdv2.Gym`
  - 接口与宿主契约
- `Atelia.TextAdv2.DefaultAgent`
  - 官方默认 agent 实现

这里的重点是“先把项目边界分清”，不是马上把它宣告成完全稳定的插件 ABI。

## 插件边界

未来如果演化到插件程序集，推荐边界是：

- 插件实现 `IAgentTurnPolicy`
- 插件只拿到 `AgentTurnInput`
- 插件返回 `AgentTurnDecision`

不推荐把 `SerialWorldRuntime` 直接暴露给插件。

这样才能保证：

- 所有世界变更都经过 turn host
- conflict resolution 仍然可控
- replay / trace / trainer 边界不被绕开

这会很像 RoboCode 的策略插件，而不是让插件直接拿世界句柄。

## 当前第一版骨架

当前独立 assembly 里只放最小但方向正确的内容：

- `DefaultAgentGoalState`
- `DefaultAgentMemorySlice`
- `DefaultAgentTurnBudget`
- `DefaultAgentTurnContext`
- `IDefaultAgentContextBuilder`
- `IDefaultAgentTurnPolicy`
- `IDefaultAgentGoalSource`
- `IDefaultAgentMemorySource`
- `IDefaultAgentBudgetSource`
- 一个把 richer context seam 适配回 Gym 最小 contract 的 `DefaultAgentTurnPolicyAdapter`
- 一个默认的 passthrough context builder
- 少量参考 policy

其中最重要的判断是：

- goal / memory / budget / dynamic context builder 属于 agent 侧
- 它们不进入 `WorldTruth`
- 但应有稳定的 assembly-level shape，便于后续逐步填充

## 为什么现在先做“薄骨架”

因为当前阶段最需要的是：

- 先把层次与边界分对
- 再逐步增强 cognition

如果现在就把默认 agent 实现做得过厚，风险是：

- 过早绑定某一种 LLM 工作流
- 过早绑定某一种 memory 结构
- 过早绑定某一种 prompting 方式

所以当前骨架故意偏薄：

- `PassthroughDefaultAgentContextBuilder` 只是把几种来源拼成统一 context
- `DefaultAgentTurnPolicyAdapter` 负责把 richer context seam 接回 `IAgentTurnPolicy`
- 参考 policy 只提供最简单的 keep / first-available-move 行为

这里有一个刻意的分层判断：

- `Gym` 的 canonical seam 仍保持 `AgentTurnInput -> AgentTurnDecision`
- `DefaultAgent` 内部再用 `DefaultAgentTurnContext` 承载 goal / memory / budget
- 不把这些 richer cognition 字段直接塞回 `Gym` 的最小 contract

## 当前推荐演化路径

1. 继续让 `TextAdv2` 核心保持 world/gym primitive 的简洁性
2. 把 richer context builder、memory、goal、budget 一步步做进 `TextAdv2.DefaultAgent`
3. 等默认实现稳定后，再考虑插件发现、外部程序集加载与 metadata

总结起来，`DefaultAgent` 独立 assembly 的意义，不只是“项目拆分”，而是明确宣告：

- cognition 是可快速实验和替换的
- world truth 与 turn host 应尽量稳定

当前测试也按这个理解收口：

- `DefaultAgent` 相关测试主要是 behavior / composition smoke tests
- 它们验证默认 agent 层如何接上 `Gym` 与 runtime observation
- 它们不应被误解为“已经存在严格 assembly isolation contract 的证明”
