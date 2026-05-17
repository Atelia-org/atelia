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
- `gm_move_actor`
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

### Phase 1: `rest-a-while` 的 GM-assisted resolution

保持现有命令不变，只把 `rest-a-while` 的硬编码结算替换为 GM-assisted 结算。

首版 GM 工具可以很少：

- `gm_record_world_event`
- `gm_set_resolution_summary`
- `gm_append_rule_note`

这一阶段暂不要求 GM 创建复杂地图或 NPC。目标是跑通“Large-Action 后由 GM tool calls 产生可审计结算”的最小闭环。

### Phase 2: 正式 `explore` / `move` Large-Action

新增正式探索动作，不复用 `dev-go`。

当玩家试图走进山洞、密林深处或调查未知出口时，GM 可以通过工具创建新 Location、连接地点、记录发现事件，然后把下一回合 Perception-Bundle 投影给玩家。

这一阶段新增工具：

- `gm_create_location`
- `gm_link_locations`
- `gm_move_actor`
- `gm_add_location_note`

### Phase 3: Item / NPC / Interaction Ledger

将物品、NPC、交互 affordance 提升为账本对象。

创建 Item 或 NPC 时，GM 不只写描述，还要写：

- 稳定 ID。
- 所在位置或持有者。
- 可见名称。
- 私有 GM note。
- 可交互动作列表。
- 基础前置条件。

这一阶段要开始防止“描述里有钥匙，但账本里没有钥匙”的漂移。

### Phase 4: 事件队列与多主体同步

引入 SimWorld 方向中的“动作耗时 + 事件队列”。

Large-Action 不一定立即完成，而是可以创建 reservation。事件真正出队时重验前置条件。如果条件失效，生成失败、打断、阻塞或部分成功结果。

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
| `gm_move_actor` | 移动 actor | actor 和目标 location 必须存在 |
| `gm_append_rule_note` | 追加规则或设定笔记 | 必须标注来源 turn id |

工具返回值应简短但包含稳定 ID，帮助 GM 在后续调用中引用。

## 开放问题

1. GM Agent 是否应在 Phase 1 就使用 Agent.Core 的完整工具循环，还是先做 TextAdv 内的最小 loop？
2. `GmResolution` 应该先只包含 summary，还是立刻包含 events / observations / rule notes？
3. GM 生成新 Location 时，description 是 hard truth 字段，还是 narrative skin 字段？
4. 玩家动作 intent 应如何从 CLI 参数升级到统一 `ActionIntent`？
5. GM 失败或无工具调用时，世界是否回滚到 Large-Action 前，还是保留 accepted step 并要求重试 GM resolution？

## 推荐下一步

下一步最适合做一个窄实现：

1. 新增 `GameMasterResolver` 的接口和一个 deterministic fake 实现。
2. 在 `ApplyRestAWhile` 的结算点接入 fake resolver。
3. 记录 `gmTrace` 和 `worldEvents` 到 StateJournal。
4. 再把 fake resolver 替换成真实 LLM + 工具循环。

这样可以先把事务边界、账本结构和回放链路跑通，再让 GM Agent 进入核心闭环。
