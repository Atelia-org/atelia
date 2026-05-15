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

## 文件

| 文件 | 说明 |
|:---|:---|
| `FridgeEntry.cs` | PipeMux 入口：冰箱状态持久化测试（put-egg / get-egg / status / reset） |
| `TextAdv.csproj` | 项目文件 |

## 注册与使用

```bash
# 注册（一次性）
pmux :register fridge \
  /repos/focus/atelia/prototypes/TextAdv/bin/Debug/net10.0/Atelia.TextAdv.dll \
  Atelia.TextAdv.FridgeEntry.BuildFridge

# 使用
pmux fridge status
pmux fridge put-egg --count 5
pmux fridge get-egg --count 2
pmux fridge reset
```

## 设计原则

- 世界状态用 `DurableDict<string>`（mixed dict）建模，便于快速演化 schema
- 每个有意义的操作结束后 `Commit`，保证 crash recovery
- 数据目录：`/tmp/atelia-textadv-fridge/`（后续可改为 repo 内路径）
