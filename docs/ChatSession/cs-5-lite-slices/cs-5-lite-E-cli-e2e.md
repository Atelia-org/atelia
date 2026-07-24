# CS-5-lite-E: CLI 与端到端验收

> 状态：Task Brief
> 日期：2026-07-25
> 父任务：[CS-5-lite](../cs-5-lite-sessionjournal-derived-recap-store.md)

## 目标

收口 `ChatSession.BacktestCli` 的命令面、README 和端到端验收，使 CS-5-lite 可以被后续 coding 会话直接运行。

## 推荐命令

新增命令优先于复用旧命令：

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary-session-journal \
  --input gitignore/session-journal/cyber-copy-upgraded \
  --threshold-tokens 12000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection local-deepseek \
  --output gitignore/backtest/session-journal-rolling-summary.jsonl \
  --call-log-dir gitignore/backtest/session-journal-rolling-summary-calls
```

原因：旧 `replay-rolling-summary --input` 默认是 legacy export JSON。新增命令可以避免输入格式二义性。

## 非目标

- 不移除 legacy 命令。
- 不实现 Context Planner。
- 不实现自动 tail-only projection。

## 验收

- README 记录新命令与工作流。
- 从 `import-session-journal` 生成的 repo 可以运行 replay。
- 输出 JSONL 能链接 artifact 和 LLM call log。
- 删除 `derived/recaps/v1/` 后重新运行可重新生成 artifacts。
- 至少有一条不依赖真实 LLM 的测试路径；真实 LLM 回测可作为手工命令记录。
