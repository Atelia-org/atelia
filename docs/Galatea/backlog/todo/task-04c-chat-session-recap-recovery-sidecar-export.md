# Task 04c: ChatSession Recap Recovery Sidecar Export

> 状态：Blocked by Task 04b
> 建议执行者：适合接续 ChatSession recovery/report 的实现会话
> 依赖：Task 04b 的结构化 recovery report

## 背景

Task 04b 只返回结构化 finding/report。Task 04c 将这些 finding 输出为 sidecar JSON / Markdown，供人工审阅、Markdown 导出器、后续 legacy recovery 或升级工具使用。

## 目标

新增 sidecar exporter：

- JSON：机器可读，包含每个 finding 的 old/new head、range、confidence、reason。
- Markdown：人类可读，列出疑似 compaction、source range、recap 文本摘要、warnings。
- 明确区分 `anchored`、`inferred`、`unresolved`。
- 不修改原 ChatSession repo。

## 非目标

- 不把推断结果写回 durable record。
- 不实现 repo 原地升级。
- 不实现 recap 展开导出全流程；只提供 sidecar 给后续工具消费。

## 验收

- 能从 Task 04b report 生成 JSON sidecar。
- 能从 Task 04b report 生成 Markdown 审阅报告。
- unresolved finding 在输出中明确标注，不被误称为已恢复。
- 输出包含 recovery warnings。
