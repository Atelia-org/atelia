# TextAdv2 AgentTurn Gym Host

> 状态：draft，但已按本文落第一版代码骨架
> 适用范围：`prototypes/TextAdv2/` 内的同进程 Agent Gym 主循环

本文收口 TextAdv2 中 Agent 与世界交互的第一版主设计。

目标不是一开始就造出分布式 actor runtime，而是先把“无 user 的 endless ReAct loop”钉成一个简单、清晰、可测、可演进的单进程主循环。

## 设计目标

- 明确 Agent 与世界交互的 canonical seam
- 明确 `AgentTurn` 是当前阶段的基本时间粒度
- 让多个 Agent 能在同一 turn 中先观察、再思考、再统一裁决、再统一生效
- 保持世界推进的确定性与可测试性
- 为后续更强的 conflict resolution、dynamic context builder、sleep/awake 调度留下扩展点

## 非目标

- 当前不追求跨进程 Agent bus
- 当前不追求事件推送式异步世界
- 当前不追求完整的 simultaneous physics / combat resolution
- 当前不把 admin authoring surface 混入 agent-facing seam

## 核心边界

### 1. World Core：同步、typed、deterministic

世界内核继续以 `SerialWorldRuntime` 为中心，并复用已经存在的：

- `ObserveBatch(...)`
- typed actor/world mutation API（如 `MoveActor(...)`、`StartActorRouteFollowing(...)`）

它们是 Gym host 的底层 primitive。

世界内核本身仍然保持同步调用，因为：

- 世界状态推进需要单一、确定的提交顺序
- 当前 StateJournal + runtime cache 结构天然适合串行 world commit
- 这样最利于 replay、测试、debug 与规则收口

### 2. Agent Boundary：允许异步

Agent 实现边界使用异步接口。

原因不是为了把世界变异步，而是为了适配：

- LLM 推理
- 远程模型
- 更重的 tool / retrieval / planning 过程

因此当前设计采用：

- world core sync
- agent policy async

### 3. AgentTurn Host：主循环驱动者

`AgentTurnHost` 不直接等于 world，也不直接等于 agent。

它负责：

1. 选择本 turn 要激活的 agent
2. 准备 agent observation
3. 分发给各 agent policy
4. 收集 action intents
5. 交给 conflict resolver 裁决
6. 调用 world 执行裁决后的效果
7. 收集 turn result，供 trace / memory / trainer 使用

## 时间模型

当前阶段明确采用：

- `AgentTurn` 是游戏逻辑时间的基本粒度
- 每个清醒的 agent，每个 turn 都会被激活一次推理
- `keep` 也是显式 action，而不是“没做事”

因此：

- 一个 turn 内，先收集所有 agent 的声明动作
- 裁决后统一结算
- turn 结束时，逻辑时间推进 1 tick

这意味着当前默认映射是：

- `1 AgentTurn = 1 logical tick`

如果一个 turn 中所有 agent 都只选择 `keep`，该 turn 仍然消耗一个 tick。

如果当前没有可激活 agent，则不启动 turn，也不推进时间。

## 当前主循环

当前建议的 canonical 主循环是：

1. 收集所有清醒的 Agent
2. 为这些 Agent 批量准备 observation
3. 并发调用各自的 policy
4. 收集各自声明的 action intent
5. 交给 conflict resolver 做统一裁决
6. 按裁决顺序提交 world mutation
7. 推进逻辑时间 1 tick
8. 收集 turn 后 observation，形成 turn result

以当前骨架来说，步骤 2 和 6 分别落在：

- `ObserveBatch(actor-context...)`
- `MoveActor(...)` / `StartActorRouteFollowing(...)` / `StartActorMining(...)` / `CancelActorEmbodiedState(...)`

## Agent-facing canonical observation

当前第一版 host 对每个 agent 提供的 canonical observation 是：

- `ActorContextObservation`

原因：

- 它已经是当前 actor-facing 的 canonical read model
- 它包含：
  - actor identity
  - current tick
  - 当前地点的窄上下文
  - `availableMoves` 这条 canonical action surface

未来如果需要更强的 RL / LLM context，可继续在 host 层往 `AgentTurnInput` 上叠加：

- supplementary observations
- retrieved memory
- goal state
- budget
- summarized recent history

但这些属于 host-side context construction，不应污染世界内核本身。

当前更推荐的做法是：

- 保持 `Gym` 的最小 canonical seam 为 `AgentTurnInput -> AgentTurnDecision`
- richer goal / memory / budget 放进 agent-side context builder
- 像 `DefaultAgent` 这样的官方实现，可以在自己的 assembly 内通过 adapter 叠 richer context，而不是直接膨胀 `AgentTurnInput`

## Action 模型

当前 action surface 已定义：

- `keep`
- `move(passageId)`
- `start-follow-route(destinationLocationId)`
- `start-mining(worksiteId)`

`StartActorMining(actorId, worksiteId)` 只表达“在此处开始采矿”，具体 `ticksPerYield / yieldItemId / yieldAmount` 由 world-side 的 `Location` mining worksite profile 提供。
- `cancel-current-process`

这与 TextAdv2 当前已落地的 embodied-process world 能力对齐。

设计上先把“agent 声明动作”和“世界执行效果”分开：

- agent 产出 `AgentActionIntent`
- resolver 把 intent 裁成 `ResolvedAgentOperation`
- `ResolvedAgentOperation` 持有真正的 `ExecutableAgentAction`
- host 只负责按顺序执行 operation、推进时间并收集结果

这样后续就能自然扩展出：

- talk
- use-item
- attack
- start-task / continue-task
- multi-step macro action

## Conflict Resolution 边界

冲突裁决是 Host 层的一等扩展点，而不是直接塞进 agent policy，也不是立即硬编码进 `SerialWorldRuntime`。

原因：

- 多 agent 的 simultaneous declaration，是 turn host 的语义
- 当前 `SerialWorldRuntime` 的 mutation API 明确仍按确定顺序推进 world
- “多个 agent 同时声明动作”与“底层如何提交 world mutation”不是同一层问题

所以当前设计拆成两层：

- declaration layer：agent 同步地“宣告意图”
- execution layer：resolver 产出本 turn 的 executable operation

当前默认 resolver 很保守：

- 保留输入顺序
- 对当前已知 intent 一律按输入顺序接受
- resolver 负责把原始 intent 映射成 typed `ExecutableAgentAction`
- `AgentTurnHost` 不再二次解释 raw intent，只顺序执行 operation
- 还不做 richer conflict semantics

它的意义主要是先把扩展点钉住。

未来如果要做更强裁决，这一层可以扩展为：

- 同地点资源争用
- 门/ passage 容量约束
- 速度 / 先后手
- 碰撞 / 打断
- 同步交换位置是否允许
- 多动作合并

## 推荐的代码边界

当前第一版建议保持下列分工：

- `SerialWorldRuntime`
  - world-facing typed operations
  - 不感知 agent policy
- `IAgentTurnPolicy`
  - 单个 agent 的思考边界
- `IAgentTurnConflictResolver`
  - 多 agent intents 到 executable operation 的裁决边界
- `AgentTurnHost`
  - turn orchestration
  - 负责区分 resolver acceptance 与 execution failure

## 为什么不是“世界异步推送 observation + agent 异步入队 action”

那条路线当然更强，但当前不值得先走。

它会马上引入：

- 事件顺序定义
- 消息幂等
- backpressure
- cancellation
- dropped / stale actions
- inbox / outbox 生命周期
- replay 难度显著上升

而当前 Gym 最重要的，是先建立一个稳定的 endless ReAct playground。

因此当前优先级是：

- 先把 turn loop 钉实
- 再把 context builder 做强
- 最后再考虑是否把 host 演化成更异步的 runtime

## Dynamic Context Builder 方向

当前 `ActorContextObservation` 只是 canonical state surface，不是最终 prompt context。

后续推荐把 Agent 每轮使用的上下文拆成：

- canonical state
- goal state
- retrieved memory
- relevant recent history
- self-summary / plan-summary
- action surface

关键点是：

- recent history 只是上下文素材之一
- 上下文应按当前问题动态构建，而不是无限堆 transcript

因此未来更合理的演化路径是：

- 保持 world core 简洁稳定
- 在 host / context builder 层不断增强 agent cognition

## 当前第一版代码骨架的取舍

第一版实现刻意保持克制：

- 默认把所有注册 agent 都视为“awake”
- 一个 agent 每 turn 只声明一个 primary action
- 默认 `keep` / `move` 两种 action
- 默认 resolver 只做顺序归一化，不做复杂物理冲突裁决
- host 统一在 turn 后推进 1 tick
- host 会在 turn 前后都收集 actor-context，便于 trainer / trace / memory 接管

这版不是最终 Gym runtime，但它已经把最关键的控制面收出来了。

## 后续建议

下一步最自然的演进方向是：

1. 加入 awake / sleep / busy activation policy
2. 在 agent-side context builder 上继续扩充 goal / memory / budget / dynamic context
3. 把 turn result 接到 trace / replay / RL dataset
4. 为 `E2eCli` 或专用 trainer host 提供一层 episode driver
5. 在 resolver 中逐步引入真正的 conflict rules

总结起来，当前设计的核心判断是：

- 多 agent 同 turn 声明动作，应该有
- 统一裁决阶段，应该有
- 世界提交 primitive 仍保持同步顺序化，应该保留
- 更复杂的“异步消息总线化世界”，现在先不做
