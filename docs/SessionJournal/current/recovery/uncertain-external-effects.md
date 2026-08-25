# SessionJournal uncertain external effects safety contract

状态：Current safety contract

实现起点：`9860bc33`；append-failure reopen-required语义由本文列出的owning code/tests锁定。

基线只证明下列 current code/tests 在该 checkout 上的行为；后续 HEAD 不自动继承本文判断。
本文拥有 provider/tool external-effect recovery 的当前安全边界，不取代 raw wire、Prepared manifest、
runtime identity 或 Host domain policy 的 owning code。

## Provider Started：默认停止，显式重启是新 attempt

`CompletionAttemptStarted` 已提交而 Action/Failed 尚未提交时，provider outcome 是 uncertain。
`SessionUncertainCompletionRecoveryPolicy.Refuse` 是默认策略：`ResumeAsync` 不调用 provider，也不写 journal。
只有 Host 明确接受潜在重复 external effect 时，才可选择 `RestartWithNewAttempt`；它会在同一 frozen
Prepared request 上创建新的 attempt，而不是证明或继续旧 attempt。因此显式 restart 路径的 provider
调用语义是 **at-least-once**：provider 可能已经完成旧调用，restart 可能产生重复调用或重复计费。

当前 Core 没有 provider request/result lookup、reconciliation、capability discovery，也没有跨进程
lease/single-flight。Host 不得把 idempotency key、provider handle 或 operator 推测当成 Core 已提供的
exactly-once proof。

### 明确的 no-dispatch / pre-stream rejection：Started 后可持久化 Failed

`CompletionAttemptStarted` 是 fail-closed 的默认分界，不代表所有调用异常都永久 uncertain。provider adapter 只有在
证明以下二者之一时才可抛出窄义 `CompletionRequestRejectedException`：（a）deterministic local validation 在 credential /
network dispatch 前拒绝 request；（b）远端在任何 observer delta 前权威地明确拒绝 request。两种情况都必须证明该 request
不可能再产生 Action。Core 只 catch 这个 exact provider-neutral 类型，把其中
`CompletionTerminationKind.Failed`、稳定 `ProviderReason` 与 caller 提供的 bounded/content-free diagnostics 追加为现有
`CompletionAttemptFailed`，随后以 `SessionJournalTurnAbortedException` 结束本轮。事件 kind、body schema 与恢复 phase
均不新增：reopen 后仍是既有 `TurnFailed`，由 Host 按 exact failed-head policy 处理。

local 分支必须按 exact validation case 分类；不能 catch 普通 converter / serialization exception 后泛化为 known rejection。
这条翻译也不适用于 caller cancellation、transport failure、redirect、5xx、未验证的 4xx、2xx non-SSE、SSE
malformed/EOF/protocol failure，或已经产生任意 observer delta 的调用；它们继续停在 Started uncertain。若
`CompletionAttemptFailed` 的 append 本身抛错，Core传播原始append failure，不能声称known outcome已持久化，并立即把当前
`SessionJournalEngine` 标记为reopen-required：同一实例不再允许任何repository-bound读取、恢复或写入；dispose仍允许，纯metadata
getter与失败前已经物化、后续不再触碰repository的immutable snapshot不受此限制。因为EventJournal的Ref move
是append后再`DurableFlush`，异常后的物理head可能仍是Started，也可能已是exact `CompletionAttemptFailed`；当前实例的内存Ref
cache不能裁决该结果。Host必须dispose/reopen，让repository recovery读取物理Ref，然后按reopen后观察到的`AwaitingCompletion`
或`TurnFailed`处理。adapter也不得把raw response body/message、token、account、prompt、generated output或`InnerException`
放入该typed rejection。

## ToolExecutionStarted：durable continuation，不等于 provider policy

`ToolExecutionStarted` 与 provider Started 使用不同证明义务。它持久化 exact
`SessionToolRuntimeIdentity`、`operationId` 与 `executionSequence`；恢复时 runtime identity 必须 exact
匹配，并以同一 operation id、同一 reserved sequence 再次调用 tool。Core 不创建第二个 Started
reservation，也不把 provider 的 `UncertainCompletionRecoveryPolicy` 应用到 tool continuation。

这只提供稳定的去重/查询关联，不证明 tool side effect 恰好一次。Host 只应让以下工具进入自动恢复：

- tool 天然幂等；或
- Host/tool backend 能按 durable `operationId` 去重，或查询并返回既有结果。

非幂等且结果不可查询的工具不得进入自动恢复路径。当前 Core 尚无按 side-effect capability 自动选择
resume/pause 的策略层；`CapabilitySetFingerprint` 只绑定 Host 声明的 capability set identity，不能替代
该 admission 决策或结果证明。

## 未实现的 future target

provider/tool result lookup、reconcile、capability-aware retry，以及 durable paused/uncertain 状态都尚未
实现。`ToolExecutionUncertain`、`TurnPaused` 或相似事件/phase 不能被文档或 Host 当成 current surface。
历史设计理由见 [architecture roadmap §8.4](../../archive/studies/event-sourced-session-architecture-roadmap.md#84-future-hardeninguncertain-与-capability-aware-recovery)；
该归档文档不拥有 current status。

## Current owners 与复核入口

| Concern | Owning code | Focused evidence |
|---|---|---|
| known no-dispatch / pre-stream rejection | [`CompletionRequestRejectedException.cs`](../../../../prototypes/Completion.Abstractions/CompletionRequestRejectedException.cs)、[`SessionJournalEngine.cs`](../../../../prototypes/SessionJournal/SessionJournalEngine.cs) | [`CompletionRequestRejectedExceptionTests.cs`](../../../../tests/Completion.Tests/CompletionRequestRejectedExceptionTests.cs)、[`SessionJournalEngineTests.cs`](../../../../tests/SessionJournal.Tests/SessionJournalEngineTests.cs) |
| provider policy/default | [`SessionJournalContracts.cs`](../../../../prototypes/SessionJournal/SessionJournalContracts.cs)、[`SessionJournalEngine.cs`](../../../../prototypes/SessionJournal/SessionJournalEngine.cs) | [`SessionPreparedCompletionRecoveryEngineTests.cs`](../../../../tests/SessionJournal.Tests/SessionPreparedCompletionRecoveryEngineTests.cs) |
| recovery inspection | [`SessionRuntimeRecoveryRequirements.cs`](../../../../prototypes/SessionJournal/SessionRuntimeRecoveryRequirements.cs)、[`SessionJournalEngine.RuntimeRecovery.cs`](../../../../prototypes/SessionJournal/SessionJournalEngine.RuntimeRecovery.cs) | [`SessionRuntimeRecoveryRequirementsTests.cs`](../../../../tests/SessionJournal.Tests/SessionRuntimeRecoveryRequirementsTests.cs) |
| tool reservation/continuation | [`SessionJournalEngine.cs`](../../../../prototypes/SessionJournal/SessionJournalEngine.cs)、[`SessionExecutionTailResolver.cs`](../../../../prototypes/SessionJournal/SessionExecutionTailResolver.cs) | [`SessionJournalEngineTests.cs`](../../../../tests/SessionJournal.Tests/SessionJournalEngineTests.cs)、[`SessionExecutionTailResolverTests.cs`](../../../../tests/SessionJournal.Tests/SessionExecutionTailResolverTests.cs) |

Host 使用顺序与 exact-head binding 见 [Core README §Send 与 recovery](../../../../prototypes/SessionJournal/README.md#send-与-recovery)。
修改上述边界时，必须同时复核 owning contracts/engine、provider uncertain tests、tool continuation tests，
并明确区分“新 attempt 可能重复”与“同一 durable tool reservation 再执行”。
