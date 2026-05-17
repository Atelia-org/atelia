# MutableContextAgentProto

实验目标：验证 mutable working context 能否通过单条 `user` message 驱动 tool-loop，而不是把原始 message history 逐条回灌给模型。

对应计划文档：[Mutable Context Agent 原型递归分解计划](../../docs/Agent/mutable-context-agent-prototype-plan.md)。

## Phase 1 Commands

```bash
dotnet run --project prototypes/MutableContextAgentProto -- smoke
dotnet run --project prototypes/MutableContextAgentProto -- render-demo
dotnet run --project prototypes/MutableContextAgentProto -- maze-demo
dotnet run --project prototypes/MutableContextAgentProto -- maze-fake-run

DEEPSEEK_BASE_URL=... DEEPSEEK_API_KEY=... \
  dotnet run --project prototypes/MutableContextAgentProto -- ping-llm

DEEPSEEK_BASE_URL=... DEEPSEEK_API_KEY=... \
  dotnet run --project prototypes/MutableContextAgentProto -- maze-llm-run
```

## Phase 1 Scope

- `Core/`: append-only event log, mutable working context, single user message renderer.
- `Protocol/`: JSON response protocol and minimal tool dispatcher.
- `Llm/`: DeepSeek V4 OpenAI Chat-style client.
- `Maze/`: deterministic maze world, tools, and fake policy.

This prototype intentionally avoids references to the older `Agent.Core` / `Completion.*` implementations.
