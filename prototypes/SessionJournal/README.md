# SessionJournal

SessionJournal 是 provider-neutral、event-addressed 的会话 authority。raw EventJournal
events 与 selected `RefId` parent lineage 决定 durable truth；Timeline、RecapGrid 与 UI
projection 都不能反向修改 raw history。

## Public lifecycle

普通消费者通过 `SessionJournalEngine.Create/Open/OpenReadOnly` 获得 owner-bound handle。
writer operation 必须提交 exact expected raw head；Prepared、Started、ToolContinuation 与
ToolResult recovery 都由 `InspectRuntimeRecoveryRequirements` 返回 closed typed shape。

一次新请求的 durable phases 为：

1. Idle + pending observation
2. ObservationAccepted
3. CompletionRequestPrepared
4. CompletionAttemptStarted
5. ToolExecutionStarted / ToolResultObserved（若有 tool）
6. terminal AgentAction 或 TurnFailed

Started 表示外部 effect 可能已经发生，不能被自动重试伪装成 exactly-once。调用方只能
Refuse，或在明确 operator/user 决策后按 exact frozen identity 开始新 attempt。

## Context extension points

`ICoherentContextCandidateSource` 负责 pure-read selection/materialization；
`ISessionContextLifecycleCoordinator` 负责在 safe boundary 协调 readiness。核心只接收
provider-neutral contribution、exact anchor 与 closed materialization result，不认识具体
RecapGrid backend。

正式 RecapGrid online composition 位于 `SessionJournal.RecapGrid.Online`：它用
HistoryTimeline reconcile/seal、Manager build 与 Getter readiness 组成单一 lifecycle。
empty Timeline 或 no-active recipe 必须走 raw-only，不能被缺失/损坏的 Grid Store 阻断；
nonempty active 但 current fulfillment 缺失则 fail closed 或在显式 budget 下补建。

raw tail 仍由 SessionJournal core 从 candidate `EndInclusive/EndSetups` 之后 fold，candidate
不得重复包含 tail，也不得越过 anchor。selected contribution 与 raw tail 的拼接必须在
materialize 后重新校验 whole Timeline head、Control head、raw boundary 与 Store identity。

## Replay and branches

- 使用 `ReplayHistory()` / reducer 保留 EventAddress provenance；`Project()` 只适合展示。
- branch rewind/retarget 只改变 selected lineage，不删除 raw events。
- 任何 derived row、view、receipt 或 cache 都必须能由 raw lineage 与 canonical contracts
  重新验证；离开 selected lineage 的 artifact 不能通过 latest/global scan 重新获得权限。
- `OpenReadOnly`与SessionJournal core的`Create/Open`不自动创建sidecar。Galatea application可在尚未发布的
  `create-if-missing` candidate中显式组合Cadence、empty Timeline与empty Control，但这不改变core contract，也不创建Store、
  asset、recipe或activation，不补写existing repository。

## Operator surfaces

- `SessionJournal.Cli recap-grid timeline ...`：Timeline lifecycle/maintenance
- `SessionJournal.Cli recap-grid control ...`：Family/Definition/Recipe/activation
- `SessionJournal.Cli recap-grid build|progress|materialize`：explicit build/read
- `SessionJournal.Cli run-online-turn`：正式 disposable online vertical
- `SessionJournal.Cli recap-grid legacy-root ...`：旧 slot 的 inspect/archive/delete

旧 recap product 与 runtime owner 已移除；旧 on-disk slot 只由 `legacy-root` 的 exact
witness workflow 处理，normal runtime 永不扫描或 fallback 到它们。
