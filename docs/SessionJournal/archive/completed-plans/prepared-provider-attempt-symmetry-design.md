# CS-3D7：Prepared / Provider Attempt 对称化

> 状态：Implemented（2026-07-27）

## 1. 问题与决策

旧协议把 `CompletionRequestPrepared` 同时解释为“canonical request 已 durable”和“首次
provider attempt 已开始”，而 retry 又另写携带 opaque identity 的
`CompletionAttemptRestarted`。这使 crash boundary、默认恢复策略和首次/重试 driver 出现两套
语义。

CS-3D7 将它收成一条地址拓扑：

```text
completion boundary
  -> CompletionRequestPrepared (P)
  -> CompletionAttemptStarted  (S1)
  -> CompletionAttemptStarted  (S2, optional retry)
  -> AgentActionProduced | CompletionAttemptFailed
```

- `P` 只声明 exact request 已 durable；provider 尚未获准调用。
- 每次调用 provider 前都先 commit 一个严格空 body 的 `S`。
- `S` 的 event address 是内部 attempt identity；`Parent` 同时表达首次派发或替换关系。
- terminal event 必须直接以最新 `S` 为 Parent。

这不是 exactly-once 协议。`S` commit 后任何 transport exception、cancellation 或 crash 都只
能保守视为 outcome uncertain；本切片不引入 provider idempotency、lookup 或 reconciliation。

## 2. Wire 与状态合同

| kind | 数值 | body schema | 语义 |
|---|---:|---:|---|
| `CompletionRequestPrepared` | 8 | v3 | `origin={correlationId,reason}`；无 attempt id |
| `CompletionAttemptFailed` | 9 | v2 | known failure；无 attempt id |
| retired | 11 | unsupported | 旧 `CompletionAttemptRestarted`，不兼容读取 |
| `ArtifactSetCommitted` | 12 | v1 | 不变 |
| `CompletionAttemptStarted` | 13 | v1 | body 必须为 `{}` |

旧实验 journal 是 breaking wire；通过离线 import/migration 重建，不以缺省字段或 root replay
猜测兼容。

执行态：

- head=`P`：`AwaitingCompletionDispatch`，
  `PendingRequestPreparedAddress=P`，`ActiveCompletionAttemptAddress=null`。
- head=`S`：`AwaitingCompletion`，
  `PendingRequestPreparedAddress=P`，`ActiveCompletionAttemptAddress=S`。
- terminal：`Idle` / `AwaitingToolExecution` / `TurnFailed`，清除 active request/attempt。

## 3. Driver 顺序

首次派发和 uncertain retry 在 preflight 之后共用同一个
`StartAndExecuteCompletionAttemptAsync` 边界：

1. fresh path 使用刚刚构造、commit 前已由 reconstructor 做 exact commitment 验证的内存 request；
   reopen/retry path 从 committed `P` 重建 exact request；
2. reopen/retry 验证 completion target、client/API、visible tools 与 durable tool runtime identity；
   fresh path 的这些 identity 来自同一个当前 runtime，并已固化进刚提交的 manifest；
3. 在写 `S` 前检查 cancellation；
4. exact-head CAS append `S`；
5. 触发 `AfterCompletionAttemptStartedCommitted` failpoint；
6. 只有 CAS winner 才能调用 provider；
7. known failure 写 `CompletionAttemptFailed`，成功写 `AgentActionProduced`。

`Open()` 保持纯读取。显式 `ResumeAsync()`：

- Prepared-only：自动完成上述 preflight，commit `S` 后派发；
- Started/uncertain：默认
  `SessionUncertainCompletionRecoveryPolicy.Refuse`，零 mutation、零 provider、无需读取 request
  range；显式 `RestartWithNewAttempt` 才重建、验证、commit 下一个 `S` 并调用。

## 4. Tail projection 不变量

`SessionReducer`、`SessionExecutionTailResolver` 与 `SessionTailContextProjection` 使用同一拓扑：

- `P` 必须直接跟随 Observation 或 dependency-closed ToolResult boundary；
- `S1.Parent=P`，后续 `Sn.Parent=S(n-1)`；
- Action/Failed 不能直接以 `P` 为 Parent，也不能绕过最新 `S`；
- resolver 只沿 operational tail 回到 `P`，不构造完整 conversation context；
- `P` 仍是 setup、ArtifactSet 和 exact request provenance 的唯一 payload source。

## 5. 验收证据

实现测试覆盖：

- 正常 `P -> S -> Action|Failed`；
- After-Prepared reopen 自动派发；
- After-Started 默认拒绝与显式 retry；
- Prepared 直接 terminal、Started 缺 Prepared、绕过最新 Started 等 malformed topology；
- runtime/target/tool mismatch 与 pre-dispatch cancellation 不写 `S`；
- transport/cancellation/after-provider-before-action 保留 uncertain `S`；
- Observation 与 ToolResult continuation 都经过 `S`；
- Prepared v2、Failed v1、retired kind 11 和非空 Started body 均 fail-fast；
- cold-prefix diagnostics、full reducer differential、tail-only 与 full projection counters。

## 6. 非目标

本切片不改变 coherent ArtifactSet、setup stream、request canonicalization recipe、tool execution
checkpoint、public `Project()` / `ReplayHistory()` 的 full semantics，也不引入 Agent.Core 依赖。
