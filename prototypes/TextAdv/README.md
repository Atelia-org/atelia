# TextAdv — 基于 PipeMux + StateJournal 的文字冒险原型

> 目标：探索文本回合制游戏作为 Native-Agentic 训练沙盒。
> 背景：见[异世界转生型训练沙盒企划案](/repos/qa-dump/docs/idea/native-agentic-isekai-proposal.md)。

## 架构

```
LLM Agent (Copilot / Claude Code)
    │  pmux textadv <command>
    ▼
┌─────────────────────────────┐
│  PipeMux 持久进程            │
│  ┌─────────────────────────┐│
│  │  TextAdv CLI             ││
│  │  (System.CommandLine)    ││
│  └──────────┬──────────────┘│
│             │                │
│  ┌──────────▼──────────────┐│
│  │  StateJournal Repository ││
│  │  (持久化世界状态)         ││
│  └─────────────────────────┘│
└─────────────────────────────┘
```

## 入口

| 入口 | 文件 | 说明 |
|:---|:---|:---|
| `fridge` | `FridgeEntry.cs` | 调试/echo 测试：冰箱状态持久化（put-egg / get-egg / status / reset） |
| `game` | `GameEntry.cs` | **主入口**：荒岛求生文本冒险游戏（new / look-around / edit-memory-notebook / interact / explore / rest-a-while / dev-go） |

## 当前代码分层

- `GameEntry.cs`：PipeMux + System.CommandLine 薄入口，负责打开仓库与绑定命令
- `GameSimulation.cs`：世界 bootstrap、状态查询、移动结算等核心逻辑
- `GmWorldEditService.cs`：GM-style 世界编辑工具集；通过 `MethodToolWrapper` 暴露 Location / Item / Actor / Interaction 账本工具
- `GameMasterResolver.cs`：真实 GM Agent 工具循环；把 `GmWorldEditService` 包装为 LLM 可调用工具，失败时回退 deterministic resolver
- `GamePresenter.cs`：把 `LocationPerception` 渲染成玩家看到的文本
- `GameActionValidator.cs`：DeepSeekV4 + tool call 驱动的 validator，负责逐步校验事前推理（PreActionReason）

## 设计文档

- `amnesia-core-loop-decisions.md`：围绕 `Amnesia`、离散回合、`Large-Action`、`Reason-Trace`、`Memory-Notebook` 的核心玩法收敛
- `turn-sequence-and-memory-notebook-decisions.md`：围绕回合步骤序列、`Memory-Notebook` 状态模型、逐步验证与语料产出形态的当前共识
- `gm-agent-world-resolution-design.md`：围绕 TRPG GM Agent、世界账本、工具化结算与动态世界生成的设计草案

## 注册与使用

```bash
# 游戏主入口（荒岛求生）
pmux :register game \
  /repos/focus/atelia/prototypes/TextAdv/bin/Debug/net10.0/Atelia.TextAdv.dll \
  Atelia.TextAdv.GameEntry.BuildGame

# 开始新游戏
pmux game new

# 查看当前最小 Perception-Bundle
pmux game look-around

# Small-Action：用 TextEditScript 编辑私人 Memory-Notebook（第一个参数永远是事前推理）
# 玩家输入时可以省略 <text-edit-script> 根节点；宿主会自动补齐。
pmux game edit-memory-notebook \
  "我需要把当前直接可见且可能很快遗忘的导航信息记进私人笔记。" \
  '<insert side="after" anchor="tail">记住：沙滩 north 通往密林。</insert>'

# 只预演，不落地：仍会执行同一套 parse、after-view 预测和 validator
pmux game edit-memory-notebook --dry-run \
  "我想先看看这条笔记在当前证据边界下能否通过 validator。" \
  '<insert side="after" anchor="tail">怀疑北边树林里可能有淡水，尚未确认。</insert>'

# Large-Action：原地休息一会，并结束回合
pmux game rest-a-while \
  "我已经先把当前最关键信息写进 notebook，而且当前没有比短暂休息更急迫的动作，所以现在原地休息一会是合理的。"

# Large-Action：执行当前可见的交互 affordance；interaction-id 来自 look-around 的“可交互”列表
pmux game interact \
  "当前可见交互提示允许我检查这处痕迹，执行它是基于眼前线索的直接观察。" \
  inspect-drag-marks

# Large-Action：向指定方向探索；若该方向没有出口，GM-style resolver 会创建新 Location 并移动玩家
pmux game explore --focus "山洞入口" \
  "北边已有密林，继续寻找遮蔽处或山洞入口有助于获得更稳定的庇护。" \
  north

# 调试移动：不参与回合结算，只用于两个地点的最小地图 sanity check
pmux game dev-go north
pmux game dev-go south

# 冰箱测试入口（调试用）
pmux :register fridge \
  /repos/focus/atelia/prototypes/TextAdv/bin/Debug/net10.0/Atelia.TextAdv.dll \
  Atelia.TextAdv.FridgeEntry.BuildFridge
```

## 世界状态结构

```
root (DurableDict<string>)
├── world → DurableDict<string>
│   ├── locations → DurableDict<string>
│   │   ├── beach → { name, description, exits: { north → "forest" } }
│   │   └── forest → { name, description, exits: { south → "beach" } }
│   ├── items → DurableDict<string> # item: { name, description, locationId | ownerActorId, visibility }
│   ├── actors → DurableDict<string>
│   │   ├── player → { kind: "terminal-player", name, locationId, profileNote, active, memoryNotebook }
│   │   └── ...     → { kind: "llm-player" | "npc", name, locationId, profileNote, active, memoryNotebook? }
│   ├── interactions → DurableDict<string>
│   └── initialLocation → "beach"
├── game → DurableDict<string>
│   ├── day / slot / slotsPerDay
│   ├── activeActorIds → DurableDict<string>
│   ├── currentTurn → {
│   │     turnOwnerActorId,
│   │     barrierState,
│   │     acceptedSteps,          # legacy mirror for terminal player
│   │     acceptedStepsByActor,
│   │     largeActionByActor,
│   │     notebookSnapshot,
│   │     nextStepNumber,
│   │     ...
│   │   }
│   ├── turnHistory → DurableDict<string>
│   └── lastResolution → string?
└── player → DurableDict<string>
  ├── location → "beach"          # 终端玩家兼容镜像
  └── memoryNotebook → DurableText # 终端玩家兼容镜像
```

## 设计原则

- 世界状态用 `DurableDict<string>`（mixed dict）建模，便于快速演化 schema
- 地点之间的关系统一用稳定 `LocationId`（字符串）表示，避免业务层混用对象引用与显式 ID
- 每个有意义的操作结束后 `Commit`，保证 crash recovery
- 当前最小原型优先立住“Perception-Bundle -> Small-Action -> Large-Action -> 结算”流程
- `Memory-Notebook` 作为 Player 私有持久状态，当前以 `DurableText` 承载
- `Memory-Notebook` 当前以 block view 向 Player 展示；玩家可输入不带根节点的编辑片段，宿主会自动补成 canonical XML `TextEditScript`
- 所有玩家动作的第一个必填参数都应表示事前推理：先说明依据当前证据为什么准备这么做，再给出动作本身；不鼓励事后合理化式解释
- `edit-memory-notebook --dry-run` 会执行与正式提交同构的 preview + validator，但不会写入状态；适合先试探 after-view 与 validator 边界
- 玩家视图应自带“当前可执行动作”速查，默认按失忆玩家设计，不假定玩家记得上一次输出里的规则说明
- `Perception-Bundle` 已经支持按 `actorId` 投影：当前位置、可见角色、持有物品和 Memory-Notebook 都来自 actor ledger；终端玩家的旧 `root.player` 字段暂时只作为兼容镜像
- `currentTurn` 已经预留并使用 `acceptedStepsByActor`、`largeActionByActor`、`turnOwnerActorId`、`barrierState`；后续真实 LLM Player loop 可直接接入这组账本
- 当存在多个 active actor 时，终端玩家的真实 Large-Action 通过 validator 后会先写入回合收集账本；当前 MVP 会依次驱动 pending `llm-player`，让它基于自己的 `Perception-Bundle` 调用工具提交 Large-Action，通过同一套 validator 后进入 collected-turn resolver
- LLM Player Agent 首版开放 `player_edit_memory_notebook` Small-Action，以及 `player_rest_a_while`、`player_explore`、`player_interact` 三个 Large-Action 工具；若未配置 API key、模式为 deterministic、provider 失败或 validator 多次拒绝，会回退为“谨慎观察并暂不移动”
- collected-turn resolver 首版按终端玩家的大型动作推进世界，其它 active actor 的意图会进入 `turnHistory` 和结算摘要；后续再升级为真正的多意图 GM 裁决
- 当前 validator 默认走 `DeepSeekV4ChatClient`
- 数据目录：`/tmp/atelia-textadv-game/`（后续可改为 repo 内路径）

## Phase 4 调试命令

这些命令只用于开发者验证多主体账本，不代表正式玩家 API：

- `pmux game dev-add-llm-player [--location <locationId>] <actor-id> <name> <profile-note>`：创建一个 active `llm-player` actor，并加入 `game.activeActorIds`
- `pmux game dev-look-actor <actor-id>`：按指定 actor 投影并渲染 `Perception-Bundle`
- `pmux game dev-turn-status`：查看当前 `barrierState`、`turnOwnerActorId` 和每个 active actor 的 Large-Action 提交状态
- `pmux game dev-submit-large-action [--payload <payload>] <actor-id> <action-kind> <summary> <reason>`：绕过 validator 为任意 active actor 提交一个 Large-Action，用来验证 `acceptedStepsByActor` / `largeActionByActor` 和 barrier 流转

当前 `llm-player` 会被创建、持久化和投影视角；每个 `Perception-Bundle` 会包含 actor 自身的 name / kind / profileNote，避免内部玩家不知道“自己是谁”。真实 LLM Player 默认使用两阶段 `director-executor` 管线：先用无工具的导演阶段整理角色事实、猜测、欲望/恐惧、风险姿态、notebook 建议和推荐 Large-Action，再把这份导演札记作为 observation 交给带工具的执行阶段。执行阶段仍必须通过 `player_edit_memory_notebook` / `player_rest_a_while` / `player_explore` / `player_interact` 行动，并继续走同一套 validator。未配置 API key、模式为 deterministic、provider 失败或多次重试失败时，系统会 fallback 提交“谨慎观察并暂不移动”。开发者仍可用 `dev-submit-large-action` 手动模拟其它动作。

多主体 Large-Action 收齐后，系统会先尝试真实 GM collected-turn staged resolver。该 resolver 把所有 active actor 的 Large-Action intent、事前推理、validator feedback 和各自行前 `Perception-Bundle` 注入同一个 GM 会话，分三阶段执行：多主体意图裁决与 hard truth 落账 → 账本审计 → 终端玩家可见摘要。GM 工具集新增 `gm_move_actor`，因此真实 GM 可以移动任意 active player actor，而不是只能移动终端玩家。若真实 GM 未启用或失败，当前 MVP 仍回退到终端玩家主导的 deterministic 结算。

## Validator 配置

- `DEEPSEEK_API_KEY`：必需，用于真实 DeepSeek validator 调用
- `ATELIA_TEXTADV_VALIDATOR_MODEL_ID`：可选，默认 `deepseek-v4-flash`
- `ATELIA_TEXTADV_GM_MODEL_ID`：可选，默认跟 validator 一样使用 `deepseek-v4-flash`
- `ATELIA_TEXTADV_GM_MODE`：可选，`auto` / `llm` / `deterministic`；默认 `auto`，有 `DEEPSEEK_API_KEY` 时优先尝试真实 GM Agent
- `ATELIA_TEXTADV_GM_MAX_ROUNDS`：可选，真实 GM Agent 工具循环最大轮数，默认 `4`
- `ATELIA_TEXTADV_LLM_PLAYER_MODE`：可选，`auto` / `llm` / `deterministic`；默认 `auto`，有 `DEEPSEEK_API_KEY` 时尝试真实 LLM Player Agent
- `ATELIA_TEXTADV_LLM_PLAYER_PIPELINE`：可选，`director-executor` / `single`；默认 `director-executor`。`single` 会跳过导演札记，直接进入工具执行阶段
- `ATELIA_TEXTADV_LLM_PLAYER_MODEL_ID`：可选，默认 `deepseek-v4-flash`
- `ATELIA_TEXTADV_LLM_PLAYER_MAX_ATTEMPTS`：可选，每个 LLM Player 每回合最多提交尝试次数，默认 `3`
- `DEEPSEEK_MODEL`：可选 fallback，若未设置 `ATELIA_TEXTADV_VALIDATOR_MODEL_ID` 则使用它
- `DEEPSEEK_BASE_URL`：可选，覆盖默认 DeepSeek base URL

PipeMux 的持久 host 进程可能不会读取每次 `pmux game ...` 调用前临时设置的 env；需要稳定切换模式时，建议在注册或启动 host 前设置好相关环境变量。

当前 validator 用 tool call 做结构化裁决：

- 若模型没有调用问题指出工具，则视为验证通过
- 若模型调用 `point_out_issues`，则视为验证不通过，并将工具参数转成反馈文本
- 对于 notebook 编辑，宿主会把“当前 notebook 块视图 + TextEditScript + 预测 after-view + 事前推理”一起喂给 validator
- dry-run / validator 里的预测 after-view 不承诺真实新 block id；新插入块只会以 `[new-N]` 形式占位显示，实际 block id 要等真正写入时才会分配

之所以使用 `point_out_issues` 这个 ASCII 工具名，而不是直接用中文工具名，是为了避免 OpenAI-compatible function name 约束带来的 provider 兼容性问题；中文语义通过 tool description 传达给模型。

## GM Agent 工具循环

`explore` / `interact` 当前会优先尝试真实 GM Agent。GM Agent 通过工具更新账本，而不是只输出叙事文本。结算已经拆成同一会话内的少数阶段，每个阶段用更专注的 observation 驱动 GM：

- `explore`: 地图与移动落账 → 实体与交互账本审计 → 玩家可见摘要
- `interact`: 交互直接后果 → affordance 生命周期审计 → 玩家可见摘要

- `gm_create_location` / `gm_link_locations` / `gm_move_player` / `gm_move_actor`
- `gm_create_item`
- `gm_create_npc`
- `gm_move_item_to_actor` / `gm_place_item_at_location`
- `gm_add_interaction`
- `gm_set_visibility`
- `gm_set_interaction_visibility`

因此 GM 可以在探索新区域时创建新 Location、可见物品、可见 NPC 和可交互 affordance。下一回合 `Perception-Bundle` 会把当前地点可见物品、角色与交互项渲染给玩家。

`interact` 会把已落账的 affordance 变成真正的 Large-Action：

- 终端玩家只能执行当前 `Perception-Bundle` 中可见的 `interaction-id`
- 动作仍走 `GameActionValidator`
- 通过后由 GM Agent 根据 `targetKind` / `targetId` / `actionKind` / `effectNote` 结算
- 若 GM 在摘要中引入新的可见物品、NPC 或可执行动作，应先通过工具落账
- `gm_add_interaction` 会要求 `precondition_note`；没有特别条件时写 `none`
- GM 可以用 `gm_set_interaction_visibility` 隐藏已经消耗或暂时不该显示的 affordance
- `take` / `drop` / `give` 这类交互应通过 `gm_move_item_to_actor` 或 `gm_place_item_at_location` 更新 `ownerActorId` / `locationId`
