# Galatea Codex 代行闭环重构状态

> 状态：**Complete**
>
> 完成日期：2026-08-28

现行产品契约、配置与开发 runbook 见
[`prototypes/Galatea/README.md`](../../prototypes/Galatea/README.md)；durable authority、failure model、
crash matrix 与非目标见
[`codex-delegation-durability-design.md`](codex-delegation-durability-design.md)。本文只作为阶段完成 tombstone，
不再复制产品说明。

后续local-only V3 resilience slice见
[`codex-delegation-local-resilience-work-order.md`](codex-delegation-local-resilience-work-order.md)；它不重新打开
本文已完成的V2 hard-cut清单。

完成结果：

- Root config已hard-cut strict V3；existing state只有在matching session存在时才strict-open并持有lifetime
  writer lock，`SESSION_MISSING`在SQLite open前fail closed；missing store在第一次writable SessionJournal
  attach自动建立physical-frontier baseline。
- SQLite current-state machine已成为capture/outbox/fixed-thread/reply lease唯一durable owner；signal + 1秒
  fallback pulse可跨进程恢复。
- Staged V2 sidecar只提供`ensure-binding`、`start-turn`、`inspect-dispatch`；同一dispatch at-most-one
  start attempt，OutcomeUnknown只做read-only reconciliation，不宣称app-server/provider exactly-once。
- Normalizer在HTTP 202前完成；reply lease以SessionJournal exact raw evidence one-shot结算；capture后的普通
  Undo不撤回outbox或重新武装Consumed notice。Maintenance与shutdown边界已接入production composition。
- 后续独立browser增量已增加默认关闭的1秒mail-loop heartbeat：它只调用server conditional
  `POST /api/v1/mailbox/ready-turn`，不读取inbox正文或textarea，自动turn也不清空玩家草稿；response-loss只做
  current/recent只读对账，recovery与非预期协议fail closed。
- C#/Node V1 owner、process-local coordinator/ledger/ReplyInbox及fallback path均已删除；current binary没有回旧
  owner或operator abandon durable candidate的产品路径。

关键提交：`6aab3310`, `b95134e7`, `a09ef6f5`, `8ec6c19a`, `0c82339e`, `6ba7b4cc`,
`e3c68d23`, `fd9e36d9`, `37a53bb6`, `eaf5692a`, `0dcab030`, `c35cdd9c`, `03d20259`,
`47f0efbe`, `b99ab11a`, `76e0f566`, `10198814`, `bfdfdbc7`, `9cd3596d`, `fcb2c2f6`。

收口验证：hard-cut focused 54/54、runtime vertical 3/3、supervisor + external provision 9/9、完整
Galatea non-live set 384 pass、production build 0 warnings/0 errors；current real-canary Fact在普通run中另有
1 explicit live skip。Current Node suite为52 pass、1 independent live skip、0 fail。2026-08-28 targeted real
V2 transport canary另以1/1 PASS验证empty binding、pre-start NotFound、
exact一次start、本地duplicate tombstone及inspect Completed；该canary不构造SQLite/host vertical。
同日ignored `cyber` production smoke验证首次writable attach自动baseline、HTTP login/recent、停服释放lock、
SQLite `quick_check=ok`与cold reopen；该smoke未启动sidecar或调用LLM。

未完成清单：**无**。

以下是独立future work，不重新打开本阶段：

- Ignored唯一开发实例已指向durable entry，live baseline/cold-reopen smoke与real V2 transport canary均已通过。
  Future full-host provider vertical仍可覆盖accepted后C# host restart、双信FIFO与durable reply lease；不能把
  现有两项窄验收或旧V1 canary扩张成这些证据。
- C#直接stdio驱动`codex app-server`、multi-thread/rollover及真实email/IM transport。
