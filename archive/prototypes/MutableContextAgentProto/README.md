# MutableContextAgentProto

实验目标：验证 mutable working context 能否通过单条 `user` message 驱动 tool-loop，而不是把原始 message history 逐条回灌给模型。

当前工具调用路径使用 OpenAI Chat 风格的服务端 `tools` / `tool_calls`：请求体携带 tool schema，响应体读取服务器已解析的 `choices[].message.tool_calls`，不再要求模型在 assistant 正文中手写 JSON 工具协议。

对应计划文档：[Mutable Context Agent 原型递归分解计划](../../docs/Agent/mutable-context-agent-prototype-plan.md)。

## Phase 1 Commands

```bash
dotnet run --project prototypes/MutableContextAgentProto -- smoke
dotnet run --project prototypes/MutableContextAgentProto -- render-demo
dotnet run --project prototypes/MutableContextAgentProto -- maze-demo
dotnet run --project prototypes/MutableContextAgentProto -- maze-fake-run
dotnet run --project prototypes/MutableContextAgentProto -- phase2-fake-wizard

DEEPSEEK_BASE_URL=... DEEPSEEK_API_KEY=... \
  dotnet run --project prototypes/MutableContextAgentProto -- ping-llm

DEEPSEEK_BASE_URL=... DEEPSEEK_API_KEY=... \
  dotnet run --project prototypes/MutableContextAgentProto -- maze-llm-run

DEEPSEEK_BASE_URL=... DEEPSEEK_API_KEY=... \
  dotnet run --project prototypes/MutableContextAgentProto -- phase2-llm-wizard
```

## Phase 1 Scope

- `Core/`: append-only event log, mutable working context, single user message renderer.
- `Protocol/`: provider-neutral tool definition, normalized tool-call request, and minimal dispatcher.
- `Llm/`: DeepSeek V4 OpenAI Chat-style client with native server tool-call parsing.
- `Maze/`: deterministic maze world, tools, and fake policy.

This prototype intentionally avoids references to the older `Agent.Core` / `Completion.*` implementations.

## Notes

- [Phase 1 findings](notes/phase-1-findings.md)
- [Phase 2 view_file micro-wizard design](notes/phase-2-microwizard-design.md)
- [Phase 2 findings](notes/phase-2-findings.md)
