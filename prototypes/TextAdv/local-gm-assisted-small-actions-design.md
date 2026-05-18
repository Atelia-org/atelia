# TextAdv — Unified Action Cost, Effect-Slot, And Working-State Design

> 状态：设计已收敛，核心方案已首轮落地
> 适用范围：`prototypes/TextAdv/`
> 目标：在不引入 local micro-phase 的前提下，让 `Small-Action`、`Large-Action`、持续性工作、共享世界延迟结算收敛到一套统一动作模型里

## 当前实现进度

截至目前，原型里已经落地：

- `TurnCost / EffectScope / EffectSlots` 进入 interaction 元数据与感知层
- `TurnCost = 0 + immediate + self` 的即时小动作
- `TurnCost = 0 + turn-end` 的 deferred small-action 队列与回合末结算
- `TurnCost > 1` 的 `Working` 账本
- `per-turn-end / on-completion` 在 `Working` 中的基础推进
- `Working actor` 暂时退出 active actor barrier，完成后重新回到 active
- 当 terminal player 处于 `Working` 时，其他 active actor 仍可继续推进回合，世界不会卡死
- 单意图 collected-turn 已由机械层直接调度，可执行单个 non-terminal actor 的 `rest / explore / interact`
- effect 结算已区分 `actor-facing` 与 `terminal-visible`
- 同地点内其他 actor 的 `Working` 进展已可被 terminal player 以保守文案观察到
- LLM 路径已引入专门的 slot-aware effect resolver，不再借道普通 interaction resolver 传递 `[effect-slot]`
- 真实 GM 成功分支也会并入 `prelude` 带来的 actor-facing 摘要，避免只写进 terminal summary

尚未完整落地：

- `Working` 被外界事件打断后的唤醒与重决策
- 多个 non-terminal active actor 同回合同时提交时，仍需依赖真实 GM collected-turn 裁决；测试若要做可控回归，应显式注入 `GameMasterStub`
- 其他 actor 的可观察 `Working` 反馈目前仍以“同地点可直接观察”为主，尚未扩展到邻近地点的声响、震动、机关联动等间接感知
- `Working` actor 的 accepted-step / journal 细节还偏 MVP，后续仍可继续润色

## 一句话结论

推荐把动作统一拆成三组正交维度：

1. `TurnCost`
   - `0`
   - `1`
   - `N (>1)`
2. `EffectSlots`
   - `immediate`
   - `turn-end`
   - `per-turn-end`
   - `on-completion`
3. `EffectScope`
   - `self`
   - `room`
   - `adjacent-room`
   - `scene`

并建立一个强约束：

> **只有只影响 acting actor 私有可用状态的效果，才能进入 `immediate` 槽位。**

这条约束是整套方案成立的关键。

它意味着：

- 当前回合内立刻生效的，只能是私有结果
- 任何会改写共享世界、影响其他 actor 感知或动作前沿的结果，都必须推迟到 `turn-end` 之后的统一结算点
- 因而不需要先引入同地点多 actor 的 local micro-phase
- 但又能保留 `Working`、持续工作、延迟共享结果、回合末冲突归并这些能力

我认为这个思路**合理、可行，而且很适合当前 MVP 阶段**。

## 为什么这条路线比“局部子回合”更适合当前阶段

当前原型已经明确采用：

- 全局离散 slot
- active actor barrier
- `Large-Action` 触发统一 world tick

如果现在直接追求“同地点多个 active actor 在同一回合内即时互相影响”，就会立刻遇到：

- `PerceptionVersion`
- `WorldVersion`
- LLM actor 中途打断与重决策
- 共享世界即时改写的先后手

而本方案把复杂度集中到两个更可控的地方：

- 回合末统一结算
- `Working` 的自动续行与打断

这更贴近当前代码结构，也更符合“先做稳，再追求强涌现”的节奏。

## 动作模型总览

### 1. `TurnCost`

表示该动作会消耗 actor 多少个离散回合。

#### `TurnCost = 0`

- 不结束当前回合
- actor 仍保持 `Active`
- 当前回合中还可以继续提交后续动作

典型例子：

- 写 notebook
- 喝一口自己的水
- 整理背包
- 给自己的水袋灌水

#### `TurnCost = 1`

- 结束当前回合
- actor 在下一回合开始时恢复 `Active`
- 它是当前模型中最接近传统 `Large-Action` 的情形
- 在当前 MVP 里，它会占用该 actor 本回合唯一的“终结动作槽位”

典型例子：

- 正式探索一个区域
- 与对象进行一段足以吃掉本回合的深入交互
- 尝试获取一个存在共享冲突的外部资源

#### `TurnCost = N (>1)`

- 当前回合结束
- actor 从 `Active` 进入 `Working`
- 后续回合若无打断事件，则不唤醒该 actor 做完整决策，而是自动续行这项工作
- 工作完成后，actor 再回到 `Active`
- 在发起它的那个回合，它同样占用该 actor 本回合唯一的“终结动作槽位”

典型例子：

- 挖矿
- 长时间搜索
- 抄录、修理、加工
- 守候、缓慢采集、持续侦测

### 2. `EffectSlots`

动作不只描述“要花多久”，还要描述“结果在什么时候生效”。

推荐把效果分到四个槽位：

#### `immediate`

在动作被 validator 接受后立刻生效。

语义：

- 立即进入 acting actor 当前回合的后续可用状态
- 后续同回合步骤可以把它当作既成事实
- 不等待 barrier

**MVP 强约束：这里只能放 `self/private` 效果。**

#### `turn-end`

在当前全局回合结束、进入统一结算时生效。

语义：

- barrier 前不改共享世界的 canonical perception
- barrier 时与其他 actor 的本回合结果一起归并
- 可以做冲突裁决

典型用途：

- 拿可能被别人同时争抢的唯一果子
- 打开会影响公共空间的机关
- 设置从下一回合开始才对外有效的陷阱

#### `per-turn-end`

用于持续工作型动作。

语义：

- 每消耗完一个工作回合，在该回合末触发一次
- 适合“逐回合产出”或“逐回合推进进度”的行为

典型用途：

- 挖矿每回合产出一点矿石
- 搜索每回合积累一点进展
- 长时间观察每回合产出一段新的线索摘要

#### `on-completion`

在整个 `TurnCost = N` 的工作完整完成后生效。

语义：

- 只有工作未被打断、成功跑完所有回合才触发
- 适合“最终成果”“最终解锁”“整体完成态”

典型用途：

- 挖通一处薄壁
- 修理完成一个装置
- 完成一份长篇抄录

### 3. `EffectScope`

它描述的是效果触达范围，而不是何时生效。

建议保留：

```text
self | room | adjacent-room | scene
```

含义：

- `self`
  - 只影响 acting actor 及其私有物
- `room`
  - 影响当前地点或其可见实体
- `adjacent-room`
  - 影响邻接地点或出口关系
- `scene`
  - 影响更大局部场景，但仍不等于整张世界图

`EffectScope` 与 `EffectSlots` 应当正交：

- “影响自己，但在工作完成时才生效”是合理的
- “影响房间，但只能在回合末生效”也是合理的

## 核心约束：为什么 `immediate` 只能是 self/private

这是整份设计里最重要的稳定器。

更形式化地说：

> 若一个效果进入 `immediate` 槽位，则它不得改变其他 actor 在当前回合内的合法感知边界、合法动作前沿、受击中可能性或共享资源占用结果。

也就是说，`immediate` 不能让别的 actor 在本回合里：

- 立刻看到新事物
- 立刻失去眼前事物
- 立刻多出或少掉一个可做 interaction
- 立刻被触发器命中、阻挡、传送或伤害
- 立刻因为共享资源被占用而失败

这样一来：

- 当前回合内的即时变化只影响 acting actor 自己
- 其他 actor 仍然可以基于本回合起点 perception 行动
- 共享世界的变化统一留到 `turn-end` 结算

这正是“不引入 local micro-phase 仍可落地”的关键。

## 这套模型下，GM-assisted local interaction 还需要吗

需要，但它的职责被明确收窄了。

### 它主要负责什么

- 处理 `TurnCost = 0`
- 且存在 `immediate` 私有效果
- 且仅影响 acting actor 自身或其私人物件

例如：

- 喝一口水，更新角色当前状态描述
- 给自己的水袋灌满水
- 整理、检查、记录自己手里的东西

### 它不再负责什么

- 立刻改写公共场景
- 立刻让别人看见新东西
- 立刻夺取共享资源
- 立刻触发会影响他人的机关后果

这些都应该进入：

- `turn-end`
- `per-turn-end`
- `on-completion`

### 为什么这很好

这样一来，新的 **GM-assisted local interaction** 流程就不会变成“半个实时世界引擎”，而只需要解决：

- 私有即时反馈
- 私有状态更新
- 当前 actor 的后续同回合可用状态

这条边界非常清晰。

## 动作可有多个效果槽位，但 MVP 要节制

理论上，一个动作可以同时拥有多个槽位中的效果。

例如“连续挖矿 3 回合”可以同时有：

- `per-turn-end`
  - 每回合产出少量碎矿
- `on-completion`
  - 最终挖开一处薄壁

例如“开始长时间抄录”也可以有：

- `immediate`
  - 自己知道“我已经开始这项工作”
- `on-completion`
  - 最终获得完整誊本

但 MVP 阶段建议加一条工程约束：

> 一个动作可以有多个效果槽位，但应避免在同一个动作里混入过多异构共享世界效果。

更具体地说：

- `immediate` 最适合承载私有即时变化
- 共享世界的主结果，优先只放进一个非即时槽位
- 复杂机关链、长连锁事件，不要在首版 small-action 里一次做满

这样实现成本和 debug 成本都会低很多。

## Barrier 语义如何保持稳定

这套模型的核心收益之一，就是 barrier 可以继续成立。

### 当前回合内

- actor 可以提交多个 `TurnCost = 0` 的动作
- 其中只有私有效果能立刻落地
- 共享世界相关结果只记录为待结算效果，不立即改变公共 perception
- 一旦某 actor 已提交 `TurnCost = 1` 或 `TurnCost = N` 动作，该 actor 本回合不再继续提交其他动作

### 当前回合结束时

- 所有 `turn-end` 槽位效果一起进入统一结算
- 与 `TurnCost = 1` 的终结动作、`Working` 的当回合尾效果一起归并
- 冲突在这一刻处理

### 下一回合开始时

- 共享结果已经成为 canonical world state
- 其他 actor 才会在新的 `PerceptionBundle` 中看到它

因此，系统仍然保持：

- 回合内本地整理可连续发生
- 共享世界结果在离散边界统一落账

### 与现有 core loop 的兼容口径

这套新模型并没有推翻“每回合最终要有一个终结动作”的大原则，而是把它重述为：

- `TurnCost = 0`
  - 不占用终结动作槽位
- `TurnCost = 1`
  - 占用终结动作槽位
- `TurnCost = N`
  - 也占用终结动作槽位，并把未来若干回合折叠进 `Working`

因此，当前 MVP 仍然可以保持：

- 一个回合内可有零到多个 `TurnCost = 0`
- 最终至多有一个 `TurnCost = 1` 或 `TurnCost = N`

这与原先的 `Small-Action + Large-Action` 骨架是兼容的，只是表达方式更统一。

## 非即时效果的关键规则：同回合内不能当成已完成事实

这点必须写得很死，否则系统很快会自相矛盾。

对于进入 `turn-end` / `per-turn-end` / `on-completion` 的效果：

- 当前回合中，它们还不是 canonical reality
- acting actor 可以收到“你已尝试开始/你正在进行/你预计将得到”的反馈
- 但不能在同回合后续 `Reason-Trace` 里把它们当成已完成事实

例如：

- “我准备摘那颗果子”
  - 可以作为已提交的意图存在
- “我已经拿到了那颗果子，所以现在把它吃掉”
  - 在 `turn-end` 之前不应成立

因此建议给 `TurnStep` 引入更精确的结果字段，而不只是一段笼统 summary。

建议新增：

```text
StepOutcomeSummary
StepOutcomeState = "committed-now" | "pending-turn-end" | "working" | "completed"
```

用途：

- 玩家 UI 更容易讲清“这是已经到手，还是只是正在进行”
- validator 可以据此拒绝“把 pending 效果当既成事实”的后续推理
- replay / training data 的语义也更稳定

## 共享资源冲突仍然存在，但被压缩到了回合末

这套方案并没有凭空消灭冲突，只是把冲突集中到 `turn-end` 归并点。

例如：

- A 想拿唯一的一颗果子
- B 也想拿同一颗果子

两边都可以在本回合内提交意图，但 canonical 结果必须在回合末裁决：

- 谁成功
- 谁失败
- 失败者收到什么反馈
- 是否允许部分成功

这意味着系统需要一个明确原则：

> 对共享对象的占用、夺取、消耗、开启、破坏，一律以回合末统一结算结果为准。

这条原则很重要，因为它让“同地点多人争抢”先不必上 micro-phase，也能维持规则清晰。

## `Working` 应成为一等状态，而不是旁支补丁

### Actor 运行状态

建议把 actor 的运行态显式建模为：

```text
Active | Working
```

其中：

- `Active`
  - 当前回合会被正常展示 perception，并需要自己决策
- `Working`
  - 当前有一个持续中的 `WorkOrder`
  - 若本回合没有唤醒事件，则系统自动续行
  - 若出现唤醒事件，则中断工作并回到 `Active`

### `WorkOrder` 建议字段

建议至少包含：

```text
workId
sourceInteractionId
remainingTurns
effectScope
perTurnEndPlan
completionPlan
interruptPolicy
targetRefs
```

说明：

- `remainingTurns`
  - 剩余还需消耗多少个完整回合
- `perTurnEndPlan`
  - 每回合末要触发的效果描述
- `completionPlan`
  - 完整做完后触发的效果描述
- `interruptPolicy`
  - 被打断后是否允许部分保留进度
- `targetRefs`
  - 当前工作锚定的对象，用于判定相关事件

### `TurnCost = N` 如何计数

推荐采用：

> 当前发起工作的这一回合，就算第一个被消耗的回合。

也就是说：

- 若动作标记 `TurnCost = 3`
- actor 在本回合提交它后结束回合
- 本回合末先结算第 1 次 `per-turn-end`
- 然后还剩 2 个未来回合需要自动续行

这是最直觉、也最容易向玩家解释的口径。

### `Working` 的唤醒条件

首版不要做太聪明的语义相关性推断，建议只认保守硬规则。

推荐把以下事件视为 wake trigger：

- actor 所在地点有其他 actor 进入或离开
- actor 被点名交互、攻击、触碰、阻拦
- actor 自己或其持有物状态被其他效果改变
- `WorkOrder.targetRefs` 发生变化
- 工作所依赖的资源耗尽、失效、被占用或被破坏

若发生 wake trigger：

- 当前工作中断
- actor 下回合回到 `Active`
- 是否保留部分进度，由 `interruptPolicy` 决定

## 推荐的交互元数据

建议 interaction ledger 元数据至少扩成下面这些维度：

```text
turnCost = 0 | 1 | N
effectScope = self | room | adjacent-room | scene
hasImmediateEffect = true | false
hasTurnEndEffect = true | false
hasPerTurnEndEffect = true | false
hasCompletionEffect = true | false
```

若希望更结构化，也可以直接写成：

```text
turnCost
effectScope
effectSlots[]
```

并补充更细的 planner metadata，例如：

```text
workDurationHint
interruptPolicy
conflictClass
```

### 为什么不再坚持旧的 `local-small / deferred-small / turn-ending-large`

旧模型的问题是：

- 它把“消耗几个回合”
- 和“什么时候生效”
- 和“影响范围多大”

缠在了一起。

而现在这三者被拆开后：

- `TurnCost = 0` 不自动等于“立即影响世界”
- `TurnCost = 1` 也不自动等于“只有回合末才有全部效果”
- `TurnCost = N` 不必再作为特殊补丁另写一套规则

统一性会强很多。

## 对几个典型例子的归类

### 写 notebook

- `TurnCost = 0`
- `EffectSlots = [immediate]`
- `EffectScope = self`

### 给自己的水袋灌水

- `TurnCost = 0`
- `EffectSlots = [immediate]`
- `EffectScope = self`

### 试图拿地上唯一的果子

- `TurnCost = 0` 或 `1`
- `EffectSlots = [turn-end]`
- `EffectScope = room`

设计建议：

- 如果想强调“只是顺手一试，暂不占掉整回合”，可设为 `0 + turn-end`
- 如果想强调“这是本回合的主要对外动作”，可设为 `1 + turn-end`

两者都合理，取决于你想给该场景多大博弈密度。

### 埋设地雷 / 陷阱

- `TurnCost = 1` 或 `N`
- `EffectSlots = [turn-end]` 或 `[on-completion]`
- `EffectScope = room`

关键规则：

- 最早只从下一回合开始参与触发判定

### 挖矿 3 回合

- `TurnCost = 3`
- `EffectSlots = [per-turn-end, on-completion]`
- `EffectScope = self` 或 `room`

### 拉动机关，暗门开启

在这版 MVP 约束下，更推荐：

- `TurnCost = 1`
- `EffectSlots = [turn-end]`
- `EffectScope = adjacent-room`

这意味着暗门不会在当前回合中途立刻改写公共现实，而是在回合末统一生效。

这是一个**有意识的简化取舍**，不是长期能力边界。

## 对玩家 UI 的含义

玩家不需要看到内部术语 `TurnCost` 或 `EffectSlots`，但 UI 最好传达两件事：

### 1. 这件事会不会吃掉本回合

可以用更世界内一点的文案：

- `顺手可做`
- `会占用这一回合`
- `会持续忙碌数回合`

### 2. 当前结果是已到手，还是待结算

例如：

- `你灌满了水袋。`
- `你试着去摘那颗果子；结果要到本回合结束时才见分晓。`
- `你开始挖矿，若不被打断，接下来几回合会持续推进。`

这能显著减少规则误解。

## 对运行时结构的直接改造建议

### 1. `InteractionPerception`

建议新增至少：

```csharp
int TurnCost
string EffectScope
IReadOnlyList<string> EffectSlots
```

### 2. `TurnStep`

建议新增：

```csharp
string? StepOutcomeSummary
string StepOutcomeState
```

### 3. Actor 状态

建议新增：

```text
executionState = active | working
currentWorkOrder
```

### 4. 当前回合账本

建议新增：

```text
pendingTurnEndEffects
```

其职责是：

- 收集当前回合所有待在 `turn-end` 生效的效果
- barrier 时统一归并

### 5. 长时工作账本

建议新增：

```text
workingByActor
```

其职责是：

- 保存 `WorkOrder`
- 在每回合结束时自动推进
- 检查 wake trigger

## 推荐的实现顺序

### Phase 1：动作元数据与 trace 落位

1. 给 interaction 增加 `TurnCost / EffectScope / EffectSlots`
2. 给 `TurnStep` 增加 `StepOutcomeSummary / StepOutcomeState`
3. 让 validator 识别“pending 结果不能当既成事实”

### Phase 2：`TurnCost = 0 + immediate + self` 路径

1. 新增收窄职责后的 **GM-assisted local interaction**
2. 它只处理私有即时结果
3. 成功后立即刷新当前 actor perception

### Phase 3：`turn-end` 延迟共享结果

1. 新增 `pendingTurnEndEffects`
2. 回合末统一归并
3. 共享资源冲突也放到这里解决

### Phase 4：`Working`

1. 新增 `executionState`
2. 新增 `WorkOrder`
3. 支持 `per-turn-end / on-completion`
4. 支持 wake trigger

### Phase 5：以后再评估是否需要 local micro-phase

只有当我们明确要追求：

- 同地点多人即时抢夺
- 同回合里连锁触发
- 他人动作立刻改变我的当前可行动作前沿

才值得继续上：

- `WorldVersion`
- `PerceptionVersion`
- local micro-phase

在那之前，这份设计足够支撑一套清晰、统一、可实现的 MVP。

## 最终结论

你提出的这套思路，我的判断是：

- **结构上合理**
- **工程上可行**
- **比“直接做局部子回合”更适合当前阶段**

其中最关键的设计收束是三条：

1. 动作统一拆成：
   - `TurnCost`
   - `EffectSlots`
   - `EffectScope`
2. 只有 `self/private` 效果才能进入 `immediate`
3. `Working` 作为一等运行态建模，而不是回合系统外的临时特判

如果后续继续往代码落地，我建议就沿着这份文档的 `Phase 1 -> Phase 4` 顺序推进。
