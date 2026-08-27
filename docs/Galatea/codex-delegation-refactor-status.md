# Galatea Codex 代行闭环重构状态

> 状态：Complete
>
> 完成日期：2026-08-27

Galatea 叙事 Action 现在可以经 `OutboundMailExtractor`、exact `Codex` route 和
进程内 fixed-thread FIFO 委派给 Codex app-server；bounded final 或退信进入
`ReplyInbox`，并在后续普通玩家回合中以独立兄弟信息块 one-shot 呈现。

长期产品契约、配置、failure semantics 与可重复的 operator runbook 以
[`prototypes/Galatea/README.md`](../../prototypes/Galatea/README.md) 为权威。相关自动化测试覆盖
composite Observation、strict sidecar transport、coordinator FIFO、recovery lease、Undo 与 cleanup；
真实 app-server canary 已于 2026-08-27 按
[gated runbook](../../prototypes/Galatea/README.md#gated-real-codex-delegation-canary)
通过（1/1，16s），确认同一 thread/context、两次回信 one-shot 消费、隔离临时 repository clean
以及没有 Galatea Completion connection 调用。

本阶段仍有意只提供 process-local 状态：没有 durable outbox、跨重启 exactly-once、multi-thread/
rollover/selection，也没有自动 scheduler 或回信到达时主动唤醒 Galatea；这些不是本重构的未完成项。

2026-08-27 的闭环实测后加固已完成：`TextExtractor`只对pre-response
`TransportOutcomeUnknown`执行最多5次总尝试，使用1s/2s/4s/8s指数退避；独立30秒extraction deadline已移除，
当前完成优先语义只保留caller cancellation。
