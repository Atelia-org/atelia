# SessionJournal Tail-only Execution Recovery Design

> **状态**：Completion Record / Historical / CS-3D0～CS-3D7 已完成
> **文档角色**：记录 CS-3D 的 cut-time design、实施切片与当时验收；不拥有 current API、wire或
> implementation status。current 默认入口是
> [Core guide](../../../../prototypes/SessionJournal/README.md#send-与-recovery)、current code与focused tests。
> **日期**：2026-07-27
> **建议路线编号**：CS-3D
> **前置实现**：CS-3A governing setup checkpoint、CS-3B dependency-closed tail context、
> CS-3C committed request recovery
> **相关文档**：
> [Configuration Access Notes](../studies/session-configuration-access-notes.md)、
> [SessionJournal 主干设计](../superseded/session-journal-trunk-design.md)、
> [SessionJournal 架构路线图](../studies/event-sourced-session-architecture-roadmap.md)、
> [CS-3D 实施后化简调研](../studies/tail-execution-recovery-simplification-study.md)、
> [候选 D0/D1 实施计划](tail-operational-semantics-simplification-plan.md)、
> [CS-3D6 Coherent-only Manifest 化简计划](coherent-request-manifest-simplification-plan.md)
> **D7 协议修订**：[Prepared / Provider Attempt 对称化](prepared-provider-attempt-symmetry-design.md)

> **P5 supersession（2026-07-29）**：本文保留 CS-3D 的实施过程和当时 differential
> oracle 设计，不再描述 current full-audit API。P5-D 已删除 public full projection/replay、
> production reducer 及其 invocation diagnostics；current correctness coverage 由明确预期的
> durable tail matrix、bounded suffix fold、Offline checked audit 与 corruption tests 组成。
> current runtime 仍严格使用本文建立的 tail-only execution recovery。

> **Prepared v5 / DM-8 supersession（2026-07-28）**：本文下文出现的 Prepared v3、exact
> activation、raw derived-set event 或 inline artifact identity 均为历史实现记录，已由
> [Derived Memory Subsystem Implementation Plan](derived-memory-subsystem-implementation-plan.md) 的
> DM-2/DM-8 breaking wire 替代。current Prepared v5 保存
> `RawStartSetups + ExactContextInputs`：anchor setup refs
> 由 controlled writer 在 append 前通过 request reconstruction、canonical exact check、
> bound setup cursor 与 head CAS 固定。online resolver 从真实 Parent lineage 遇到 Prepared 后，会
> 重验其 setup refs 的 kind/schema/hash，但刻意不再 O(N) 回扫来证明它们是该 Prepared ancestry 上
> 最新的 setup；这是 validated-writer trust boundary，不是 ticket 自证。来自不可信 import 的
> journal 必须先通过 full offline validation，之后才可进入 online resolution。reconstructor 不依赖 raw
> derived-set definition/member/selection。DM-4 已从 raw event inventory、codec、reducer、tail resolver、
> setup resolver 与 offline validator 删除该 activation surface；没有 compatibility decoder 或
> fallback。Prepared 是唯一近头 raw setup hint；hint 之前的真实 Parent walk仍是查找路径，但不会
> 在每次 online 命中后重复完整 ancestry 证明。

> **后续架构方向（2026-07-28）**：本文保留 CS-3D0～D7 当时的历史 wire/验收事实。候选 C / DM-4
> 已删除 raw activation，把 artifact/set 的维护、存储、lineage、indexes 与 selection 移入独立可替换的
> DerivedMemory 子系统；SessionJournal 只定义 store-neutral coherent context candidate contract。
> `SessionExecutionTailResolver` 始终保持 raw-only，只有未 Prepared 的 request-context
> planning/materialization 注入该 provider；Prepared/Started exact reopen 不读取 derived subsystem。
> current 实施见
> [DerivedMemory 实施方案](derived-memory-subsystem-implementation-plan.md)；原 provisioning 问题见
> [历史缺口备忘](memory-maintainer-provisioning-planner-gap.md)。

> **历史正文解释规则**：除上面的 supersession/current replacement 说明外，正文中的“当前”、
> `Project()`、`ReplayHistory()`、`SessionReducer`、ArtifactSet activation与 DerivedMemory均指
> CS-3D cut-time snapshot。P5 已删除 public full projection/replay与production reducer；EADR V4已
> 替换旧 derived implementation。不要从正文反推 current public surface。

## 0. CS-3D cut-time 结论（Historical）

CS-3D 启动时，`SessionJournalEngine.Project()` 会从 ref root 正向解码到 ref head，并用
`SessionReducer` 同时构造完整 conversation context 与 execution state。该切片把它用作当时的
审计、迁移和 differential-test oracle，但要求 online reopen / resume退出这条路径；P5 后这些
public/production surface 已删除，current full audit属于 Offline companion。

CS-3D 当时把主线拆成两个彼此独立的投影：

1. **Execution Recovery Projection**：只回答“链头现在处于什么 phase，下一合法动作是什么，执行该动作还缺
   哪些局部依赖”。它从 head 沿 Parent 反向读取当前 operational tail，绝不物化完整 conversation。
2. **Request Context Projection**：只在确实要调用 LLM 时运行。正常长会话使用 coherent
   recap/artifact set（rolling 第一人称自传、world-understanding 等）加 dependency-closed raw
   suffix，并将最终 request 固化进 committed manifest。

显式 `Project()` / `ReplayHistory()` 在该切片完成时仍允许 O(全历史)；P5 随后删除了这些 public
surface。保留下来的 current规则是：online `Open`、`ResumeAsync`、`SendAsync`、tool-loop driver、
setup mutation与request preparation不通过full projection判断execution phase。

目标复杂度不是一个脆弱的固定窗口，而是：

```text
O(head 到最近可信 operational checkpoint 的距离 + 当前未闭合 dependency span)
```

连续自主 tool loop 不一定存在传统 user-turn 边界，因此 resolver 必须依赖 durable attempt/tool
dependencies，而不是假定“回溯到最近一条用户消息”。

## 1. 问题与目标

长寿命 Agent 的 raw SessionJournal 会远大于任何 LLM context window。为了恢复一次中断而构造
`SessionProjection.Context` 的全部历史，既浪费 IO/解码/内存，也违反 recap/artifact 层存在的目的。

### 1.1 目标

- reopen 后从 ref head 路由，无需先执行 root-to-head replay。
- 对所有 execution phase 恢复最小、确定性的 `SessionExecutionState`：
  `Idle`、`TurnFailed`、`AwaitingAgentAction`、`AwaitingCompletionDispatch`、
  `AwaitingCompletion`、
  `AwaitingToolExecution`。
- 找回当前 active attempt、correlation、pending tool call、operation id、started/result settlement 与
  tool execution sequence，不加载旧 conversation。
- 正常 live driver 的每一步只读取当前 operational tail，然后以 exact-head CAS append 下一事件。
- LLM request context 由 artifact set + raw suffix 提供；execution resolver 不读取
  `DerivedRecapStore`。
- 保持 raw event Parent chain 是唯一正确性来源；derived artifacts/indexes 仍可删除、可重建。
- 保留 full reducer 作为参考实现，对同一合法链验证：

  ```text
  TailExecutionResolver(head).State == Project(head).ExecutionState
  ```

  这里只比较 execution state，不要求 tail resolver 构造完整 `Context`。

### 1.2 非目标

- 不把 `Project()` 本身改造成有损或只返回 tail 的 API。
- 不缓存完整 `SessionProjection.Context`。
- 不用固定 event 数/turn 数截断代替 dependency closure。
- 不从 `latest` index 隐式挑 artifact；Context Planner 必须提交 exact selection。
- 不为 D6D 前的 `full-raw` / `explicit-artifact-tail` Prepared wire 保留 compatibility reader；
  旧实验 journal 通过离线重建迁移，不由 current codec 猜测。
- 不在本切片解决 provider-side idempotency/result lookup 或非幂等工具的完整补偿策略。

## 2. 三类投影的 cut-time 分离（Historical）

| 投影 | 回答的问题 | 输入 | 输出 | 在线复杂度 |
| --- | --- | --- | --- | --- |
| Full Audit Projection | 历史完整发生了什么 | root…head raw chain | 完整 context + execution state | O(全历史) |
| Tail Execution Projection | 现在应执行什么 | head 附近 operational tail + raw checkpoints | 最小 execution state | O(局部依赖) |
| Request Context Projection | 下一次 LLM 看见什么 | exact artifact set + dependency-closed suffix + setup | bounded canonical request | O(artifact + suffix) |

在 CS-3D cut-time，`SessionReducer` 是第一行的 differential oracle，
`SessionExecutionTailResolver` 属于第二行，`SessionTailContextProjection`及当时后续 Context
Planner属于第三行。P5以后第一行由 Offline checked audit与明确预期的corruption/acceptance
matrix承担，不再存在public/production full reducer。

这三者不能再通过“先构造完整 `SessionProjection`，然后只取其中一个字段”耦合。

## 3. CS-3D cut-time 实现边界（Historical）

关键文件：

- `prototypes/SessionJournal/SessionJournalEngine.cs`
  - `Project()` / `ReplayHistory()`：完整 `ReadChronologicalChain`。
  - CS-3D3 后 `ResumeAsync()`、`SendAsync()`、setup/import boundary 与 tool-loop transition
    全部由 `SessionExecutionTailResolver` 路由；Action/Started/Result append 后按返回的 exact address
    重新 resolve。
  - D7 后 online writer 已收为 coherent recipe；DM-8 的 current Prepared v5
    codec/reconstructor 继续只接受该单一路径，并允许 strict empty-lineage bootstrap 的零 inputs，
    artifact-tail recipe，不调用 `Project()` 物化 request Context。D6D 前的 full-raw /
    explicit reader 只属于历史，不存在于 current runtime。
- `prototypes/SessionJournal/SessionReducer.cs`
  - 同时累计 config、system prompt、完整 context 与 execution state。
  - full reducer 已消费 D1 的 durable execution checkpoint；它不再通过从 root 计数
    `ToolResultObserved` 推断 sequence，但仍会为审计语义解码完整历史。
- `prototypes/SessionJournal/SessionTailContextProjection.cs`
  - 已能从 coherent artifact-set common anchor fold dependency-closed Observation / settled
    ToolResult suffix，并把 visible tool snapshot 纳入 prepared request。
  - 这是 request context projector，不是通用 execution reducer。
- `prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs`
  - 只从 committed Prepared v3 的 exact activation、inline artifact contributions、setup refs、
    dependency-closed suffix 与 tool snapshot 重建 canonical request。
  - activation `coverageSetups` 是 suffix fold seed；fold 后的 governing setup 必须与 Prepared exact
    refs 一致，不能让 Prepared 自证自己的 setup。

已有 fast path 应复用，不应另造第二套 attempt/setup 解析：

- `ResolveGoverningSetup`。
- `SessionExecutionTailResolver` 内的 Prepared/Started address chain、live/imported terminal 与 setup-run
  validators。
- manifest 中的 setup refs、origin correlation 与 exact canonical commitment；attempt identity 是
  `CompletionAttemptStarted` event address。

## 4. 正确性与信任边界

### 4.1 Raw fact 与 derived data

- EventJournal raw events 是 execution correctness 的唯一真源。
- recap/artifact 只决定 LLM context，不决定 pending tool 或 active attempt。
- execution cache 可以存在，但必须可删除、可由 raw tail 重建；本阶段不以 cache 作为前置条件。

### 4.2 Validated-writer trust

正常 live path 信任更早 prefix 已经由 SessionJournal 受控 writer 验证。tail resolver 对当前 Parent
lineage、checkpoint 引用和局部 dependencies 做严格验证，但不为每次 reopen 重做全历史验真。

不可信 raw import 必须先走离线 full validation/migration，不能因为线上 resolver 只读 tail 就把任意伪造
prefix 当成合法事实。

### 4.3 Branch 与 rewind

所有 checkpoint 只能在当前 head 的 Parent lineage 上命中。不得按物理地址大小、时间戳或全局 latest
index 复用另一分支的状态。找不到 checkpoint 时：

- bootstrap/legacy 的显式迁移或审计命令可以 full replay；
- 正常在线路径应 fail-fast 或要求生成 checkpoint，不能静默退化成 O(全历史)。

### 4.4 Bounded 不等于固定长度

一个 completion 可能声明多个 tool calls，自主 Agent 也可能持续多轮 tool continuation。resolver
必须读到当前 dependencies 闭合所需的真实边界；不能为了保证固定 N 条而截断合法状态。

每次 committed Prepared 是天然的近头 operational checkpoint。若未来单次 dependency span 也可能极长，
再增加显式 execution checkpoint，而不是恢复 root replay。

## 5. 目标恢复流程

```text
Open(repo)
  -> read main ref head
  -> ResolveExecutionTail(head)              // no Context, no artifact
  -> switch state.Phase
       Idle:
         wait for observation or setup mutation

       TurnFailed at exact CompletionAttemptFailed head:
         expose FailedTurnMustBeAbandoned(FailedHead)
         exact AbandonFailedTurn, then reinspect Idle

       AwaitingCompletionDispatch:
         reconstruct and validate committed request
         append CompletionAttemptStarted, then dispatch

       AwaitingCompletion:
         resolve Prepared/Started address chain
         apply Refuse / explicit restart policy
         reconstruct only committed request

       AwaitingToolExecution:
         verify pinned tool runtime identity
         reconcile or execute the one pending operation
         append ToolResult with expected-head CAS
         ResolveExecutionTail(newHead)

       AwaitingAgentAction:
         ContextPlanner selects exact coherent artifact set + raw boundary
         materialize artifact snapshot + dependency-closed suffix
         commit Prepared manifest
         call provider
```

pre-Beta采用direct cut：旧`TurnFailed + setup/Observation suffix`不做兼容回扫，runtime requirements
inspection与fresh Send均fail-closed；这类tail必须显式迁移或重建。

这里 `AwaitingAgentAction` 既可能来自新 Observation，也可能来自已结算的 ToolResult。两者共用 request
materialization contract；不能让 tool continuation 回落到 full `Project()`。

## 6. Tail Execution Projection 合同

### 6.1 输出形状

最终命名可在实现切片中调整，但职责应接近：

```csharp
internal sealed record SessionExecutionRecovery(
    EventAddress Head,
    SessionExecutionState State,
    SessionExecutionRecoveryBoundary Boundary,
    SessionExecutionRecoveryDiagnostics Diagnostics
);

internal sealed record SessionExecutionRecoveryBoundary(
    EventAddress? SourcePrepared,
    EventAddress? SourceAction,
    EventAddress? SourceObservation,
    EventAddress? LatestExecutionCheckpoint
);
```

不要在该对象中加入 `IReadOnlyList<IHistoryMessage> Context`、artifact 文本或完整 setup 值。需要 setup
时单独调用 `ResolveGoverningSetup(head)`。

### 6.2 按链头路由

| Head kind | 最小读取与输出 |
| --- | --- |
| `RuntimeConfigSetup` / `SystemPromptSetup` | 跳过连续 setup run，验证前驱是 idle/failed terminal；phase 保持静止 |
| `SessionCreated` | 验证三事件 bootstrap；`Idle`，tool sequence = 0 |
| `ObservationAccepted` | direct parent 必须是合法 idle boundary；`AwaitingAgentAction`，correlation 由 observation address 派生 |
| `CompletionRequestPrepared` | 验证 source boundary；`AwaitingCompletionDispatch`，active attempt 为空 |
| `CompletionAttemptStarted` | 沿连续 Started Parent 回到 source Prepared；`AwaitingCompletion`，head address 即 active attempt |
| `CompletionAttemptFailed` | 验证 direct parent 是最新 Started；`TurnFailed` |
| terminal `AgentActionProduced` / `ImportedAgentAction` | 验证来源与无未结算 calls；`Idle` |
| Action 含 tool calls | 保存 action 与声明顺序；首个 call pending |
| `ToolExecutionStarted` | 回溯到当前 Action，join call id/name/args；恢复同一 operation 与 reserved sequence |
| `ToolResultObserved` | 只收集当前 Action 之后的 starts/results；按 Action 声明顺序决定下一个 pending call，全部结算则 `AwaitingAgentAction` |

Prepared/Started 控制事件不进入 conversation context，但它们是 execution recovery 的权威
checkpoint。Action 的多个 tool results 必须继续按声明顺序 join，而不是按 append 顺序推断。

### 6.3 Imported path

live Action 必须从 active Started 产生；沿 Started Parent 找到 source Prepared 后，可从 manifest
取得 correlation 与 checkpoint。
manual/legacy `ImportedAgentAction` 没有 manifest，因此新 schema 应携带最小 execution checkpoint，或在
导入完成时追加一个明确 checkpoint。

不要为少数 legacy path 让所有正常 reopen 回退到 root。旧 import repo 应通过一次性迁移补齐，而不是永久
拖累 live protocol。

## 7. Tool execution sequence 必须先变成近头 durable fact

CS-3D1 前，`ToolExecutionSequenceCheckpoint` 从 root 累计 `ToolResultObserved`，是通用 tail
recovery 的主要长程阻塞点，而且“执行后才从结果计数”不能精确表达已 durable-started 但尚未
observed-result 的调用。CS-3D1 已按下述协议消除这项长程状态。

已实施协议：

1. `CompletionRequestPrepared` 增加最小 `ExecutionCheckpoint`，至少保存
   `LastIssuedToolExecutionSequence`。
2. `AgentActionProduced` / `ImportedAgentAction` 统一携带 checkpoint；前者必须与 source Prepared
   相等，后者必须与 append 前 reducer state 相等。
3. 开始工具前先计算并 durable reserve `nextSequence = lastIssued + 1`。
4. `tool-execution-started` 同时保存 `operationId` 与 `executionSequence`。
5. 工具执行获得的 `ToolExecutionContext.ExecutionSequence` 必须等于已落盘的 reserved sequence；
   不得在外部调用开始后再随机分配。
6. `tool-result-observed` 重复保存并校验同一个 sequence。
7. reopen 于 Started 时，reconcile/retry 使用同一 operation id 与 sequence；reopen 于 Result 后，下一次
   从该 sequence + 1 开始。

`ToolSession.ExecuteReservedAsync(call, reservedSequence, ct)` 是 durable host 的唯一正式入口：
宿主先提交 reservation，再把确切 sequence 交给 dispatcher。它允许等于当前 sequence，以支持 uncertain
operation 用同一个 reservation 重试；拒绝小于当前 sequence；允许新进程从 0 直接采用较大的 durable
checkpoint。旧 `AuthoritativeExecutionSequenceAllocator` 已删除，`Agent.Core` 作为
`Completion.Tools` 的并列消费者也已迁移；SessionJournal 不依赖 Agent.Core。

Prepared 每次 LLM 调用前都会出现，因此即使很多轮没有工具，最近 checkpoint 仍贴近 head；活跃工具段内
Started/Result 自身继续推进 checkpoint。

CS-3D1 的 wire 形状为：

```text
CompletionRequestPrepared.execution
  { lastIssuedToolExecutionSequence }

AgentActionProduced / ImportedAgentAction
  correlationId
  execution { lastIssuedToolExecutionSequence }
  toolRuntimeIdentity | null

ToolExecutionStarted
  operationId
  executionSequence
  toolRuntimeIdentity

ToolResultObserved
  executionSequence
```

`toolRuntimeIdentity` 是非 secret、set-level 的
`{ hostId, implementationSetFingerprint, capabilitySetFingerprint }`。tool definitions 仍描述模型
看见的接口；runtime identity 额外固定真正执行这些接口的实现集合与副作用 capability policy。非空 tool
set 必须有 identity，空 tool set 必须在 manifest 中写 `null`。Action 有 tool calls 时继承 Prepared
identity（import 则取当前显式 runtime identity），Started 再重复固定实际 dispatch identity。reducer 和
driver 都要求三者精确相等。

CS-3D2 进一步发现：Action 还必须固定 non-empty `correlationId`。live Action 从 source Prepared
精确继承；import Action 从当前 Observation/settled ToolResult completion boundary 继承。terminal
Action 落盘时同样保存 correlation，只在派生 execution state 进入 Idle 后清空。否则连续
`ImportedAgentAction -> Started -> Result -> ImportedAgentAction` 会迫使 reopen 一直追到最初
Observation，Action 就不能充当 bounded operational checkpoint。

这是原型期的 breaking wire upgrade：codec 对新增字段采用 exact decode，不为旧 Action/Started/Result
body 推断默认 checkpoint 或 runtime identity。已有实验 journal 必须重建或离线迁移；在线恢复路径不保留
兼容分支，也不会为缺字段的旧历史回退到 root 猜测。

checkpoint 在 Started 提交时推进，而不是等 Result：所以 Started 后崩溃、外部执行后但 Result 前崩溃，
都恢复同一 `operationId + executionSequence`。Result 只重复并确认该 sequence；下一次 Started 必须严格
等于 checkpoint + 1。

显式 artifact-tail request preparation 目前用 `ResolveExecutionCheckpoint` 沿 Parent 做 header-first
回溯，只解码最近的 Prepared/Action/Started/Result checkpoint；bootstrap 才走到 SessionCreated=0。
这只是 D1 给现有 request path 的近头读取桥梁。通用 reopen 仍待 D2 用
`SessionExecutionTailResolver` 替换 `Project()`，不要误报 D1 已完成 online tail-only driver。

## 8. Request Context：recap/artifact 是正常路径，不是异常优化

### 8.1 正常长会话输入

下一次 completion 的 provider-facing context 应由：

```text
coherent active artifact set
  - rolling 第一人称自传
  - world-understanding
  - 后续其他被 Context Planner 选中的 memory blocks
+ dependency-closed raw suffix
+ governing system prompt / runtime config
```

构成。ArtifactSet 必须固定每个成员 artifact id、共同适用的 raw boundary/lineage 与 renderer
identity。v1 宜要求每个被激活 block 都明确覆盖到同一个 `RawStartExclusive`，避免较新的某个 block
与较旧 suffix 发生隐式重复。不能假定两个 maintainer 各自的“latest”天然组成 coherent snapshot。

这曾是 D4 前的 gap；当前 runtime 已由 raw `ArtifactSetCommitted` 接受多个 exact members，并按
canonical role/target 聚合。未来 Context Planner 仍负责预算化选择，但不再需要发明第二种 request
shape。

### 8.2 Observation 与 tool continuation 统一

历史 explicit tail path 只承诺：

```text
ObservationAccepted + no visible tools
```

当前 coherent v2 已把 request boundary 泛化为 dependency-closed completion boundary：

- 新 observation；
- 当前 Action 的全部 tool calls 已有 ToolResult；
- 其他未来被 planner 明确定义为可请求 completion 的边界。

`SessionTailContextProjection.FoldSuffix` 已有局部 tool join 能力，可作为基础；但 planner、manifest
reason/correlation、tool definitions 与 tool implementation identity 必须一并固定。

### 8.3 Prepared 后恢复

Prepared 提交前可以重新选择 artifact set；Prepared 提交后禁止重跑 planner。manifest 必须继续内联
足以 exact 重建 canonical request 的有界 materialized snapshot，因此 derived artifact 删除不会破坏
在途 request。

D6D 前 `full-raw` manifest 的 O(全历史) 成本是历史背景；current codec 不再读取该 wire，也不会
偷偷改用新 recap 猜测旧请求。旧实验 journal 必须离线重建。

### 8.4 Bootstrap 与 artifact 不可用

online bootstrap 必须先由上层 provisioning 在 replay-safe anchor 产生所需 derived artifacts 并
提交 coherent `ArtifactSetCommitted`。没有可用 coherent set 时不得提交 Observation 或把 root replay
当成兜底；应先触发/等待 maintainer 重建，或把 session 置为可诊断的 not-ready/paused 状态。旧
`full-raw` / `explicit-artifact-tail` 只存在于 D6D 前历史文档，current reader 已删除。

Prepared 提交前 artifact 缺失属于 planning/liveness 问题；Prepared 提交后则由 manifest 内联 snapshot
保证恢复，不再依赖 sidecar 是否存在。

## 9. Tool runtime identity 与副作用安全

tail-only 只能解决“读多少历史”，不能自动解决外部副作用：

- CS-3D1 已让 manifest 同时固定 tool definitions 与 tool implementation/capability runtime identity。
- reopen 后准备执行 raw Action 中的 tool call 前，必须验证当前 host 的实现/版本/capability 与 durable
  identity 相符。
- 幂等、可查询、不可查询非幂等工具仍分别需要 retry/reconcile/pause 策略。

因此 `SessionExecutionTailResolver` 只恢复 pending operation，不自行执行工具。执行 driver 必须在
runtime identity 和 capability policy 通过后才能产生外部调用。

## 10. 实施切片

### CS-3D0：合同、观测与 reference oracle

> **状态**：已实施

目标：先锁定“在线路径不得 full replay”的可执行边界，不改 event schema。

- 为 raw header/payload reads 与 `Project()` 调用提供测试 diagnostics。
- 建立每种 head phase 的 full reducer oracle fixtures。
- 增加长冷历史前缀，证明当前实现哪些路径仍退化。
- 冻结 `SessionExecutionState` 的必要字段，避免 resolver 被迫返回 Context。

验收：

- phase matrix 覆盖 Observation、Prepared/CompletionAttemptStarted、Failure、Action、ToolStarted、Result、
  Idle、setup run。
- 测试明确区分 audit full replay 与 online recovery reads。

历史实际落点（CS-3D5/D6；DM-3B 已删除 writer/command/online activation route，
DM-4 再删除剩余 kind-12 raw reader/validator）：

- 新增 `SessionJournalEventReader`，统一承接 Engine、Prepared reconstructor 与 tail context projector
  的逻辑读取；累计 header preview、payload read、chronological-chain 调用及返回 event 数。
- `SessionJournalEngine.CaptureReadDiagnostics()` 将上述累计值与
  `FullProjectionInvocationCount` 合并成可做 snapshot delta 的
  `SessionJournalReadDiagnostics`。它计量 SessionJournal 发起的逻辑 API reads，不冒充底层物理 IO、
  page cache 或解压字节统计。
- 新增 full reducer reference-oracle matrix，冻结 Empty、Idle、AwaitingAgentAction、
  AwaitingCompletionDispatch、AwaitingCompletion、AwaitingToolExecution、TurnFailed 各 phase 的必要字段；后续 D2 differential
  tests 应复用同一语义合同。
- D0 当时的冷前缀 baseline 证明：`T` 个已闭合 imported turns 的 Idle `ResumeAsync` 曾读取
  `3 + 2T` 个 payload、返回同样数量的 chronological events，并调用一次 full `Project()`；D7 后
  Started `Refuse` 在不同前缀长度下始终只读局部 Started/Prepared/source proof，不重建 request、不调用
  chronological chain / `Project()`。CS-3D3 已把 Idle 基线翻转为 1 turn 与 10001 turns 相同的
  2 header + 2 payload、0 chronological chain、0 `Project()`。

相关文件：

- `prototypes/SessionJournal/SessionJournalEventReader.cs`
- `prototypes/SessionJournal/SessionJournalContracts.cs`
- `prototypes/SessionJournal/SessionJournalEngine.cs`
- `tests/SessionJournal.Tests/SessionExecutionRecoveryContractTests.cs`

### CS-3D1：Durable execution checkpoint 与 reserved tool sequence

> **状态**：已实施

目标：移除 execution state 中唯一必须从 root 累计的字段。

- 给 Prepared/import checkpoint 增加 last-issued tool sequence。
- 给 ToolStarted/ToolResult 增加并校验 reserved execution sequence。
- 调整 `ToolSession`，让已 durable reserve 的 sequence 真正进入 `ToolExecutionContext`。
- 在 Prepared 固定 visible tool implementation/capability identity，并在 ToolStarted 再固定本次实际
  dispatch identity；恢复执行前必须与当前 host 精确匹配。
- 更新 canonical codec/goldens、failpoint 与 reopen tests。

验收：

- Started 前、Started 后、外部执行后、Result 后崩溃都恢复同一 sequence/operation。
- 后续工具严格单调递增，无 root scan。

实现补充：

- failpoint 覆盖 `AfterActionCommitted`、`AfterToolStartedCommitted`、
  `AfterToolExecutionBeforeResultCommitted`、`AfterToolResultCommitted`；
- canonical manifest/action/start/result codec 均严格校验新字段；
- full reducer 仍作为 reference oracle，但 checkpoint 已改为读取 durable fact，不再按 Result 数量计数；
- `SessionTailContextProjection` 的 suffix fold 同步校验 checkpoint、reserved sequence 与 runtime identity；
- runtime identity 不匹配时，在 provider/tool dispatch 或新 Started 写入之前 fail-fast。

关键文件：

- `src/Completion.Tools/ToolSession.cs`
- `src/Completion.Tools/ToolDispatch.cs`
- `prototypes/SessionJournal/SessionJournalContracts.cs`
- `prototypes/SessionJournal/SessionRequestManifestCodec.cs`
- `prototypes/SessionJournal/SessionEventCodec.cs`
- `prototypes/SessionJournal/SessionReducer.cs`
- `prototypes/SessionJournal/SessionJournalEngine.cs`
- `tests/Completion.Tests/Tools/ToolSessionTests.cs`
- `tests/SessionJournal.Tests/SessionJournalEngineTests.cs`
- `tests/SessionJournal.Tests/SessionExecutionRecoveryContractTests.cs`

### CS-3D2：`SessionExecutionTailResolver`

> **状态**：已实施

目标：实现独立、纯读取、不构造 Context 的 tail execution projection。

- 按 §6 的 head-kind DFA 实现反向 dependency collection。
- 复用 setup、Prepared/CompletionAttemptStarted identity 与 tail terminal validators。
- 对受控 writer 产生的合法 fixtures，与 full reducer 的 `ExecutionState` 做 differential tests。
- 对分支、rewind、错 Parent、错 attempt、乱序 tool result、重复 call id fail-fast。

验收：

- 正常链上 state 与 full reducer oracle 一致。
- reads 只覆盖 operational tail/checkpoint。

实际落点：

- 新增 `SessionExecutionTailResolver.Resolve(reader, exactHead)`；`head = null` 返回 Empty，其他路径只使用
  `ReadEventHeaderPreview` / `ReadEvent` 沿 exact Parent lineage 读取，不调用
  `ReadChronologicalChain`、`Project()`、artifact store，也不产生 history message。
- `SessionExecutionRecovery` 返回最小 `SessionExecutionState`、source
  Prepared/Action/Observation/latest-checkpoint boundary 与本次 header/payload logical read diagnostics。
  Engine 暴露 current-head 与 exact-head internal 入口，供 CS-3D3 driver 切换；本切片没有把
  `ResumeAsync` / tool loop 的 phase routing 全部迁过去。
- setup/bootstrap、Observation、Prepared/CompletionAttemptStarted/Failure、live/import Action、
  ToolStarted/Result 分别按 §6 DFA
  收集依赖。tool tail 回溯到当前 Action 后反转，并按 Action 声明顺序正向验证
  `Started -> Result`、call id/name/raw args、runtime identity 与 reserved sequence。
- 连续 setup run 的 Parent 导航仍先读 header，但 run 内每个 setup payload 都 exact decode；因此损坏的
  near-head setup 不会被仅凭 kind 误判为 Idle。新 Observation 会开启新的 boundary provenance：
  清空上一轮 source Prepared/Action，只保留本 Observation 与继承的 latest checkpoint。
- Prepared/CompletionAttemptStarted identity-chain resolver 从 Engine 抽到 resolver 成为共享单一路径；
  Engine 既有 recent-idle validator 也委托 tail resolver，不再维护第二套
  bootstrap/terminal attempt topology。
- validated-writer trust cut 明确停在最近 Prepared/Action durable checkpoint：Prepared 验 direct
  source kind/reason/correlation/checkpoint，但不递归重验更旧 autonomous loop；Imported-after-Result
  最多回收前一 Action 的完整 tool span，证明 Result 已 fully settled 后即停。
- Action wire 新增 required non-empty `correlationId`。这是又一次 prototype breaking wire
  redefinition：codec exact decode，不从旧 Action body 或遥远 Observation 猜值；旧实验 journal 必须
  再次 import/migrate。
- differential tests 覆盖全部 head phase、多 tool、受控 Engine writer、exact old head、真实 main-ref
  rewind/divergent branch、失败后的新 Observation；malformed tests 覆盖 setup payload、
  Parent/attempt/correlation/checkpoint/runtime identity、result-before-start、声明顺序和 duplicate
  call id。
- 冷前缀 1 turn 与 32 turns 下，terminal imported Action 与新 Prepared 的 resolver read 数相同；
  imported-after-settled-Result 也固定只回收前一 Action tool span；chronological-chain/full-projection
  计数均为 0。

关键文件：

- `prototypes/SessionJournal/SessionExecutionTailResolver.cs`
- `prototypes/SessionJournal/SessionJournalContracts.cs`
- `prototypes/SessionJournal/SessionEventCodec.cs`
- `prototypes/SessionJournal/SessionReducer.cs`
- `prototypes/SessionJournal/SessionJournalEngine.cs`
- `tests/SessionJournal.Tests/SessionExecutionTailResolverTests.cs`

### CS-3D3：Engine driver 切换

> **状态**：已实施

目标：在线 execution routing / transition 彻底脱离 `Project()`；request-context materialization
继续服从已提交或即将提交的显式 selection policy。

- `ResumeAsync()` 所有 phase 改用 tail resolver。
- `ContinueToolLoopAsync()` 每次 append 后增量重解新 tail，不再 `Project()`。
- `SendAsync()`、setup mutation、import append 的 boundary validation 改用 recovery state。
- pending tool 只有在 D1 的 durable implementation/capability identity 验证通过后才能执行。
- 保留 public `Project()` / `ReplayHistory()` 的 full semantics。

验收：

- routing-only phase 的 `FullProjectionInvocationCount` 保持不变；artifact-tail Observation
  completion 仍为 0。
- online provider request 的 `FullProjectionInvocationCount` 保持为 0；public audit projection 仍按
  调用显式计数。
- 10k+ 冷历史前缀不增加 execution recovery payload reads；D6D 前 historical full-raw
  Prepared reconstruction 仍可按其只读合同读取全 raw。
- exact-head CAS/failpoint 行为与原实现一致。

实际落点：

- `SendAsync()`、`ResumeAsync()`、setup mutation 与 imported Action append 统一从 current-head
  `ResolveExecutionTail()` 获取 phase/state；mutation 均以 `recovery.Head` 作为
  `AppendExpected` parent。
- `CompleteAwaitingAgentActionAsync(recovery)` 是唯一 completion routing 入口，并直接进入 coherent
  artifact-tail；Observation 与 dependency-closed ToolResult continuation 均不调用 `Project()`。
  D6C1 已删除 live full-raw routing 与 `MaterializeFullRawRequestContext()`。
- completion helper 返回 committed `ActionAddress`；普通 completion 与 Prepared restart 都从该
  exact address 重新 resolve。若 Action 含 tools，同一次 driver 调用继续进入 tool loop。
- tool Started/Result 分别以旧 recovery head / exact Started address 做 CAS append；每次 append 后
  只 resolve 返回的新 address。runtime implementation/capability identity 在新 Started 和外部调用前
  验证；reopen 已处于 Started 时还会先确认 main head 未漂移。durable `operationId` 与 reserved
  sequence 一并传入 `ToolExecutionContext`，因此首次执行和 Started reopen retry 对工具可见的是同一
  operation identity。
- provider response 是否允许含 tool calls 由 durable tool set 是否非空决定。空 tool set 的
  initial/retry attempt response 若收到 tool call，会提交
  `atelia.host.unsupported-tool-call` known failure，不留下可被再次 restart 的伪 uncertain Prepared。
- Prepared dispatch/retry 的合法 tool-call response 会在同一次 `ResumeAsync()` 中完成
  Action -> ToolStarted -> external tool -> Result -> continuation completion，并继续使用 manifest
  固定的 runtime identity。
- failpoint 仍紧贴 Observation、Prepared/CompletionAttemptStarted、Action、ToolStarted、
  external-before-Result、Result 的原 crash window。Result CAS 若在外部副作用后失败，durable
  ToolStarted 仍保留相同
  operation id + reserved sequence 供 reopen。
- 删除 Engine 中旧 `ReadEventKind`、tail boundary/checkpoint walkers 与重复 Prepared routing
  bypass；public `Project()` / `ReplayHistory()` 保持完整审计语义。
- 复杂度测试覆盖 1 与 10001 个已闭合 imported turns：Idle `ResumeAsync` 均为 2 header +
  2 payload、0 chronological、0 full projection。D6D 后 request-context performance fixture 已
  迁为 coherent v2，不再保留 full-raw provider-path oracle。

已知边界：

- 对尚未 Started 的 pending call，Started CAS 是外部执行前的 durable claim。
- 对已经停在 Started 的两个并发 driver，调用前 head check 只能拒绝已知 stale driver，不能消除
  check 与外部副作用之间的 TOCTOU，也不提供 exactly-once。跨进程 lease、tool idempotency/result
  lookup 与 reconcile/pause 仍属于后续 capability；`operationId` 只是让工具拥有实现这些策略的稳定
  identity，并不自动提供策略本身。

### CS-3D4：Artifact-tail completion 泛化

> **状态**：已实施
> **历史说明**：本节保留 D4 当时的增量演进记录；其中 explicit reader/进程内 selection 已由 D6D
> current wire 删除，当前合同以本文件 §3 与 D6 计划完成记录为准。

目标：恢复到 `AwaitingAgentAction` 后，Observation 和 tool continuation 都不再借 full context 发请求。

- 新请求使用独立的 `coherent-artifact-tail` policy；旧
  `explicit-artifact-tail.v1` 只保留已 committed 单 artifact request 的 exact reopen 语义，不能在
  原 fingerprint 下悄悄改变 renderer。
- 上层 caller 选择至少两个 distinct exact artifact id，不读取 `latest`；这组 ids 是本切片的
  membership assertion。SessionJournal 验证 coverage coherence，不在 generic persistence 层硬编码
  roleplay autobiography/world-understanding 的 block key。每个 artifact **只贡献其
  `Target` block**；按 `System -> Observation -> Action`、同 carrier 内 `BlockKey` ordinal 组成一个
  aggregate snapshot。不得拼接每个 artifact 携带的完整 working `MemoryPack`，否则两个 maintainer
  会重复或混入彼此的旧 block。
- coherent set v1 要求每个成员 produced/exact-id 可验证、`AnchorRawEvent == SourceEndInclusive`、
  共享 common anchor、target 不重复、各 `SourceRawHead` 都位于
  `current boundary -> common anchor` 的真实 Parent lineage，且成员记录的 governing setup refs 与
  anchor-as-of setup 一致。不同 maintainer 可以有不同 `SourceRawHead`，不强求伪造同时生成。
- `RawStartExclusive` 固定为 common anchor；suffix 可以结束在新 Observation，或经 tail execution
  resolver 证明已 dependency-closed 的 `ToolResultObserved`。后一种边界的 reason、correlation 与
  last-issued tool sequence 必须与 exact recovery state 一致。
- visible tool definitions 及 D1 固定的 implementation/capability runtime identity 一并进入
  manifest。suffix fold 还要验证 Action 继承其 source Prepared 的 runtime identity，Started 再继承
  Action identity；不能只凭最终 head kind 判断 tool dependency 已闭合。
- manifest 的每个 artifact input 内联 singleton target contribution snapshot。reconstructor 按
  manifest 顺序聚合这些 contribution，再展开一次；因此 committed manifest 继续支持所有 sidecar
  artifact 删除后的 exact reopen，且不重新运行 planner。
- Prepared 前任一 exact member 缺失/不 coherent 时 fail-fast，不 append Prepared、不调用 provider；
  调用方可以恢复 sidecar 或显式提供另一组 exact selection 后重试，不能静默退回 full raw。

验收：

- 长历史上的 observation completion 与多轮 tool continuation 均不调用 `Project()`。
- 两种 maintainer artifact 的 lineage/coherence 可审计。
- sidecar 在 Prepared 后删除仍可恢复；Prepared 前缺失则 fail-fast/重新规划。

实际落点：

- D4 初版曾由 `SessionTailProjectionOptions` 冻结至少两个 distinct exact artifact ids；runtime 不
  读取 `latest`。CS-3D5 随后删除这一进程内输入，改由 raw activation 提供 exact ids。manifest 继续
  使用 coherent artifact-tail 的独立 identities；D6D 随后删除了
  `explicit-artifact-tail.v1` reconstructor 与这些冗余 policy 字段。
- `SessionTailContextProjection` exact load 全体成员，验证 common anchor、每个 source head 的当前
  Parent-lineage membership、anchor-as-of setup refs、replay-safe boundary 与 duplicate target。
  每个成员经 singleton `MemoryPack.Render()` 只产生 target contribution；按
  carrier + block key 排序后聚合，其他 working-pack blocks 不进入 request。
- projector 以 exact anchor recovery 为 execution seed，fold suffix 时校验
  setup/bootstrap/Failure/Prepared/CompletionAttemptStarted/Imported/Produced
  Action/ToolStarted/Result 的 suffix-local
  phase、Parent、attempt、reason/correlation/checkpoint 与 runtime identity；最终 fold phase 再与
  exact current recovery 对照。这样较早的 malformed Failure/Setup/ImportedAction 不会被后续合法
  near-tail checkpoint 掩盖。
- Engine 的 artifact-tail completion 同时接受 Observation 与 fully-settled ToolResult；visible
  definitions 和非空 runtime identity 固定进 manifest。工具返回后重新 resolve exact Result head，
  继续走同一 artifact-tail path，不调用 `Project()`。
- coherent reconstructor 只消费 manifest inline target contributions、exact raw suffix、setup refs
  与 tool snapshot。测试在 Prepared 后删除全部 selected sidecar files，仍能 restart 并得到相同
  canonical request；Prepared 前缺 member 则在 append/provider 前失败。
- 工具验收覆盖连续两轮 tool call、三次 provider request；每次 request 都携带相同 visible tool
  snapshot，且 `FullProjectionInvocationCount` 不变。D4 当时的 legacy v1 fixture 后来迁到
  coherent v2/current-wire oracle。

已知 provenance 边界：manifest durable 保存 exact artifact ids/kinds、common anchor、每个 singleton
contribution 及其 hash，足以审计 selection 与重建 exact request；profile、target、
`previousArtifact` / `inputArtifacts` 的完整 lineage 细节仍由 exact sidecar artifact 承载。若未来要求
“sidecar 已删除后仍能独立展开完整 artifact lineage”，应版本化扩展 artifact input provenance，
不能在当前 renderer fingerprint 下改变 body shape。

同样需要准确区分两层 coherence：D4 校验的是 caller-selected members 具有共同 coverage anchor、
正确 setup/current lineage 与不重复 target；“active set 必须包含 autobiography +
world-understanding，且二者属于同一次原子 activation”是后续 `ArtifactSetCommitted` /
Context Planner 的 semantic membership policy。`DerivedRecapArtifact.InputArtifacts` 是 producer
dependency/lineage，不等价于 active-set membership，不能拿它猜后一条合同。

### CS-3D5：Legacy 与性能收口

> **状态**：已实施
> **历史说明**：D5 先建立迁移、activation、validator 与性能闸门；D6D 已完成 Prepared v2
> coherent-only wire cut，以下“过渡 reader”描述不再是 current runtime。

- 为旧 import/full-raw repo 提供显式 offline validate/migrate/checkpoint 命令。
- benchmark header visits、payload reads、decoded bytes 与 peak memory；不以易抖动 wall-clock 作为唯一
  验收。
- 清理仅为 live full replay 保留的内部耦合。

实际落点：

- 新增 raw kind 12 `ArtifactSetCommitted`。event address 本身是 activation identity；body 只保存
  membership policy/fingerprint、common coverage anchor、coverage/current governing setup 的
  address/schema/payload-hash refs，以及按 role canonical 排序的 exact members。member 固定
  role、artifact id/kind、target 与 singleton contribution hash，不把 `MemoryPack` 或 request snapshot
  复制进 raw。
- `CommitArtifactSetAsync()` 只允许 exact idle head，以 full parent-lineage/coherence validation 读取
  exact sidecars，校验共同 anchor、source head、setup、唯一 role/id/target 和 contribution hash，再以
  exact-head CAS 原子 append。sidecar 仍是可删除的 derived payload；activation raw event 是“哪一组
  exact artifacts 生效”的权威事实。
- coherent Prepared 的 plan 固定 activation 的 `{ address, bodySchemaVersion, payloadSha256 }` exact
  reference，并继续内联 provider-facing singleton contributions。online active-set resolver 从
  以下 activation 描述是 D6/D7 历史实现：completion boundary 回溯当时直接遇到 activation 即命中；
  遇到近头 coherent Prepared 则由 exact
  reference 恢复并核对 member assertion，不读取 `latest`、dedicated ref 或 state cache。该一跳
  fast path 延续 SessionJournal online path 的受控 writer 信任边界；对离线导入或低层构造的 raw，
  strict validator 会逐 Prepared 重建 canonical request，并证明它引用的是 authoritative raw range
  中最后一个 activation，不能让旧或 divergent activation 冒充当前集合。
- activation 同时保存 coverage-anchor 与 activation-current setup refs。首次 Observation request 可从
  activation 的 current refs 取得 governing setup；artifact projector 从 coverage refs 直接取得
  anchor seed。两者都验证 kind/schema/payload hash，消除了“setup 长期不变时每轮回溯到 root”的隐藏
  O(history)。
- D7 历史 `SessionRuntime` 不再暴露 request-context policy selector；online writer 只有 coherent
  artifact-tail。缺 activation/member 时在 append Observation/Prepared/provider 前 fail-fast，绝不
  静默 full replay。public `Project()` / `ReplayHistory()` 仍保留完整审计语义；当时 Prepared v3 的
  `activeArtifactSet` 为 required exact reference。DM-4/DM-8 已删除 raw activation/reference；
  current Prepared v5 保存 exact context snapshots，unprepared planning 由 host 注入 two-phase
  candidate source。
- D6D 是 breaking wire upgrade：旧实验 journal 若包含早期 Prepared bytes，必须走离线重建/
  版本化迁移，不能用缺省字段猜测过去；仅含 current imported raw facts 的 repo 可直接 validate，
  再在 artifacts 就绪后 append activation。

离线收口：

- `import-legacy-json` 被明确界定为 legacy upgrade export → current SessionJournal wire 的迁移：
  写入新 repo 与 ordinal→address mapping，不原地改 immutable raw。没有旧 codec 时不声称能迁移任意
  旧 SessionJournal wire。
- `validate` 通过 `EventJournal.OpenReadOnlyExisting()` 手工沿 Parent 做
  cycle/continuity 检查与 strict payload decode，再比较 full reducer 和 exact-head tail resolver，
  报告 setup、`PreparedRequestCount`、logical payload bytes 与 active-set readiness。每个
  `ArtifactSetCommitted` 的 common anchor、coverage/current governing setup refs 都按其历史边界
  验证；每个 Prepared 都走 canonical reconstruction，coherent Prepared 还必须引用截至其 parent 的
  effective/latest activation。
- strict read-only open 把只读合同下沉到 active event RBF、ref-op-log 和 live ref object：
  不 recovery/truncate/rotate，不创建 refs/cache 目录，不读写/删除 disk ForwardPlan。malformed active
  event/ref-op/ref-object tail 均 fail-fast；测试逐文件比较 length + SHA-256，证明失败前后 repo bytes
  不变。为完成 inventory/ref validation，它有意 O(raw inventory)，只服务 offline administration，
  不进入 online tail recovery。
- `checkpoint-artifact-set --member role=id ...` 先 full validate，再调用上述 exact
  commit API，并 post-validate 只新增一条可用 activation；旧 events/manifests 与 derived files 均不
  改写。

性能观测与验收：

- `SessionJournalEventReader` 现在统计 header visits、payload reads、成功读取的
  `LogicalPayloadByteCount`、chronological reads/events 与 `Project()` 次数。SessionJournal-owned
  frame lease 另统计 current/peak live logical payload bytes；重复 dispose 幂等，失败读取只增加 read
  count，不增加 bytes。
- 1 与 10001 个 closed imported turns 的真实对照，分别覆盖 Observation completion，以及两轮 tool
  continuation/三次 provider request。两组的 header reads、payload reads、logical bytes 与 peak-live
  bytes 完全相同，current-live、chronological chain/event、full projection 均为 0；三次 provider
  request 还逐次比较各阶段增量，避免多阶段回归在 aggregate 中互相抵消。
- 测试还发现并修复 `BuildOperationId` 使用默认 `EventAddress.ToString()` 时 payload 长度随物理坐标
  文本变化的问题；现在使用固定宽度 canonical `EventAddressTextCodec`。
- `PeakLiveLogicalPayloadBytes` 是确定性的同时存活 logical frame payload 指标，不等价于 compressed
  stored bytes、ArrayPool capacity、decoded object graph 或 derived sidecar/MemoryPack 内存。后几项若
  需要，应另设指标；不把 GC/working-set 或 wall-clock 抖动值作为单元验收门槛。

验收证据：

- `SessionJournal.Tests`：194/194。
- `SessionJournal.Cli.Tests`：35/35。
- `RbfFileFactoryTests`：8/8；`RbfSegmentStore.Tests`：18/18；`EventJournal.Tests`：38/38。
- SessionJournal project references 中没有 `Agent.Core`。

已知剩余边界：

- CLI 的 prevalidate → commit 跨进程窗口不是一个事务；真正的 mutation 仍由
  `CommitArtifactSetAsync()` 对它自己观察到的 exact head 做 CAS，postvalidate 还要求 event count
  恰好 +1。若未来需要“必须基于 prevalidated head”这一更强管理合同，应把 expected head 显式传入
  commit API。
- active member sidecar 在 Prepared 前删除会造成可诊断的 liveness failure；Prepared 后仍从 inline
  manifest exact reopen。这是 raw activation 与 derived content 的有意边界。

## 11. 测试矩阵

至少覆盖：

| 维度 | 用例 |
| --- | --- |
| Head phase | Empty/Idle/Failed/Observation/Prepared/CompletionAttemptStarted/Action/ToolStarted/Result |
| Tool calls | 0、1、多个；每个 start/result failpoint |
| Completion | observation、tool-continuation、known failure、uncertain restart |
| Setup | 尾段无变化、prompt-only、runtime-only、两者变化 |
| Artifact | coherent set、anchor 不可达、成员 lineage 不一致、Prepared 后 sidecar 删除 |
| Branch | fork、rewind、divergent checkpoint |
| Corruption | 错 Parent、错 attempt、错 correlation、乱序/重复 tool result、sequence 回退 |
| Complexity | 10k+ 冷前缀下 execution-routing read delta 不随 prefix 增长；request reads 按 plan kind 单独断言 |

测试采用两类断言：

1. **语义断言**：tail recovery state 与 full reducer oracle 一致。
2. **复杂度断言**：execution routing 不调用 `Project()`，读取量只随 operational tail 变化；
   artifact-tail request 只随 artifact/suffix 变化；current online path 不存在 full-raw fallback。

## 12. 明确否决的捷径

- 给 `SessionReducer` 塞一个 config seed 后宣称完成 tail recovery：它仍缺 session marker、attempt、
  correlation、tool dependency 与 sequence。
- 在内存/sidecar 保存完整 `SessionProjection`：把冷历史重新物化成另一份双真源。
- online resolver 找不到 checkpoint 时静默 full replay：会让性能在最长会话上突然退化且难以观测。
- 把 artifact coverage anchor 直接当 dependency boundary：可能从 ToolResult 中间启动。
- 为恢复状态加载 autobiography/world-understanding 文本：execution correctness 不依赖这些内容。
- 为构造 LLM context 加载完整 raw history：正常长会话必须走 artifact set + suffix。

## 13. 下一次 Coding Session 的起点

CS-3D 的 tail execution recovery 主线已闭合。下一轮更自然的起点不是继续扩 resolver DFA，而是从
以下相邻能力中选择一个独立切片：

1. 为 non-idempotent / externally queryable tools 设计跨进程 lease、result lookup 与
   reconcile/pause policy；当前 durable operation id/sequence 只提供 identity，不提供 exactly-once。
2. 在 durable activation 之上实现真正的 Context Planner/ArtifactSet semantic policy 与 budget；
   SessionJournal core 目前只持久化 caller 提交的 role membership，不硬编码 autobiography /
   world-understanding 的应用层规则。
3. 若管理工具需要更强事务语义，把 offline validator 返回的 exact head 显式传入 artifact-set commit，
   收掉 prevalidate→commit 的跨进程 TOCTOU。
4. 若要把 online trust boundary 从“受控 writer”提升为“任意低层 raw 也即时自证”，需另行设计
   bounded authenticated checkpoint；不要把 strict offline 的 O(raw inventory) validation 偷渡进
   online resolver。当前 divergent/旧 activation、错误 historical coverage/current setup refs 已由
   offline validator fail-fast。

继续保持：不引入 state/context cache，不读取 `latest` 决定 active membership；raw Parent lineage、
`ArtifactSetCommitted`、committed manifest 与 exact artifact ids 是 provenance 边界。
