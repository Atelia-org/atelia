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
| `game` | `GameEntry.cs` | **主入口**：荒岛求生文本冒险游戏（new / help / look-around / edit-memory-notebook / interact / explore / rest-a-while / dev-go） |

## 当前代码分层

- `GameEntry.cs`：PipeMux + System.CommandLine 薄入口，负责主命令入口与共享宿主状态
- `GameEntry.Dev.cs`：开发者调试命令；从主入口拆分出的 dev CLI 绑定与导出 helper
- `GameSimulation.cs`：Schema/Core；世界 bootstrap、StateJournal schema key、底层 ledger accessor
- `GameSimulation.ActorJournal.cs`：每个 actor 的第一人称诊断导出视图；尽量基于 `turnHistory` / 私有 resolution 按需生成 Markdown，而不是维护独立持久化副本
- `GameSimulation.AutonomousDiagnostics.cs`：开发者诊断跑批；托管终端玩家、补足 diagnostic LLM players，并尽量复用统一的 collected-turn 流程自动推进固定回合数
- `GameSimulation.Perception.cs`：Read-side projection；`Perception-Bundle`、可见性枚举、turn status、interaction lookup
- `GameSimulation.TurnFlow.cs`：Write-side workflow；Small/Large-Action 落账、回合归档、GM/LLM player 驱动，以及 `Working` / effect-slot / collected-turn 机械推进
- `PlayerActionGuideCatalog.cs`：共享玩家操作手册；Terminal `help` / 最简帮助提示 / LLM Player 操作手册 / tool schema 描述共用这一个数据源
- `GmWorldEditService.cs`：GM-style 世界编辑工具集；通过 `MethodToolWrapper` 暴露 Location / Item / Actor / Interaction 账本工具
- `GameMasterResolver.cs`：真实 GM Agent 工具循环；把 `GmWorldEditService` 包装为 LLM 可调用工具。运行时只支持真实 LLM GM；测试通过显式注入 `GameMasterStub`
- `GamePresenter.cs`：把 `LocationPerception` 渲染成玩家看到的文本
- `GameActionValidator.cs`：DeepSeekV4 + tool call 驱动的 validator，负责逐步校验事前推理（PreActionReason）

## 设计文档

- `amnesia-core-loop-decisions.md`：围绕 `Amnesia`、离散回合、`Large-Action`、`Reason-Trace`、`Memory-Notebook` 的核心玩法收敛
- `turn-sequence-and-memory-notebook-decisions.md`：围绕回合步骤序列、`Memory-Notebook` 状态模型、逐步验证与语料产出形态的当前共识
- `gm-agent-world-resolution-design.md`：围绕 TRPG GM Agent、世界账本、工具化结算与动态世界生成的设计草案

## 注册与使用

### 直接进程入口

长时间自动模拟更适合直接启动 TextAdv 进程，避免 PipeMux 单次请求 timeout：

```bash
dotnet /repos/focus/atelia/prototypes/TextAdv/bin/Debug/net10.0/Atelia.TextAdv.dll \
  --repo-dir /tmp/atelia-textadv-long-run \
  new

dotnet /repos/focus/atelia/prototypes/TextAdv/bin/Debug/net10.0/Atelia.TextAdv.dll \
  --repo-dir /tmp/atelia-textadv-long-run \
  --actor-journal-dir /tmp/atelia-textadv-long-run-journals \
  dev-run-autonomous-rounds --ensure-llm-players 2 50
```

直接入口支持两个仅用于进程启动的全局参数：

- `--repo-dir <dir>`：覆盖本进程使用的游戏存档目录
- `--actor-journal-dir <dir>`：覆盖本进程导出的 actor journal 目录

### PipeMux 入口

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

# interact 统一入口：interaction-id 来自 look-around 的“可交互”列表；系统会判定它是 small 还是 large
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
│   │   ├── player → { kind: "player", controllerKind: "external-terminal", name, locationId, profileNote, active, memoryNotebook }
│   │   └── ...     → { kind: "player" | "npc", controllerKind?: "internal-llm", name, locationId, profileNote, active, memoryNotebook? }
│   ├── interactions → DurableDict<string>
│   └── initialLocation → "beach"
├── game → DurableDict<string>
│   ├── day / slot / slotsPerDay
│   ├── activeActorIds → DurableDict<string>
│   ├── currentTurn → {
│   │     acceptedStepsByActor,
│   │     largeActionByActor,
│   │     notebookSnapshot,
│   │     nextStepNumber,
│   │     ...
│   │   }
│   ├── turnHistory → DurableDict<string> # actor journal 导出等诊断视图优先从这里派生
│   └── lastResolutionByActor → DurableDict<string>
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
- Terminal 玩家默认只常驻一条最简帮助提示；完整操作速查通过 `pmux game help` 临时查看，也可用 `pmux game help on|off` 切换是否常驻
- `Perception-Bundle` 已经支持按 `actorId` 投影：当前位置、可见角色、持有物品和 Memory-Notebook 都来自 actor ledger；终端玩家不再维护额外镜像
- `currentTurn` 当前只保留真正驱动流程的账本字段：`acceptedStepsByActor`、`largeActionByActor`、`notebookSnapshot`、`nextStepNumber` 等；`dev-turn-status` 里的 barrier / next actor 由当前提交状态即时推导
- 当存在多个 active actor 时，终端玩家的真实 Large-Action 通过 validator 后会先写入回合收集账本；当前 MVP 会依次驱动 pending 的 `controllerKind=internal-llm` 玩家，让它基于自己的 `Perception-Bundle` 调用工具提交 Large-Action，通过同一套 validator 后进入 collected-turn resolver
- LLM Player Agent 首版开放 `player_edit_memory_notebook` Small-Action、`player_rest_a_while` 与 `player_explore` 两个显式 Large-Action 工具，以及统一处理当前可见 interaction 的 `player_interact`。`player_interact` 会由系统按当前 interaction 判定 small / large：small interaction 立即执行，large interaction 暂存为本回合的 Large-Action proposal；但每回合最终仍必须提交 exactly one Large-Action。运行时要求真实 provider 可用，失败时直接报错。若诊断跑批不想启用真实内部玩家，可在更外层改用保守诊断动作
- collected-turn resolver 首版按终端玩家的大型动作推进世界，其它 active actor 的意图会进入 `turnHistory` 和结算摘要；自动诊断回合也应尽量复用这条统一流程，而不是维护一套平行结算逻辑
- 当前 validator 默认走 `DeepSeekV4ChatClient`
- 数据目录默认是 `/tmp/atelia-textadv-game/`，可通过 `ATELIA_TEXTADV_REPO_DIR` 覆盖（适合并行开多个试验世界）

## Phase 4 调试命令

这些命令只用于开发者验证多主体账本，不代表正式玩家 API：

- `pmux game dev-add-llm-player [--location <locationId>] <actor-id> <name> <profile-note>`：创建一个 active 内部玩家 actor（`kind=player`，`controllerKind=internal-llm`），并加入 `game.activeActorIds`
- `pmux game dev-look-actor <actor-id>`：按指定 actor 投影并渲染 `Perception-Bundle`
- `pmux game dev-turn-status`：查看当前推导出的 barrier / next actor，以及每个 active actor 的 Large-Action 提交状态
- `pmux game dev-submit-large-action [--payload <payload>] <actor-id> <action-kind> <summary> <reason>`：绕过 validator 为任意 active actor 提交一个 Large-Action，用来验证 `acceptedStepsByActor` / `largeActionByActor` 和 barrier 流转
- `pmux game dev-show-actor-journal <actor-id>`：显示指定 actor 的第一人称诊断导出视图
- `pmux game dev-export-actor-journals [--output-dir <dir>]`：把所有 actor journal 导出为 Markdown 文件（优先从 `turnHistory` 等现有账本信息派生）
- `pmux game dev-run-autonomous-rounds [--ensure-llm-players <n>] [--real-agents] [--skip-export] [--output-dir <dir>] <rounds>`：托管终端玩家自动推进若干诊断回合；GM 结算始终要求真实 provider，未开 `--real-agents` 时其余 diagnostic 内部玩家只提交保守动作

当前内部玩家会被创建、持久化和投影视角；每个 `Perception-Bundle` 会包含 actor 自身的 name / kind / profileNote，避免角色缺少自我锚点。世界层不再用 `terminal-player` / `llm-player` 区分角色本体，`pmux game` 只是 `controllerKind=external-terminal` 的一个 connector，而内置 LLM 则对应 `controllerKind=internal-llm`。真实 LLM Player 默认使用两阶段 `director-executor` 管线：先用无工具的导演阶段整理角色事实、猜测、欲望/恐惧、风险姿态、notebook 建议和推荐 Large-Action，再把这份导演札记作为 observation 交给带工具的执行阶段。执行阶段仍必须通过 `player_edit_memory_notebook` / `player_rest_a_while` / `player_explore` / `player_interact` 行动，并继续走同一套 validator；其中 `player_interact` 是统一 interaction 入口，系统会按当前 interaction 判定 small / large，而每回合最终仍要落成 exactly one Large-Action。运行时不再提供 LLM Player fallback；若需要可控回归，应显式注入 `LlmPlayerStub`，或在诊断命令外层直接提交保守动作。开发者仍可用 `dev-submit-large-action` 手动模拟其它动作。

Terminal `help` 文本、`GameEntry` 里各主要命令的 `reason` 参数说明、Validator 的共享 groundedness 规则，以及 LLM Player 的操作手册、原生 tool schema 描述现在共用 `PlayerActionGuideCatalog`。在它内部，又用 `PlayerActionGuideText` 这层 `const string` 文本库承载可供 `[Tool]` / `[ToolParam]` attribute 直接复用的编译期常量。这样终端玩家看到的帮助、命令行入参提示、validator 检查的证据边界、以及内置玩家得到的动作边界、事前推理要求、记事本书写约束可以保持一致，减少三条路径之间的语义漂移。

多主体 Large-Action 收齐后，系统会进入真实 GM collected-turn staged resolver。该 resolver 把所有 active actor 的 Large-Action intent、事前推理、validator feedback 和各自行前 `Perception-Bundle` 注入同一个 GM 会话，分三阶段执行：多主体意图裁决与 hard truth 落账 → 账本审计 → 各 actor 私有结算反馈与终端玩家摘要。GM 统一使用 `gm_move_actor` 移动任意 active player actor；终端玩家也同样通过 `gm_move_actor(player, ...)` 移动。GM 工具集还提供 `gm_set_actor_resolution`，用于写入 `game.lastResolutionByActor[actorId]`；下一回合每个 actor 的 `Perception-Bundle.LastResolution` 直接读取自己的私有反馈。运行时不再提供 deterministic world-resolution fallback；需要可控回归时，应在测试中显式注入 `GameMasterStub`。

actor journal 是诊断优先的只读导出视图：每个 active actor 的第一人称日志优先基于 `turnHistory`、私有 resolution 和相关回合摘要按需生成，而不是单独维护一份持久化账本。`dev-run-autonomous-rounds` 默认会补足 2 个 diagnostic 内部玩家，托管终端玩家持续探索，并在结束后导出日志；若未开 `--real-agents`，其他 diagnostic actor 会提交保守动作，但世界结算仍走正式 GM 路径。

## Validator 配置

- `DEEPSEEK_API_KEY`：必需，用于真实 DeepSeek validator 调用
- `ATELIA_TEXTADV_VALIDATOR_MODEL_ID`：可选，默认 `deepseek-v4-flash`
- `ATELIA_TEXTADV_GM_MODEL_ID`：可选，默认跟 validator 一样使用 `deepseek-v4-flash`
- `ATELIA_TEXTADV_GM_MAX_ROUNDS`：可选，真实 GM Agent 工具循环最大轮数，默认 `4`
- `ATELIA_TEXTADV_LLM_PLAYER_PIPELINE`：可选，`director-executor` / `single`；默认 `director-executor`。`single` 会跳过导演札记，直接进入工具执行阶段
- `ATELIA_TEXTADV_LLM_PLAYER_MODEL_ID`：可选，默认 `deepseek-v4-flash`
- `ATELIA_TEXTADV_LLM_PLAYER_MAX_ATTEMPTS`：可选，每个 LLM Player 每回合最多提交尝试次数，默认 `3`
- `ATELIA_TEXTADV_REPO_DIR`：可选，覆盖游戏存档目录；默认 `/tmp/atelia-textadv-game/`
- `ATELIA_TEXTADV_ACTOR_JOURNAL_DIR`：可选，覆盖 actor journal Markdown 导出目录；默认 `<repoDir>/actor-journals`
- `DEEPSEEK_MODEL`：可选 fallback，若未设置 `ATELIA_TEXTADV_VALIDATOR_MODEL_ID` 则使用它
- `DEEPSEEK_BASE_URL`：可选，覆盖默认 DeepSeek base URL

PipeMux 的持久 host 进程可能不会读取每次 `pmux game ...` 调用前临时设置的 env；需要稳定切换模型或日志环境时，建议在注册或启动 host 前设置好相关环境变量。

对于 `dev-run-autonomous-rounds` 这类长跑批，优先使用上面的直接进程入口；PipeMux 更适合 `look-around`、`explore`、`rest-a-while` 这类短命令交互。

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

- `gm_create_location` / `gm_link_locations` / `gm_move_actor`
- 终端玩家也统一通过 `gm_move_actor(player, ...)` 移动
- `gm_create_item`
- `gm_create_npc`
- `gm_move_item_to_actor` / `gm_place_item_at_location`
- `gm_add_interaction`
- `gm_set_visibility`
- `gm_set_interaction_visibility`
- `gm_set_actor_resolution`

因此 GM 可以在探索新区域时创建新 Location、可见物品、可见 NPC 和可交互 affordance。下一回合 `Perception-Bundle` 会把当前地点可见物品、角色与交互项渲染给玩家。

`interact` 是执行已落账 affordance 的统一入口：

- 终端玩家只能执行当前 `Perception-Bundle` 中可见的 `interaction-id`
- 宿主会先根据该 interaction 的 turn cost 判定它是 small 还是 large
- 动作仍走 `GameActionValidator`
- small interaction 会立即执行；large interaction 通过 validator 后会作为本回合的 Large-Action proposal 暂存，再交给 GM Agent 根据 `targetKind` / `targetId` / `actionKind` / `effectNote` 结算
- 若 GM 在摘要中引入新的可见物品、NPC 或可执行动作，应先通过工具落账
- `gm_add_interaction` 会要求 `precondition_note`；没有特别条件时写 `none`
- GM 可以用 `gm_set_interaction_visibility` 隐藏已经消耗或暂时不该显示的 affordance
- `take` / `drop` / `give` 这类交互应通过 `gm_move_item_to_actor` 或 `gm_place_item_at_location` 更新 `ownerActorId` / `locationId`
