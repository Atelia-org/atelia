# SessionJournal

SessionJournal 是 provider-neutral、event-addressed 的会话 authority。raw EventJournal
events 与 selected `RefId` parent lineage 决定 durable truth；Timeline、RecapGrid 与 UI
projection 都不能反向修改 raw history。

## Public lifecycle

普通消费者通过 `SessionJournalEngine.Create/Open/OpenReadOnly` 获得 owner-bound handle。
writer operation 必须提交 exact expected raw head；Prepared（含历史 Started 尾）、ToolContinuation 与
ToolResult recovery 都由 `InspectRuntimeRecoveryRequirements` 返回 closed typed shape。

一次新请求的 durable phases 为：

1. Idle + pending observation
2. ObservationAccepted
3. CompletionRequestPrepared
4. 完整结果校验后提交 AgentActionProduced v2
5. ToolExecutionStarted / ToolResultObserved（若有 tool），整个工具批次结算后可准备后继生成
6. 无工具的 terminal AgentAction，或安全生成边界上的 TurnEnded

新执行不写 CompletionAttemptStarted / CompletionAttemptFailed；调用和退避是 Host 瞬态状态。
Prepared 与合法历史 Started 尾统一为 AwaitingCompletion，按原计划及 runtime identity 恢复。
纯生成可以重复计算（可能重复计费），不宣称远端 exactly-once；完整 Action 落盘后才执行工具。
历史 Action v1 仍要求 Started parent；新 Action v2 可接 Prepared 或合法历史 Started 尾。

`EndPendingTurn(expectedHead, reason)` 在完整工具批次已闭合的生成边界追加 TurnEnded，原因限于
Stopped / Rejected / Incomplete；保留 Observation 与已提交工具事实，不 rewind。旧 Failed 不自动
生成，只允许显式 Stopped 收口；环境/协议错误保留 pending，不能伪装业务结束。
TurnEnded 与 terminal Action 都是 closed turn，前者不伪造成功 Action。

可能已发布 ref 的写入异常使 engine 进入 reopen-required；必须 dispose/reopen 后以 selected lineage
确认提交事实，不能直接重试生成。工具仍按其独立 durable operation/sequence 合同恢复，详见
[外部效果与恢复边界](../../docs/SessionJournal/current/recovery/uncertain-external-effects.md)。
本节描述当前源码合同；集成验收与真实部署状态见
[实施方案](../../docs/Galatea/completion-auto-retry-refactor-plan.md)，不以本文宣告全部验收完成。

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

- `SessionJournal.Cli rewind-branch`：显式停服后的 raw Parent 回退，默认只读预览；
  不是 completed-turn Undo，不撤销外部副作用，详见 [CLI README](../SessionJournal.Cli/README.md#离线-branch-回退)。
- `SessionJournal.Cli recap-grid timeline ...`：Timeline lifecycle/maintenance
- `SessionJournal.Cli recap-grid control ...`：Family/Definition/Recipe/activation
- `SessionJournal.Cli recap-grid build|progress|materialize`：explicit build/read
- `SessionJournal.Cli run-online-turn`：正式 disposable online vertical
- `SessionJournal.Cli recap-grid legacy-root ...`：旧 slot 的 inspect/archive/delete

旧 recap product 与 runtime owner 已移除；旧 on-disk slot 只由 `legacy-root` 的 exact
witness workflow 处理，normal runtime 永不扫描或 fallback 到它们。
