# Phase 1 Findings: Single User Message Tool Loop

**状态**：Completed smoke validation
**日期**：2026-05-17

---

## 1. 结论

单条 `user` message 驱动 tool-loop 在最小迷宫任务上可行。

本阶段已经跑通：

- 本地 fake policy tool-loop。
- DeepSeek V4 `ping-llm` JSON 协议测试。
- DeepSeek V4 `maze-llm-run` 真实工具循环。

真实迷宫运行中，模型连续调用 `maze.move`，按路径：

```text
east -> east -> south -> south -> east -> east -> north
```

最终到达 `(5,2)`，第 8 步返回：

```text
Reached the goal at (5,2) in 7 steps.
```

这说明模型不依赖 assistant/tool role 历史回放，也能在每轮重新渲染的单 user message 中保持任务状态并继续行动。

---

## 2. 已完成任务

- T-00：新建 `prototypes/MutableContextAgentProto` 控制台项目，并加入 `Atelia.sln`。
- T-01：实现 `EventLog`、`WorkingContext`、行动日志、记忆项、临时视图。
- T-02：实现 `SingleUserContextRenderer`，可渲染一条完整 user message。
- T-10：实现绑定 DeepSeek V4 的最小 Chat client。
- T-11：实现 JSON 工具协议、parser、tool dispatcher。
- T-12：实现固定迷宫环境与 `maze.look` / `maze.move` / `maze.status`。
- T-13：实现 fake policy 并跑通迷宫。
- T-14：接入 DeepSeek V4 并跑通真实迷宫。
- T-15：记录本复盘文档。

---

## 3. 关键经验

### 3.1 单 user message 形态没有天然阻塞 tool-loop

模型可以接受“当前工作上下文 + 可用工具 + JSON 输出协议”作为唯一输入，并持续执行多步任务。

这里的关键不是 message role 交替，而是每轮投影中必须明确包含：

- 当前目标
- 近期行动日志
- 当前已知状态
- 可用工具说明
- 严格输出格式

### 3.2 模型会偏离自定义 JSON 协议

第一次真实 `maze-llm-run` 暴露了协议坑：

- 模型有时省略 `id`。
- 模型有时不用 `name`，而是用 `tool` 或 `tool_name`。
- 模型可能把 JSON 包在 markdown fence 里。

因此 `ToolCallParser` 从严格 parser 调整为宽容 parser：

- 自动提取 fenced JSON 或文本中的首尾 JSON object。
- 缺失 `id` 时自动补 `call-N`。
- 接受 `name` / `tool` / `tool_name`。
- 接受 `arguments` / `args`。

这不意味着协议可以无限宽松，而是 Phase 1 的真实结论：自定义 tool protocol 必须设计 repair/normalization 层。

### 3.3 tool result 不用 `tool` role 回灌也可行

工具结果通过 `WorkingContext` 的行动日志与记忆项进入下一轮单 user message。模型仍能利用这些状态继续行动。

这支持后续 micro-wizard 方向：工具结果不必原样回灌，可以由框架转成可控的工作上下文节点。

### 3.4 当前上下文仍会线性膨胀

Fake run 与 LLM run 都会把每步位置记忆追加到 `WorkingContext.Memories`。这对迷宫任务无害，但已经能看出 Phase 2/3 必须引入：

- 状态类记忆的替换而非追加。
- transient view 的生命周期策略。
- Memory Notebook 的折叠/摘要/展开。

这正好为后续 micro-wizard 提供实验动机。

---

## 4. 后续补强

Reviewer 复盘后补了两点：

- `maze-fake-run` 现在也会生成 fake model JSON，并经过 `ToolCallParser` 与 `ToolDispatcher` 执行，因此无 API key 路径也覆盖 T-11 的协议链路。
- `maze-llm-run` 会把 raw request/response 写入 `.atelia/debug-logs/MutableContextAgentProto/*.jsonl`，方便复盘真实模型协议偏移。

同时，`WorkingContext` 中的工具行动日志只保留短摘要；完整工具 payload 留在 `EventLog` 或 run log，避免 fake/LLM 路径把工具结果原样塞回上下文。

---

## 5. 当前命令

```bash
dotnet run --project prototypes/MutableContextAgentProto -- smoke
dotnet run --project prototypes/MutableContextAgentProto -- render-demo
dotnet run --project prototypes/MutableContextAgentProto -- maze-demo
dotnet run --project prototypes/MutableContextAgentProto -- maze-fake-run
dotnet run --project prototypes/MutableContextAgentProto -- ping-llm
dotnet run --project prototypes/MutableContextAgentProto -- maze-llm-run
```

`ping-llm` 与 `maze-llm-run` 需要：

```bash
DEEPSEEK_BASE_URL=...
DEEPSEEK_API_KEY=...
```

---

## 6. 验证记录

已通过：

```bash
dotnet build prototypes/MutableContextAgentProto/MutableContextAgentProto.csproj
dotnet run --project prototypes/MutableContextAgentProto -- smoke
dotnet run --project prototypes/MutableContextAgentProto -- render-demo
dotnet run --project prototypes/MutableContextAgentProto -- maze-demo
dotnet run --project prototypes/MutableContextAgentProto -- maze-fake-run
dotnet run --project prototypes/MutableContextAgentProto -- ping-llm
dotnet run --project prototypes/MutableContextAgentProto -- maze-llm-run
```

真实 LLM 迷宫结果：

```text
Reached the goal at (5,2) in 7 steps.
EventLog entries: 24
```

补充复验中，模型探索性地撞了一次墙并多次调用 `maze.look`，最终仍在 7 次有效移动后到达终点：

```text
Reached goal at (5,2) in 7 steps.
EventLog entries: 42
Run log: .atelia/debug-logs/MutableContextAgentProto/20260517-150118-maze-llm-run.jsonl
```

这说明真实模型路径不是确定最短策略，但单 user message 投影仍足以承载纠错、观察与继续行动。

---

## 7. Phase 2 建议

进入文本 micro-wizard 前，建议先把“当前状态类记忆”改成 keyed memory，避免同类事实无限追加。

然后再启动 T-20 到 T-25：

- 文本沙盒
- transient file view
- inspection intent
- selective remember/discard
- text replace 不回灌完整旧文本
