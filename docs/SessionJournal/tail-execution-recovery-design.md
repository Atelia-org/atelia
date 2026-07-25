# SessionJournal Tail-only Execution Recovery Design

> **状态**：Design Baseline / CS-3D0 已实施，CS-3D1 待实施
> **日期**：2026-07-26
> **建议路线编号**：CS-3D
> **前置实现**：CS-3A governing setup checkpoint、CS-3B dependency-closed tail context、
> CS-3C committed request recovery
> **相关文档**：
> [Configuration Access Notes](session-configuration-access-notes.md)、
> [SessionJournal 主干设计](session-journal-trunk-design.md)、
> [ChatSession 架构路线图](../ChatSession/event-sourced-session-architecture-roadmap.md)

## 0. 给后续 Coding Agent 的结论

当前 `SessionJournalEngine.Project()` 会从 ref root 正向解码到 ref head，并用 `SessionReducer`
同时构造完整 conversation context 与 execution state。这个实现仍可作为审计、迁移和测试 oracle，
但**不得继续作为在线 reopen / resume 的默认依赖**。

后续主线必须拆成两个彼此独立的投影：

1. **Execution Recovery Projection**：只回答“链头现在处于什么 phase，下一合法动作是什么，执行该动作还缺
   哪些局部依赖”。它从 head 沿 Parent 反向读取当前 operational tail，绝不物化完整 conversation。
2. **Request Context Projection**：只在确实要调用 LLM 时运行。正常长会话使用 coherent
   recap/artifact set（rolling 第一人称自传、world-understanding 等）加 dependency-closed raw
   suffix，并将最终 request 固化进 committed manifest。

显式 `Project()` / `ReplayHistory()` 继续允许 O(全历史)，因为调用方明确请求完整审计投影。在线
`Open`、`ResumeAsync`、`SendAsync`、tool-loop driver、setup mutation 与 request preparation 不应为了
判断 execution phase 而隐式调用它们。

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
  `Idle`、`TurnFailed`、`AwaitingAgentAction`、`AwaitingCompletion`、
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
- 不声称旧的 `full-raw` Prepared manifest 能在不读旧 raw 的情况下 exact 重建。
- 不在本切片解决 provider-side idempotency/result lookup 或非幂等工具的完整补偿策略。

## 2. 三类投影必须分离

| 投影 | 回答的问题 | 输入 | 输出 | 在线复杂度 |
| --- | --- | --- | --- | --- |
| Full Audit Projection | 历史完整发生了什么 | root…head raw chain | 完整 context + execution state | O(全历史) |
| Tail Execution Projection | 现在应执行什么 | head 附近 operational tail + raw checkpoints | 最小 execution state | O(局部依赖) |
| Request Context Projection | 下一次 LLM 看见什么 | exact artifact set + dependency-closed suffix + setup | bounded canonical request | O(artifact + suffix) |

`SessionReducer` 是第一行的正确性 oracle；拟新增的 `SessionExecutionTailResolver` 属于第二行；现有
`SessionTailContextProjection` 及后续 Context Planner 属于第三行。

这三者不能再通过“先构造完整 `SessionProjection`，然后只取其中一个字段”耦合。

## 3. 当前实现边界

关键文件：

- `prototypes/SessionJournal/SessionJournalEngine.cs`
  - `Project()` / `ReplayHistory()`：完整 `ReadChronologicalChain`。
  - `ResumeAsync()`：Prepared/Restarted 与窄 observation-tail 有 fast path，其余 phase 回落到
    `Project()`。
  - `ContinueToolLoopAsync()`：每次 append tool result 后再次 `Project()`。
- `prototypes/SessionJournal/SessionReducer.cs`
  - 同时累计 config、system prompt、完整 context 与 execution state。
  - `ToolExecutionSequenceCheckpoint` 当前通过从 root 计数 `ToolResultObserved` 得到。
- `prototypes/SessionJournal/SessionTailContextProjection.cs`
  - 已能从 artifact anchor fold dependency-closed suffix。
  - 这是 request context projector，不是通用 execution reducer。
- `prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs`
  - `explicit-artifact-tail` 只读 manifest 固化的 snapshot + suffix。
  - 旧 `full-raw` manifest 仍按合同读取 `[root, raw end]`。

已有 fast path 应复用，不应另造第二套 attempt/setup 解析：

- `ResolveGoverningSetup`。
- `ResolvePreparedAttemptIdentityChain`。
- `ValidateTailIdleBoundary` 及 live/failed terminal 的局部因果验证。
- manifest 中的 setup refs、attempt identity、correlation 与 exact canonical commitment。

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
       Idle / TurnFailed:
         wait for observation or setup mutation

       AwaitingCompletion:
         resolve P/R identity chain
         apply Refuse / lookup / explicit restart policy
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
| `CompletionRequestPrepared` / `CompletionAttemptRestarted` | 复用 P/R identity-chain resolver；`AwaitingCompletion` |
| `CompletionAttemptFailed` | 验证 direct active attempt 与 attempt id；`TurnFailed` |
| terminal `AgentActionProduced` / `ImportedAgentAction` | 验证来源与无未结算 calls；`Idle` |
| Action 含 tool calls | 保存 action 与声明顺序；首个 call pending |
| `ToolExecutionStarted` | 回溯到当前 Action，join call id/name/args；恢复同一 operation 与 reserved sequence |
| `ToolResultObserved` | 只收集当前 Action 之后的 starts/results；按 Action 声明顺序决定下一个 pending call，全部结算则 `AwaitingAgentAction` |

Prepared/Restarted 控制事件不进入 conversation context，但它们是 execution recovery 的权威
checkpoint。Action 的多个 tool results 必须继续按声明顺序 join，而不是按 append 顺序推断。

### 6.3 Imported path

live Action 必须从 active Prepared/Restarted 产生，可直接从 manifest 取得 correlation 与 checkpoint。
manual/legacy `ImportedAgentAction` 没有 manifest，因此新 schema 应携带最小 execution checkpoint，或在
导入完成时追加一个明确 checkpoint。

不要为少数 legacy path 让所有正常 reopen 回退到 root。旧 import repo 应通过一次性迁移补齐，而不是永久
拖累 live protocol。

## 7. Tool execution sequence 必须先变成近头 durable fact

当前 `ToolExecutionSequenceCheckpoint` 从 root 累计 `ToolResultObserved`，是通用 tail recovery 的主要
长程阻塞点，而且“执行后才从结果计数”不能精确表达已 durable-started 但尚未 observed-result 的调用。

推荐协议：

1. `CompletionRequestPrepared` 增加最小 `ExecutionCheckpoint`，至少保存
   `LastIssuedToolExecutionSequence`。
2. `ImportedAgentAction` 同样携带 checkpoint，或要求 import 后追加专用 checkpoint。
3. 开始工具前先计算并 durable reserve `nextSequence = lastIssued + 1`。
4. `tool-execution-started` 同时保存 `operationId` 与 `executionSequence`。
5. 工具执行获得的 `ToolExecutionContext.ExecutionSequence` 必须等于已落盘的 reserved sequence；
   不得在外部调用开始后再随机分配。
6. `tool-result-observed` 重复保存并校验同一个 sequence。
7. reopen 于 Started 时，reconcile/retry 使用同一 operation id 与 sequence；reopen 于 Result 后，下一次
   从该 sequence + 1 开始。

这可能需要为 `ToolSession` 增加“执行已保留 sequence”的正式 API，而不是临时修改
`AuthoritativeExecutionSequenceAllocator`。项目尚未发布，优先做干净的协议升级与 event schema golden
更新，不保留长期兼容分支。

Prepared 每次 LLM 调用前都会出现，因此即使很多轮没有工具，最近 checkpoint 仍贴近 head；活跃工具段内
Started/Result 自身继续推进 checkpoint。

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

现有 `DerivedRecapArtifact.MemoryPack` 和 exact artifact tail 是可复用基础，但当前运行时仍只接受一个
exact `ArtifactId`；多 maintainer coherent activation 属于后续 ArtifactSet/Context Planner 切片。

### 8.2 Observation 与 tool continuation 统一

现有 explicit tail path 只承诺：

```text
ObservationAccepted + no visible tools
```

下一阶段要把 request boundary 泛化为 dependency-closed completion boundary：

- 新 observation；
- 当前 Action 的全部 tool calls 已有 ToolResult；
- 其他未来被 planner 明确定义为可请求 completion 的边界。

`SessionTailContextProjection.FoldSuffix` 已有局部 tool join 能力，可作为基础；但 planner、manifest
reason/correlation、tool definitions 与 tool implementation identity 必须一并固定。

### 8.3 Prepared 后恢复

Prepared 提交前可以重新选择 artifact set；Prepared 提交后禁止重跑 planner。manifest 必须继续内联
足以 exact 重建 canonical request 的有界 materialized snapshot，因此 derived artifact 删除不会破坏
在途 request。

旧 `full-raw` manifest 的 exact recovery 仍是 O(全历史)。这是旧请求合同的真实成本，不应通过偷偷改用
新 recap 改写过去；正常新请求应默认产生 artifact-tail manifest，使它逐渐退出热路径。

### 8.4 Bootstrap 与 artifact 不可用

首次 artifact 尚未产生且 raw history 仍在明确预算内时，planner 可以提交显式 `full-raw` bootstrap
plan。长历史没有可用 coherent artifact set 时，不得把 root replay 当成无声兜底；应先触发/等待
maintainer 重建，或把 session 置为可诊断的 paused 状态。

Prepared 提交前 artifact 缺失属于 planning/liveness 问题；Prepared 提交后则由 manifest 内联 snapshot
保证恢复，不再依赖 sidecar 是否存在。

## 9. Tool runtime identity 与副作用安全

tail-only 只能解决“读多少历史”，不能自动解决外部副作用：

- manifest 当前固定 tool definitions，不等于固定 tool implementation identity。
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

- phase matrix 覆盖 Observation、P/R、Failure、Action、Started、Result、Idle、setup run。
- 测试明确区分 audit full replay 与 online recovery reads。

实际落点：

- 新增 `SessionJournalEventReader`，统一承接 Engine、Prepared reconstructor 与 tail context projector
  的逻辑读取；累计 header preview、payload read、chronological-chain 调用及返回 event 数。
- `SessionJournalEngine.CaptureReadDiagnostics()` 将上述累计值与
  `FullProjectionInvocationCount` 合并成可做 snapshot delta 的
  `SessionJournalReadDiagnostics`。它计量 SessionJournal 发起的逻辑 API reads，不冒充底层物理 IO、
  page cache 或解压字节统计。
- 新增 full reducer reference-oracle matrix，冻结 Empty、Idle、AwaitingAgentAction、
  AwaitingCompletion、AwaitingToolExecution、TurnFailed 各 phase 的必要字段；后续 D2 differential
  tests 应复用同一语义合同。
- 冷前缀 baseline 证明：`T` 个已闭合 imported turns 的 Idle `ResumeAsync` 当前读取
  `3 + 2T` 个 payload、返回同样数量的 chronological events，并调用一次 full `Project()`；Prepared
  `RefuseUncertain` 在不同前缀长度下始终只读一个 head header 与一个 Prepared payload，不调用
  chronological chain / `Project()`。

相关文件：

- `prototypes/SessionJournal/SessionJournalEventReader.cs`
- `prototypes/SessionJournal/SessionJournalContracts.cs`
- `prototypes/SessionJournal/SessionJournalEngine.cs`
- `tests/SessionJournal.Tests/SessionExecutionRecoveryContractTests.cs`

### CS-3D1：Durable execution checkpoint 与 reserved tool sequence

> **状态**：下一实施切片

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

### CS-3D2：`SessionExecutionTailResolver`

目标：实现独立、纯读取、不构造 Context 的 tail execution projection。

- 按 §6 的 head-kind DFA 实现反向 dependency collection。
- 复用 setup、P/R identity 与 tail terminal validators。
- 对受控 writer 产生的合法 fixtures，与 full reducer 的 `ExecutionState` 做 differential tests。
- 对分支、rewind、错 Parent、错 attempt、乱序 tool result、重复 call id fail-fast。

验收：

- 正常链上 state 与 full reducer oracle 一致。
- reads 只覆盖 operational tail/checkpoint。

### CS-3D3：Engine driver 切换

目标：在线恢复路径彻底脱离 `Project()`。

- `ResumeAsync()` 所有 phase 改用 tail resolver。
- `ContinueToolLoopAsync()` 每次 append 后增量重解新 tail，不再 `Project()`。
- `SendAsync()`、setup mutation、import append 的 boundary validation 改用 recovery state。
- pending tool 只有在 D1 的 durable implementation/capability identity 验证通过后才能执行。
- 保留 public `Project()` / `ReplayHistory()` 的 full semantics。

验收：

- 所有 online phase 的 `FullProjectionInvocationCount` 保持不变。
- 10k+ 冷历史前缀不增加恢复 payload reads。
- exact-head CAS/failpoint 行为与原实现一致。

### CS-3D4：Artifact-tail completion 泛化

目标：恢复到 `AwaitingAgentAction` 后，Observation 和 tool continuation 都不再借 full context 发请求。

- 引入 exact coherent ArtifactSet selection；至少组合 autobiography 与 world-understanding。
- 把 explicit tail materialization 扩展到 dependency-closed ToolResult boundary 和 visible tools。
- 将 D1 已固定的 tool implementation/capability snapshot 纳入 artifact-tail manifest 重建验证。
- committed manifest 继续支持 artifact 删除后的 exact reopen。

验收：

- 长历史上的 observation completion 与多轮 tool continuation 均不调用 `Project()`。
- 两种 maintainer artifact 的 lineage/coherence 可审计。
- sidecar 在 Prepared 后删除仍可恢复；Prepared 前缺失则 fail-fast/重新规划。

### CS-3D5：Legacy 与性能收口

- 为旧 import/full-raw repo 提供显式 offline validate/migrate/checkpoint 命令。
- benchmark header visits、payload reads、decoded bytes 与 peak memory；不以易抖动 wall-clock 作为唯一
  验收。
- 清理仅为 live full replay 保留的内部耦合。

## 11. 测试矩阵

至少覆盖：

| 维度 | 用例 |
| --- | --- |
| Head phase | Empty/Idle/Failed/Observation/P/R/Action/Started/Result |
| Tool calls | 0、1、多个；每个 start/result failpoint |
| Completion | observation、tool-continuation、known failure、uncertain restart |
| Setup | 尾段无变化、prompt-only、runtime-only、两者变化 |
| Artifact | coherent set、anchor 不可达、成员 lineage 不一致、Prepared 后 sidecar 删除 |
| Branch | fork、rewind、divergent checkpoint |
| Corruption | 错 Parent、错 attempt、错 correlation、乱序/重复 tool result、sequence 回退 |
| Complexity | 10k+ 冷前缀下 header/payload read delta 不随 prefix 增长 |

测试采用两类断言：

1. **语义断言**：tail recovery state 与 full reducer oracle 一致。
2. **复杂度断言**：online driver 不调用 `Project()`，读取量只随 operational tail/suffix 变化。

## 12. 明确否决的捷径

- 给 `SessionReducer` 塞一个 config seed 后宣称完成 tail recovery：它仍缺 session marker、attempt、
  correlation、tool dependency 与 sequence。
- 在内存/sidecar 保存完整 `SessionProjection`：把冷历史重新物化成另一份双真源。
- online resolver 找不到 checkpoint 时静默 full replay：会让性能在最长会话上突然退化且难以观测。
- 把 artifact coverage anchor 直接当 dependency boundary：可能从 ToolResult 中间启动。
- 为恢复状态加载 autobiography/world-understanding 文本：execution correctness 不依赖这些内容。
- 为构造 LLM context 加载完整 raw history：正常长会话必须走 artifact set + suffix。

## 13. 下一次 Coding Session 的起点

从 **CS-3D0** 开始，不要直接重写 `ResumeAsync()`：

1. 先为当前 phase matrix 建立 read diagnostics 与 full reducer oracle。
2. 具体设计 CS-3D1 的 event body/schema 和 `ToolSession` reserved-sequence API。
3. 在 schema 评审通过后实现 D1，再实现纯读取 D2。
4. D2 differential tests 稳定后，才让 D3 替换 online driver。

这样能把“协议正确性”“tail resolver 正确性”“driver 外部副作用”分成可独立审阅的风险面。
