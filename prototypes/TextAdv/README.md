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
| `game` | `GameEntry.cs` | **主入口**：荒岛求生文本冒险游戏（new / go / look-around） |

## 当前代码分层

- `GameEntry.cs`：PipeMux + System.CommandLine 薄入口，负责打开仓库与绑定命令
- `GameSimulation.cs`：世界 bootstrap、状态查询、移动结算等核心逻辑
- `GamePresenter.cs`：把 `LocationPerception` 渲染成玩家看到的文本
- `GameActionValidator.cs`：DeepSeekV4 + tool call 驱动的 validator，负责逐步校验 `Reason-Trace`

## 设计文档

- `amnesia-core-loop-decisions.md`：围绕 `Amnesia`、离散回合、`Large-Action`、`Reason-Trace`、`Memory-Notebook` 的核心玩法收敛
- `turn-sequence-and-memory-notebook-decisions.md`：围绕回合步骤序列、`Memory-Notebook` 状态模型、逐步验证与语料产出形态的当前共识

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

# Small-Action：编辑私人 Memory-Notebook（每步都要求 Reason-Trace）
pmux game edit-memory-notebook \
  "记住：沙滩 north 通往密林。" \
  "我需要把当前直接可见且可能很快遗忘的导航信息记进私人笔记。"

# Large-Action：原地休息一会，并结束回合
pmux game rest-a-while \
  "我已经先把当前最关键信息写进 notebook，而且当前没有比短暂休息更急迫的动作，所以现在原地休息一会是合理的。"

# 调试移动：不参与回合结算，只用于两个地点的最小地图 sanity check
pmux game go north
pmux game go south

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
│   └── initialLocation → "beach"
├── game → DurableDict<string>
│   ├── day / slot / slotsPerDay
│   ├── currentTurn → { notebookSnapshot, acceptedSteps, nextStepNumber, ... }
│   ├── turnHistory → DurableDict<string>
│   └── lastResolution → string?
└── player → DurableDict<string>
  ├── location → "beach"          # 当前位置 ID
  └── memoryNotebook → DurableText # 私人持久笔记
```

## 设计原则

- 世界状态用 `DurableDict<string>`（mixed dict）建模，便于快速演化 schema
- 地点之间的关系统一用稳定 `LocationId`（字符串）表示，避免业务层混用对象引用与显式 ID
- 每个有意义的操作结束后 `Commit`，保证 crash recovery
- 当前最小原型优先立住“Perception-Bundle -> Small-Action -> Large-Action -> 结算”流程
- `Memory-Notebook` 作为 Player 私有持久状态，当前以 `DurableText` 承载
- 当前 validator 默认走 `DeepSeekV4ChatClient`
- 数据目录：`/tmp/atelia-textadv-game/`（后续可改为 repo 内路径）

## Validator 配置

- `DEEPSEEK_API_KEY`：必需，用于真实 DeepSeek validator 调用
- `ATELIA_TEXTADV_VALIDATOR_MODEL_ID`：可选，默认 `deepseek-v4-flash`
- `DEEPSEEK_MODEL`：可选 fallback，若未设置 `ATELIA_TEXTADV_VALIDATOR_MODEL_ID` 则使用它
- `DEEPSEEK_BASE_URL`：可选，覆盖默认 DeepSeek base URL

当前 validator 用 tool call 做结构化裁决：

- 若模型没有调用问题指出工具，则视为验证通过
- 若模型调用 `point_out_issues`，则视为验证不通过，并将工具参数转成反馈文本

之所以使用 `point_out_issues` 这个 ASCII 工具名，而不是直接用中文工具名，是为了避免 OpenAI-compatible function name 约束带来的 provider 兼容性问题；中文语义通过 tool description 传达给模型。
