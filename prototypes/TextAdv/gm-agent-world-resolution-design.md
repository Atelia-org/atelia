# TextAdv - GM Agent World Resolution Design

> 状态：设计意向评估与分阶段实施草案
> 适用范围：`prototypes/TextAdv/`
> 目标：评估并收敛“TRPG GM Agent 驱动动态世界”的软件设计边界

## 一句话结论

这条路线可以进一步完善成合理、可逐步实现的软件设计。

关键取舍是：GM Agent 不应成为世界真相的唯一持有者，也不应直接凭自然语言改写一切状态。更稳妥的结构是：

- 代码层用 StateJournal 维护 hard truth、身份、位置、资源、可见性、时间和审计链。
- GM Agent 负责解释玩家意图、裁决软规则、生成叙事细节，并通过受控工具提出或执行世界变更。
- Narrator 只基于面向玩家的 Perception-Bundle 渲染文本，不直接读取完整隐藏世界。

换句话说，GM Agent 是“受账本和工具约束的 TRPG 裁判”，不是随口续写世界的唯一作者。

## 与现有 TextAdv 的接合点

当前 TextAdv 已经具备这条路线的几个关键支点：

- `GameEntry.cs` 已经提供 PipeMux + System.CommandLine 入口。
- `GameSimulation.cs` 已经维护 `world/game/player/currentTurn/turnHistory`。
- `GameActionValidator.cs` 已经有 LLM tool-call 风格的 groundedness validator。
- `PerceptionBundle`、`TurnStep`、`TurnResolution` 已经表达了“看到局面、提交步骤、回合结算”的骨架。
- StateJournal 已经承担跨进程持久化，PipeMux static 状态只是热态缓存。

最自然的 GM 接入点是在 Large-Action 通过 validator、并被追加到当前回合 `acceptedSteps` 之后，归档当前回合、推进时间、重置下一回合之前。

当前 `ApplyRestAWhile` 中硬编码的 `resolutionSummary` 正是未来 GM-assisted resolution 的占位点：此时本回合完整轨迹已经齐备，代码可以把世界状态、玩家动作意图、当前规则账本投影给 GM Agent，让它开始裁决后果。

## 设计原则

### 1. 世界真相先于叙述投影

TextAdv 应沿用 SimWorld 的方向：先维护世界 hard truth，再将它投影为面向主体的 observation。

GM Agent 可以说“洞口传来潮湿的冷风”，但下面这些事实必须由账本和规则代码托住：

- 洞穴 Location 是否存在。
- 玩家是否位于洞口附近。
- 洞穴与当前位置是否连通。
- 玩家是否能看见、听见或推断到相关信息。
- 新 Location、Item、NPC 是否有明确 creation event。

自然语言可以解释账本，不能替代账本。

### 2. GM 以工具更新世界

GM Agent 不应通过自由文本让宿主“相信世界变了”。它应该调用工具来执行受控修改，例如：

- `gm_create_location`
- `gm_link_locations`
- `gm_move_player`
- `gm_create_item`
- `gm_place_item`
- `gm_record_world_event`
- `gm_append_rule_note`
- `gm_set_resolution_summary`

工具内部负责验证前置条件、写 StateJournal、返回结构化结果。复杂参数首版可以用 JSON string，因为 `MethodToolWrapper` 当前只支持扁平标量参数。

### 3. 账本分层而不是 prompt 堆叠

建议将世界信息分为这些层：

| 层级 | 名称 | 性质 | 示例 |
|:---|:---|:---|:---|
| L0 | World Ledger | hard truth | 地点、实体、位置、资源、时间、事件、可见性 |
| L1 | Perception Packet | 派生视图 | 某玩家当前能感知到的文本与结构化线索 |
| L2 | Rule Schema | 半结构化规则 | 动作类型、前置条件、耗时、失败类型 |
| L3 | Rulebook / Scenario Bible | 软规则文本 | 世界风格、社会习俗、NPC 口吻、题材边界 |
| L4 | Belief / Notebook / Rumor | 世界内信念 | 玩家笔记、NPC 记忆、传闻、假说 |
| L5 | Narrative Rendering | 输出文本 | 下一回合玩家读到的自然语言描述 |

L0 是规则判定的最终依据。L4 可以影响角色决策，但不能被世界 reducer 直接当成事实。

### 4. 保留回合轨迹的可回放性

每次世界推进都应能回放出：

1. 起始 Perception-Bundle。
2. 当前回合 accepted steps。
3. 每一步的 Reason-Trace 与 validator 结果。
4. GM Agent 输入材料。
5. GM Agent tool calls 与 tool results。
6. 落账后的 WorldEvents。
7. 分发给各主体的 ObservationDelivery。
8. 下一回合 Perception-Bundle。

这条链既是调试审计日志，也是未来训练数据的天然形态。

## 建议架构

### 首版组件

`GameEntry`

继续作为 PipeMux/CLI 薄入口，负责打开 repo、取 root、commit、输出渲染文本。

`GameSimulation`

继续作为当前世界状态的低层读写入口。后续可以逐步拆出 `WorldLedger`、`TurnLedger`、`PerceptionProjector`。

`GameActionValidator`

继续负责玩家动作和 Reason-Trace 的 groundedness 检查。它不负责世界后果结算。

`GameMasterResolver`

新增组件。负责在 Large-Action accepted 后组织 GM Agent 会话。输入包括：

- 当前 world snapshot 的可见摘要。
- 当前玩家 Perception-Bundle。
- 当前回合 accepted steps。
- Large-Action intent。
- 规则账本摘要。
- 可用工具定义。

输出包括：

- `GmResolution`。
- GM tool call trace。
- 下一回合渲染所需的 resolution summary。

`GmWorldEditService`

新增工具服务。持有或接收 `(Repository repo, DurableDict<string> root)`，将地图、实体、规则账本更新包装成工具方法。

`GmInvariantChecker`

新增轻量检查器。首版只检查少量硬约束：

- 引用的 LocationId / ActorId / ItemId 是否存在。
- 新增实体 ID 是否唯一。
- 连接边是否指向有效地点。
- 玩家位置是否只能指向有效 Location。
- 新 observation 是否只分发给合法主体。

### 数据流

```text
Player command
  -> GameActionValidator
  -> Append accepted TurnStep
  -> if Large-Action:
       Build TurnResolutionContext
       Run GameMasterResolver
       GM calls GmWorldEditService tools
       GmInvariantChecker validates patch/tool effects
       Record WorldEvents and GM trace
       Advance clock / event queue
       Archive completed turn
       Reset current turn
       Project next Perception-Bundle
       Render to player
```

### GM Agent 会话管线

固定 User 消息可以分成几个稳定段落：

1. 任务说明：你是 TextAdv 的 GM，必须通过工具更新世界状态。
2. 裁决边界：不要把未落账内容当事实，不要泄露玩家不可见信息。
3. 输入材料：玩家可见信息、当前回合动作序列、world ledger 摘要、规则账本。
4. 输出要求：必须先完成必要工具调用，再设置 resolution summary。
5. 停止条件：没有更多必要工具调用后，给出简短裁决总结。

GM 的自由文本可以保留，但宿主只信任工具结果和结构化 summary。

## MVP 路线

### Phase 0: 文档与边界收敛

当前文档就是 Phase 0 的产物。目标是先把 GM Agent 的职责边界钉住，避免实现时滑向“LLM 说什么就是什么”。

### Phase 1: `explore` / `move` 的 GM-style resolution

将原先设想的 `rest-a-while` GM-assisted resolution 与正式 `explore` / `move` Large-Action 合并。

原因是：单纯让 GM 为 `rest-a-while` 写结算摘要，目标和效用都不够清晰；而探索和移动可以直接依托现有 `world.locations`、`exits`、`player.location` 数据结构，形成玩家能感知、能验证、能继续游玩的功能闭环。

这一阶段的目标是：

- 新增正式 `explore` Large-Action，不复用 `dev-go`。
- 若方向已有出口，玩家沿已知出口移动。
- 若方向没有出口，GM-style resolver 通过工具创建新 Location、连接地点、移动玩家。
- Large-Action 仍然走现有 groundedness validator。
- 回合照常归档、推进时钟、重置下一回合。

首版工具应优先服务这个闭环：

- `gm_create_location`
- `gm_link_locations`
- `gm_move_player`

`gm_record_world_event`、`gm_set_resolution_summary`、`gm_append_rule_note` 可以推迟到结算日志和规则账本真正进入实现时再加入。

### Phase 2: 真实 GM Agent 工具循环

在 Phase 1 的 deterministic resolver 跑通后，再把 `GmWorldEditService` 中的 C# 方法用 `MethodToolWrapper` 暴露为 `ITool`，接入真实 LLM GM Agent。

这一阶段不改变玩家命令协议，主要替换 resolver 内部实现：

- deterministic resolver 决定创建什么地点；
- 真实 GM Agent 根据玩家意图、Perception-Bundle 和账本摘要选择调用哪些工具；
- 宿主继续只信任工具结果和账本状态。

当前落地策略：

- `GameMasterResolver` 手写最小工具循环：LLM 输出 tool calls，`ToolExecutor` 执行，宿主用 `ToolResultsMessage` 回灌，直到 LLM 停止调用工具并输出玩家可见摘要。
- `ATELIA_TEXTADV_GM_MODE=auto` 时，有 `DEEPSEEK_API_KEY` 就优先尝试真实 GM Agent；未配置或失败时回退 deterministic resolver。
- `ATELIA_TEXTADV_GM_MODE=deterministic` 可强制关闭真实 GM，便于离线调试。
- `ATELIA_TEXTADV_GM_MODE=llm` 可用于验证真实 GM 路径；若 provider 或工具循环失败，当前仍回退 deterministic resolver 以保护游戏可玩性。
- 首版真实 GM 的工具集合包含 `gm_create_location` / `gm_link_locations` / `gm_move_player`，以及 Phase 3 的 `gm_create_item` / `gm_create_npc` / `gm_add_interaction` / `gm_set_visibility`。GM 可以创建少量可见物品、NPC 和 affordance，但仍不创建复杂规则。

### Phase 3: Item / NPC / Interaction Ledger

将 Item、NPC、Interaction affordance 提升为账本对象，但仍保持“少而硬”的 MVP 范围。

这一阶段的目标不是做完整 RPG 物品系统，而是防止叙事漂移：

- 如果 GM 文本说“洞口有一块锋利贝壳”，账本里就必须有 `Item`。
- 如果 GM 文本说“有人影在密林边缘观察”，账本里就必须有 `NPC` 或至少有 pending entity note。
- 如果某个对象可以被玩家操作，账本里必须记录可交互 affordance，而不是只藏在描述里。

推荐最小 schema：

```text
world
├── items
│   └── item-id
│       ├── name
│       ├── description
│       ├── locationId | ownerActorId
│       ├── visibility       # visible / hidden / discovered
│       └── interactions     # inspect / take / use / open / talk 等 affordance id 列表
├── actors
│   └── actor-id
│       ├── kind             # terminal-player / llm-player / npc
│       ├── name
│       ├── locationId
│       ├── memoryNotebook
│       ├── profileNote
│       └── active
└── interactions
    └── interaction-id
        ├── targetKind       # item / actor / location
        ├── targetId
        ├── actionKind       # inspect / take / talk / use / explore-detail
        ├── visibleLabel
        ├── preconditionNote
        └── effectNote
```

创建 Item 或 NPC 时，GM 不只写描述，还要写：

- 稳定 ID。
- 所在位置或持有者。
- 可见名称。
- 私有 GM note。
- 可交互动作列表。
- 基础前置条件。

首版工具建议：

| 工具 | 作用 | 约束 |
|:---|:---|:---|
| `gm_create_item` | 创建物品并放置到 Location | 必须指定稳定 item_id、name、description、location_id |
| `gm_create_npc` | 创建 NPC actor | 必须指定 actor_id、name、locationId、profileNote |
| `gm_move_item_to_actor` | 把 Item 转移到 Actor 持有 | 用于 take / give / pick-up；当前终端玩家 ActorId 是 `player` |
| `gm_place_item_at_location` | 把 Item 放置到 Location | 用于 drop / place / reveal |
| `gm_add_interaction` | 给 item / actor / location 增加 affordance | target 必须存在；actionKind 首版用自然语言小枚举约束 |
| `gm_set_visibility` | 调整 item/NPC 可见性 | 只能在 GM 结算时调用，不能静默改历史 |
| `gm_set_interaction_visibility` | 调整 affordance 可见性 | 用于隐藏已消耗、暂不可用或被条件遮蔽的交互 |

`Interaction Ledger` 的重要性在于：它把“可以做什么”从叙事文本里捞出来，供 Player Agent 和终端玩家在下一回合的 Perception-Bundle 里稳定读取。这样 LLM Player 不需要凭自然语言猜按钮，GM 也不需要把所有规则写进 prompt。

当前实现状态：

- `world.items`、`world.actors`、`world.interactions` 已经进入 StateJournal 账本。
- 新游戏会创建 `actor:player`，并在 `game.activeActorIds` 中登记终端玩家；旧的 `root.player.location` 暂时保留为终端玩家控制状态，移动时同步镜像到 actor ledger。
- `PerceptionBundle` 会投影当前地点的可见物品、可见 NPC、地点交互、物品交互和 actor 交互。
- Validator 和 GM Agent observation 都包含可见 NPC 与 affordance，避免 Player Agent 或 GM 只靠叙事文本猜测“能做什么”。
- 真实 GM 工具循环已经能通过 `gm_create_npc` 创建可见 NPC，并通过 `gm_add_interaction` 给 `actor:<id>` 添加 `talk` / `inspect` 等交互。
- `pmux game interact '<reason>' '<interaction-id>'` 已经把可见 affordance 提升为可执行的 Large-Action。宿主只允许执行当前 Perception-Bundle 中存在的 interaction，动作通过 validator 后交给 GM Agent 按 `targetKind` / `targetId` / `actionKind` / `effectNote` 结算。
- `interactions` 现在记录 `preconditionNote` 和自身 `visibility`。`preconditionNote` 会进入玩家可见交互说明和 validator observation；`visibility` 让 GM 可以用 `gm_set_interaction_visibility` 隐藏已消耗或暂时不该显示的 affordance。
- `items` 现在支持 `ownerActorId`，玩家视图会显示 `actor:player` 持有的物品。GM 可用 `gm_move_item_to_actor` / `gm_place_item_at_location` 结算拿起、交给、放下等持有关系变化。

这一落地方式特意没有把 `interactions` 嵌入 item 或 actor 内部，而是保留全局 `world.interactions` 索引。原因是后续 Phase 4 里，不同主体的可见 affordance 可能会按 actor、位置、关系、记忆单独过滤；全局交互账本更利于审计、投影和工具校验。

### Phase 4: 简化多主体同步回合

Phase 4 暂不引入动作耗时、reservation、事件队列和出队重验。MVP 继续保持当前离散回合制。

核心规则：

1. 只有一个终端玩家通过 `pmux game ...` 操作。
2. 终端玩家完成一个 validator 通过的 Large-Action 后，形成本回合 barrier。
3. TextAdv 内部依次驱动所有 active LLM Player。
4. 每个 LLM Player 看到自己的 Perception-Bundle、自己的 Memory-Notebook、自己的可用动作说明。
5. 每个 LLM Player 可执行零到多个 Small-Action，最终必须提交一个 Large-Action。
6. 所有 active Player 的 Large-Action 都收齐后，GM Agent 一次性拿到所有大型动作意图，统一结算世界。
7. 结算后为每个 Player 投影下一回合 Perception-Bundle。

这比事件队列更适合当前 TextAdv：

- 保留行动机会稀缺和回合交卷感。
- 避免实时调度、等待、抢占、reservation 重验。
- GM Agent 每回合有完整的多主体意图表，裁决更接近桌面 TRPG 的“所有人声明行动后统一处理”。
- 训练数据天然包含多主体对照：同一个世界状态下，不同 Agent 如何观察、记忆、推理和行动。

推荐新增结构：

```text
game
├── activeActorIds
├── currentTurn
│   ├── turnOwnerActorId      # 当前终端玩家或内部 LLM player
│   ├── acceptedStepsByActor
│   ├── largeActionByActor
│   └── barrierState          # collecting-terminal / collecting-llm / ready-for-gm
└── turnHistory
```

当前 Phase 4 前置落地：

- `game.activeActorIds` 已存在；新游戏会登记 `actor:player`，开发者可用 `dev-add-llm-player` 添加 active `llm-player`。它代表“需要参与 Player 回合收集的主体”，不等同于所有 NPC。
- `world.actors` 中的 `kind` 已预留 `terminal-player` / `llm-player` / `npc`。NPC 可以被 GM 创建和被玩家感知，但暂不自动声明 Large-Action。
- `PerceptionBundle` 已经可以通过 `DescribePerceptionForActor(root, actorId)` 按 actor 投影。地点、可见角色、持有物品和 Memory-Notebook 都从 actor ledger 读取；`DescribeCurrentPerception` 只是 `actor:player` 的便捷入口。
- `actor:player` 与旧 `root.player` 并存。终端玩家的 `location` 和 `memoryNotebook` 已镜像到 actor ledger；旧字段暂时作为兼容层保留，后续可以在 LLM Player loop 稳定后删除。
- `currentTurn` 已预留 `turnOwnerActorId`、`barrierState`、`acceptedStepsByActor`、`largeActionByActor`。当前终端玩家路径仍写入 legacy `acceptedSteps`，同时把同一组步骤挂到 `acceptedStepsByActor["player"]`；旧存档读取时会自动补齐这些 Phase 4 字段。
- Large-Action 现在会落入 `largeActionByActor`。只有 `actor:player` active 时仍保持原有“通过 validator 后立刻 GM 结算”的单玩家流程；如果存在多个 active actor，终端玩家 Large-Action 会先进入回合收集态，并驱动 pending `llm-player` 提交保守 fallback Large-Action。
- pending `llm-player` 的首版 fallback 是 `large/rest-a-while`，语义为“谨慎观察并暂不移动”。这不是最终 Player Agent，只是让 Phase 4 闭环能端到端前进，并把后续真实 LLM Player Agent 的替换点留在同一套提交/validator 边界上。
- `dev-look-actor <actor-id>` 可用于查看任意 actor 的 Perception-Bundle；这验证了未来 LLM Player Agent 的输入可以只包含该 actor 的视角，而不是完整世界真相。
- `dev-turn-status` 可审计当前 barrier 与每个 active actor 的 Large-Action 提交状态。`dev-submit-large-action` 可绕过 validator 模拟任意 active actor 交卷，用于测试 `acceptedStepsByActor` / `largeActionByActor` 和 `ready-for-gm` 流转。
- 多主体收齐后已经进入 `collected-turn resolver`，首版按终端玩家的大型动作推进世界，并把所有 actor 的 Large-Action intent 写入 `turnHistory` 和玩家可见结算摘要。这让 MVP 不再停在 `ready-for-gm`，但还不是最终的多意图 GM 裁决。
- 下一步应把 `collected-turn resolver` 升级为真正的 GM staged resolver：把所有 actor 的 Large-Action intent、各自 Perception-Bundle 和必要账本投影注入同一 GM 会话，由 GM 一次性裁决冲突、顺序和后果。
- GM 结算已经从单次宽 prompt 改为同一会话内的分阶段工具循环：`explore` 依次执行“地图与移动落账 → 实体与交互账本审计 → 玩家可见摘要”，`interact` 依次执行“交互直接后果 → affordance 生命周期审计 → 玩家可见摘要”。每个阶段都会保留前文 history，并在阶段开始注入最新 Perception/账本投影，以减少遗漏实体、交互或可见性更新的概率。

`LLM Player Agent` 的最小行为协议：

- 输入：只给该 actor 的 Perception-Bundle、Memory-Notebook、当前动作指南。
- 输出：通过工具提交动作，而不是直接返回自由文本。
- 工具：复用终端玩家能做的动作边界，例如 `player_edit_memory_notebook`、`player_interact`、`player_explore`、`player_rest_a_while`。
- Validator：与终端玩家同一套 `GameActionValidator`，不为内部 Agent 开后门。
- 失败：validator 不通过时，把反馈作为 Observation 回灌给该 LLM Player，让它重试；MVP 可设置每 actor 每回合最多 2 到 3 次尝试，超过则自动 `rest-a-while` 或 `hesitate`。

优秀人类 TRPG 主持人的对应模式：

- 先让每个玩家声明“这一回合做什么”。
- 对声明含糊或越界的玩家追问/要求改写。
- 收齐后统一判断冲突、顺序和后果。
- 每个玩家只听到自己角色能感知到的反馈。

这正是 Phase 4 应模拟的东西。

### Phase 5: 多 GM role 拆分

当单一 GM Prompt 变得过重后，可以拆成：

- `IntentParser`
- `RuleJudge`
- `WorldReducer`
- `Narrator`
- `Archivist`

首版不需要拆。先让一个 `GameMasterResolver` 跑通闭环，再按压力点拆分。

## 主要风险与缓解

| 风险 | 表现 | 缓解 |
|:---|:---|:---|
| 世界漂移 | GM 把叙事细节逐渐当成事实 | 所有事实变化必须落账，GM 只能通过工具改世界 |
| 隐藏信息泄露 | 输出告诉玩家未感知到的真相 | Narrator 只吃 Perception-Bundle，不吃完整 world truth |
| 规则不可测试 | 规则全藏在 prompt 中 | 高频硬规则提升到 schema 或代码检查 |
| GM 越权改账 | 为戏剧性传送 NPC、生成资源、改写历史 | 工具做前置检查，历史只能追加修正事件 |
| Notebook 污染真相 | 玩家笔记里的猜测被系统当事实 | Notebook 属于 belief 层，reducer 不直接信 notebook |
| 上下文膨胀 | 动态世界越跑越大，GM prompt 失控 | prompt 注入摘要和可查询索引，不塞完整历史 |
| 多主体冲突 | 多人同时拿同一物品或互相等待 | reservation + 出队重验 + 稳定排序键 |
| Tool 参数复杂 | MethodToolWrapper 不支持对象/数组 | 首版拆小工具，复杂结构用 JSON string 并在工具内解析 |
| repo 锁冲突 | 多 PipeMux 实例打开同一 repo | 固定同一 PipeMux session，或将 RepoDir 做成 per-session |

## 初始状态 schema 建议

当前 `world.locations` 可以保留，并逐步扩展：

```text
root
├── world
│   ├── locations
│   ├── actors
│   ├── items
│   ├── worldEvents
│   ├── observationDeliveries
│   └── rulebook
├── game
│   ├── currentTurn
│   ├── turnHistory
│   ├── gmTrace
│   └── eventQueue
└── player
    ├── location
    └── memoryNotebook
```

`rulebook` 首版可以是 `DurableText`，用 TextEditScript 风格的小增量编辑维护。等规则结构稳定后，再把高频字段提升到 durable dict schema。

## 推荐的第一批工具

首批工具应该少而硬：

| 工具 | 作用 | 首版约束 |
|:---|:---|:---|
| `gm_record_world_event` | 追加一条已发生世界事件 | 必须包含 event kind、summary、actor/location 引用 |
| `gm_set_resolution_summary` | 设置本回合玩家可见结算摘要 | 首版建议只允许设置一次 |
| `gm_create_location` | 创建新地点 | location_id 唯一，name/description 必填 |
| `gm_link_locations` | 建立地点连接 | 两端 location 必须存在 |
| `gm_move_player` | 移动当前玩家 | 目标 location 必须存在 |
| `gm_append_rule_note` | 追加规则或设定笔记 | 必须标注来源 turn id |

工具返回值应简短但包含稳定 ID，帮助 GM 在后续调用中引用。

## 开放问题

1. GM Agent 是否应在 Phase 2 使用 Agent.Core 的完整工具循环，还是先做 TextAdv 内的最小 loop？
2. `GmResolution` 应该先只包含 summary，还是立刻包含 events / observations / rule notes？
3. GM 生成新 Location 时，description 是 hard truth 字段，还是 narrative skin 字段？
4. 玩家动作 intent 应如何从 CLI 参数升级到统一 `ActionIntent`？
5. GM 失败或无工具调用时，世界是否回滚到 Large-Action 前，还是保留 accepted step 并要求重试 GM resolution？

## 推荐下一步

下一步最适合做一个窄实现：

1. 新增正式 `explore` Large-Action。
2. 新增 `GmWorldEditService`，先提供 `gm_create_location` / `gm_link_locations` / `gm_move_player`。
3. 让 deterministic resolver 调用这些工具形态的方法，跑通“探索未知方向 -> 创建 Location -> 移动玩家 -> 下一回合感知”的闭环。
4. 再把 deterministic resolver 替换成真实 LLM + 工具循环。

这样可以先把事务边界、账本结构和回放链路跑通，再让 GM Agent 进入核心闭环。
