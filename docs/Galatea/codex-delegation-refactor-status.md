# Galatea Codex 代行闭环重构状态

> 总体状态：In Progress
>
> process-local 闭环子阶段：Complete（2026-08-27）
>
> durable delegation 子阶段：In Progress（2026-08-28 启动）

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

上述结果现在被明确定义为已完成的 **process-local 闭环子阶段**。它的
coordinator ledger、fixed-thread binding、ReplyInbox 和 reply lease 均只在进程内存中；
进程退出后丢失是该子阶段已知且被接受的边界，不是其验收缺口。

新的 **durable delegation 子阶段** 以
[`codex-delegation-durability-design.md`](codex-delegation-durability-design.md)
为当前实施真源。目标是将 capture、outbox dispatch、fixed-thread binding、reply inbox
与 one-shot reply lease 收口为 Galatea-owned SQLite current-state machine，并通过
signal 加 1 秒 fallback pulse 跨进程恢复。该阶段明确不承诺 provider-call
exactly-once，也不将外部 side effect 伪装成可随 SessionJournal Undo 撤销。
每个 user database 必须由 process-lifetime exclusive OS writer lock 保护，不用
epoch/fencing 容忍多 writer；reply lease 以 cutoff 与紧邻 `SendAsync` 前的
`BindObservationBase` 两阶段持久。

在最终 hard cut 之前，所有新 durable 代码必须保持 dormant：现行产品路径继续
由已验收的 process-local owner 唯一运行，不做 live dual-write。hard cut 时才会
同步更新 `prototypes/Galatea/README.md` 中的现行产品契约；本文档包不预写
尚未激活的产品事实。所有可回退 preflight 必须在 explicit baseline 前完成；
baseline 建立后不得直接恢复旧 owner。

2026-08-27 的闭环实测后加固已完成：`TextExtractor`只对pre-response
`TransportOutcomeUnknown`执行最多5次总尝试，使用1s/2s/4s/8s指数退避；独立30秒extraction deadline已移除，
当前完成优先语义只保留caller cancellation。

2026-08-27 的内建工具配置增强已完成：delegate config升级为strict V2，
`localCommandNetwork`与`tools.{webSearch,imageGeneration,viewImage}`显式解耦。唯一开发实例采用
本地命令出网、live Web Search、Image Generation与View Image均开启；Apps/MCP继续关闭。
真实ephemeral canary在`networkAccess=false`下完成，并在app-server事件流中观察到exact一个
`webSearch` item；provider capability同时回报Web Search与Image Generation可用。
第二个临时workspace canary又观察到exact一个`imageGeneration`和一个`imageView` item，
完成后已删除生成文件与临时目录。
