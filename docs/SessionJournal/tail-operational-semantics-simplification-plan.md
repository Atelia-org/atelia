# SessionJournal Dependency-closed Fold Seed 与共享 Operational Semantics 实施计划

> **状态**：D0 / D1 Implemented；D2 Deferred / NO-GO pending A0
> **日期**：2026-07-28
> **设计基线（实施前）**：Prepared v5、CS-3D0～D7、DM-0～DM-8；
> 第 1～5 节中的 “current” 若未另行标注，均指提交 `92bd049d` 时的实施前基线
> **来源**：
> [Tail Execution Recovery 后续化简候选](tail-execution-recovery-simplification-study.md)
> **相关实现记录**：
> [Tail-only Execution Recovery Design](tail-execution-recovery-design.md)
> **范围说明**：本文的 D0 / D1 是“化简候选 D”的内部子切片，不是历史
> `CS-3D0` / `CS-3D1`。
> **目标**：先删除 suffix fold 的隐式 seed 双路径，再共享稳定、无 IO 的局部 operational
> semantics；不在本计划中合并 full reducer、suffix projector 与 reverse tail traversal。

## 0. 执行结论

候选 D 的 D0/D1 已按下列分片完成；这从未、也不能解释为批准一次“大一统 operational fold”重写。

```text
Simplification-D0a Retire unused single-candidate path
  ↓
Simplification-D0b Explicit dependency-closed fold seed
  ↓
Simplification-D1a Pure classification + correlation identity
  ↓
Simplification-D1b Shared action/tool validators + violation vocabulary
  ↓
Simplification-D1c Differential semantic matrix
  ↓
Candidate-D2 go/no-go：Deferred / NO-GO pending A0
```

本计划批准的 D0 与 D1 已实施。D2 已在 D1c 后完成 go/no-go 复核，当前结论为
**Deferred / NO-GO**：不要在 production 中建立 `SessionOperationalFold`，见
[§6](#6-d2-gono-go)。

核心决议：

1. `SessionTailContextProjection.FoldSuffix(...)` 必须接收显式、不可空、与同一 raw anchor
   绑定的 fold seed。
2. seed 的职责不是复制 `SessionExecutionRecovery` 字段，而是证明：
   - governing setup 与 execution recovery 属于同一 anchor；
   - anchor 是 dependency-closed / replay-safe；
   - fold 无需桥接 pending Prepared、attempt 或 tool dependency。
3. D1 只提取无 IO、无 traversal、无 storage、无 provider/derived dependency 的纯语义。
4. `SessionReducer`、`SessionTailContextProjection` 与
   `SessionExecutionTailResolver` 继续拥有各自 traversal 与输出职责。
5. Legacy importer 验证的是旧 export grammar，不成为 current raw operational semantics
   的消费者。Offline validator 继续编排 full reducer 与 tail resolver，而不是建立第四套状态机。

### 0.1 实施记录与结果

| 工作包 | 提交 | 结果 |
| --- | --- | --- |
| D0a | `b79d67f8`、`ce906d40` | 退役 single-candidate materialization/validation path，并清理其残留 projection 字段 |
| D0b | `ac5e15f2`、`c17a0960` | 引入 anchor-bound、dependency-closed typed fold seed，并补齐 correlation rejection matrix |
| D1a | `522c31b3` | 共享 kind/phase classification 与 observation correlation identity |
| D1b | `5cca0dd2`、`b93263e3` | 共享 Action/tool local validators、internal violation vocabulary 与 renderer；修正文档性注释 |
| D1c | `2f15c0d9` | 建立 legal/illegal differential matrix，并强化 Prepared/Started 不访问 candidate provider 的证据 |

实际结果：

- `SessionTailContextProjection.Materialize(...)`、
  `ValidatedSessionContextCandidate` 与
  `SessionContextCandidateValidator.Validate(...)` 已删除；
- 两个 live fold caller 均通过同一个 `SessionDependencyClosedFoldSeed.Create(...)` 进入
  `FoldSuffix(...)`；nullable seed、`InferSeedPhase(...)` 与 checkpoint fallback 已删除；
- `SessionOperationalSemantics` 成为 classification、correlation、Action/tool local validator 与
  violation code 的 single source；三种 projection 仍各自拥有 traversal、source proof 与输出；
- legal matrix 有 19 个 scenario：13 个可做 full reducer / typed-seed fold / exact-head resolver
  三方比较；6 个合法 open-tool head 只能做 reducer/resolver exact equality，并要求 request-context
  fold 因 final dependency 未闭合而拒绝；
- illegal matrix 覆盖 17 类 Parent/attempt/correlation/checkpoint/tool identity/order/sequence/setup
  错误；codec-owned identity presence wire invariant 与 decoded operational validation 被明确分层；
- 最终 `SessionJournal.Tests` 为 280/280，solution build 为 0 warning / 0 error；
- 相对实施前计划提交 `92bd049d`，production 按 `git numstat` 粗指标为 731 行新增、633 行删除，
  净增 98 行。行数不等同复杂度；D0 删除了真实 dead surface，D1 建立了 semantic single source，
  但 adapter/diagnostic 跳转成本也真实存在。

没有改变 raw wire、public API、online read authority 或程序集依赖方向。

## 1. Pre-implementation facts（历史基线）

本节记录为什么批准 D0/D1；其中 nullable seed、旧 single-candidate path 等描述已经由
[§0.1](#01-实施记录与结果) 所列提交收口，不再代表 current code。

### 1.1 Nullable seed 已经没有生产必要

current `SessionTailContextProjection.FoldSuffix(...)` 接受：

```csharp
SessionGoverningSetup seed,
IReadOnlyList<DecodedSessionEvent> events,
SessionExecutionRecovery? executionSeed = null
```

初始化逻辑为：

```csharp
SessionExecutionPhase phase = executionSeed?.State.Phase
    ?? InferSeedPhase(executionSeed?.State.HeadKind);
```

这里存在一个比“phase 推断可能不准确”更直接的问题：

- `executionSeed` 非空时，`State.Phase` 是非 nullable enum，不会进入
  `InferSeedPhase(...)`；
- `executionSeed` 为空时，传给 `InferSeedPhase(...)` 的 `HeadKind` 也必然为空；
- 因而 `InferSeedPhase` 中针对 setup、Action、ToolResult、Prepared 等非空 kind 的分支均不可达；
- 缺 seed 的实际行为只是默认为 `Empty`，不是可靠地从某个 head kind 推断状态。

current code 中有三个 `FoldSuffix(...)` 直接调用点，但只有两个属于 current live route：

1. `SessionTailContextProjection.Materialize(...)`
   - current repo 没有调用者；
   - 它消费的 `ValidatedSessionContextCandidate` 与
     `SessionContextCandidateValidator.Validate(...)` 同样只被旧 contract tests 直接使用；
   - DM-8 online route 已改为 batch planning window +
     `SessionJournalEngine.MaterializeSelectedContext(...)`。
2. `SessionJournalEngine.ReadHistoryPlanningWindowAtCore(...)`
   - 优先消费 repository-bound planning seed 内的 recovery；
   - 缺失时对 resolved start 调用 `ValidateReplaySafeBoundary(...)`；
   - 真正进入 fold 前始终拥有非空 recovery。
3. `SessionPreparedRequestReconstructor`
   - 对 `rawStartExclusive` 和 `rawEndInclusive` 分别调用 tail resolver；
   - fold 后核对 exact final recovery 与 Prepared v5 commitment。

因此 D0 应先删除已经失去 production caller 的单 candidate materialization surface，而不是为它迁移
新 seed；随后只迁移 history planning 与 Prepared reconstruction 两条 live route。删除旧 surface
时，要把仍有价值的 raw-authority assertions 迁到 current provider-route/batch tests，不能用删测试
掩盖 coverage 缺口。

剩余两个 live caller 都已经取得 exact recovery，所以 seed 收紧不引入新的 recovery authority。

### 1.2 Fold 实际消费的 recovery 子集

fold 只读取：

- anchor address；
- anchor head kind；
- exact execution phase；
- last-issued tool execution sequence；
- active correlation id。

它不读取：

- `SessionExecutionRecoveryBoundary`；
- recovery diagnostics；
- full context；
- DerivedMemory identity；
- provider state。

但 fold 同时依赖独立传入的 `SessionGoverningSetup`。两份对象都带 anchor，却没有一个组合合同在
入口处证明它们指向同一 address。这是 D0 应顺手关闭的真实缝隙。

### 1.3 三种消费者共享局部规则，但不共享 traversal

当前重复主要集中在：

- setup / Action / tool-segment kind classification；
- observation correlation identity；
- Action tool-call declaration shape 与 duplicate call id；
- pending tool call 的 id / name / raw arguments join；
- tool runtime identity 与 reserved sequence；
- replay-safe、idle-or-failed 等 phase predicate。

三种消费者不可共享的职责为：

| 消费者 | 必须保留的独立职责 |
| --- | --- |
| `SessionReducer` | root-to-head full audit、完整 context、addressed provenance、sticky setup projection |
| suffix fold | seeded forward range、suffix context、planning units、replay-safe boundaries、final dependency closure |
| `SessionExecutionTailResolver` | head-to-root dependency discovery、Prepared/Started Parent proof、Action source proof、bounded checkpoint cut |

特别是 imported Action 的 bounded cut 不能递归穿越所有旧 turn；这属于 tail-only 性能与正确性合同，
不能被通用 forward fold 取代。

## 2. Goals 与 non-goals

### 2.1 Goals

- 删除 nullable execution seed、不可达 `InferSeedPhase` 与 nullable checkpoint 分支。
- 删除无 production caller 的旧 single-candidate validation/materialization surface。
- 在 fold 入口证明 governing setup 与 execution recovery 的 anchor identity。
- 明确哪些 raw boundary 可以成为 dependency-closed fold seed。
- 让 planning、candidate materialization 与 Prepared v5 reconstruction 共用同一 seed factory。
- 为重复的 pure classification、correlation 与 action/tool validators 建立单一真源。
- 为共享 validator 建立 internal、机器可比较的 violation vocabulary。
- 建立 full reducer、suffix fold、tail resolver 的合法/非法 differential matrix。
- 保持 online reads、exact-head CAS、Prepared reopen 与 full audit public semantics 不变。

### 2.2 Non-goals

- 不改变 raw event kind、payload schema、Prepared v5 wire 或 canonical request bytes。
- 不改变 public `Project()` / `ReplayHistory()` 的 full semantics。
- 不让 SessionJournal 依赖 DerivedMemory、Maintainers 或 Agent.Core。
- 不合并 reverse tail collector 与 forward fold。
- 不把 full context、governing setup lookup 或 DerivedMemory selection 放进 operational semantics。
- 不把自然语言异常文本升级为 public compatibility contract。
- 不让 Legacy importer 复用 current raw event transition kernel。
- 不在 D0/D1 中拆分 `SessionJournalEngine`。
- 不在 D0/D1 中决定 candidate A 的 canonical request snapshot 取舍。

## 3. Simplification-D0：显式 dependency-closed fold seed

### 3.1 推荐合同

推荐新增 internal 组合值对象，暂名：

```csharp
internal sealed record SessionDependencyClosedFoldSeed(
    SessionGoverningSetup GoverningSetup,
    SessionEventKind HeadKind,
    SessionExecutionPhase Phase,
    long ToolExecutionSequenceCheckpoint,
    string? ActiveCorrelationId
) {
    public EventAddress Head => GoverningSetup.Head;
}
```

最终命名可以在实现 review 中调整，但它必须同时拥有 governing setup 与 bounded execution
initial state。不要保留：

```text
FoldSuffix(governingSetup, events, independentExecutionSeed)
```

这种允许两份 anchor 静默分叉的形状。

seed 只能通过 internal factory 从：

```text
SessionGoverningSetup + SessionExecutionRecovery
```

构造。factory 是 proof boundary，不是方便用 mapper。

### 3.2 Factory invariants

factory 至少验证：

1. `recovery.Head` 非空，且等于 `governingSetup.Head`。
2. `recovery.State.HeadKind` 非空。
3. `ToolExecutionSequenceCheckpoint >= 0`。
4. state 不携带任何未闭合执行依赖：
   - `PendingToolCall == null`；
   - `PendingOperationId == null`；
   - `PendingToolExecutionStarted == false`；
   - `PendingRequestPreparedAddress == null`；
   - `ActiveCompletionAttemptAddress == null`；
   - `PendingToolRuntimeIdentity == null`。
5. phase / head-kind 组合属于下表。
6. `AwaitingAgentAction` 必须有非空 `ActiveCorrelationId`；其他合法 seed phase
   不得保留 active correlation。

合法矩阵：

| Phase | 合法 HeadKind | 说明 |
| --- | --- | --- |
| `Empty` | `SystemPromptSetup` | setup-only genesis；完整 governing setup 已可由 ancestors 取得，下一合法 bootstrap event 是 `SessionCreated` |
| `Idle` | `SessionCreated`、`RuntimeConfigSetup`、`SystemPromptSetup`、terminal `AgentActionProduced` / `ImportedAgentAction` | 已创建 session 的无 pending boundary |
| `AwaitingAgentAction` | `ObservationAccepted`、dependency-closed `ToolResultObserved` | 可直接准备下一次 completion |
| `TurnFailed` | `CompletionAttemptFailed` | known-failure boundary，可接 observation 或 setup mutation |

以下状态不是 dependency-closed seed：

- `AwaitingCompletionDispatch`；
- `AwaitingCompletion`；
- `AwaitingToolExecution`；
- 含 pending tool / Prepared / attempt 字段的伪造 state。

`Empty + RuntimeConfigSetup` 是合法 raw bootstrap prefix，却不能作为本组合 fold seed：此时
`SystemPromptSetup` 尚未出现，无法构造完整 `SessionGoverningSetup`。不要为了覆盖这个 raw prefix
而允许不完整 setup 进入 request-context fold。

### 3.3 EmptyLineage 与 setup-only genesis

必须区分两个名字相近但不同的边界：

- strict `EmptyLineage` 是 DerivedMemory candidate discovery 状态；
- current raw-only bootstrap 的 `RawStartExclusive` 是 `SessionCreated`；
- 因而 empty-lineage request fold 实际从
  `Idle + SessionCreated` seed 开始，不存在“空 journal fold seed”。

setup-only genesis 是另一个合法 API 场景：

```text
RuntimeConfigSetup -> SystemPromptSetup | seed(Empty)
                                      -> SessionCreated -> ...
```

它应有独立测试，但不能与 empty-lineage bootstrap 混为一谈。

### 3.4 Fold signature 与内部删除项

目标签名接近：

```csharp
internal static TailFoldResult FoldSuffix(
    SessionDependencyClosedFoldSeed seed,
    IReadOnlyList<DecodedSessionEvent> events,
    ICollection<AddressedSessionHistoryMessage>? addressedMessages = null,
    ICollection<SessionHistoryPlanningBoundary>? replaySafeBoundaries = null
)
```

迁移后：

- runtime config / system prompt 从 `seed.GoverningSetup` 初始化；
- `phase`、checkpoint、correlation、prior kind/address 直接从 seed 初始化；
- 删除 `SessionExecutionRecovery?`；
- 删除 `InferSeedPhase(...)`；
- checkpoint 变为 non-null `long`；
- Prepared、Action、ToolStarted 的 checkpoint 比较不再保留“若 seed 存在才比较”的分支；
- `TailFoldResult.ToolExecutionSequenceCheckpoint` 不再用 `?? 0` 兜底。

fold 仍负责：

- 每个 suffix event 的 direct Parent continuity；
- suffix-local Prepared/attempt/action/tool transition；
- context 与 addressed provenance materialization；
- replay-safe planning boundaries；
- final dependency closure。

seed factory 只证明 start boundary，不验证 suffix 内容。

### 3.5 旧 surface 删除与两个 live caller 迁移

#### A. 删除旧 single-candidate path

实现前再次以 repo-wide call graph 核对。若 current 状态不变，删除：

- `SessionTailContextProjection.Materialize(...)`；
- internal `ValidatedSessionContextCandidate`；
- `SessionContextCandidateValidator.Validate(...)`；
- 只服务上述 path 的 raw-interval/helper 代码。

保留并继续使用：

- `SessionTailContextProjectionResult`：current Engine selected-context materialization 仍返回该结果；
- `SessionTailContextProjection.FoldSuffix(...)`；
- `SessionContextCandidateValidator.ValidateMaterializedCandidate(...)` 及 current batch/provider-route
  所需的 contribution normalization。

旧 `SessionContextCandidateContractTests` 中仍有价值的 contribution/hash/shape/lineage assertions 应迁到
current descriptor/materialization/provider-route tests；只验证已删除 internal DTO 的测试一并删除。

#### B. History planning live route

`ReadHistoryPlanningWindowAtCore(...)` 继续允许
`SessionHistoryPlanningSeed.ExecutionRecovery` 为 null：

- `ReadHistoryPlanningSeeds(...)` 的批量 setup scan 不应被迫为每个 candidate 提前做 tail
  recovery；
- 真正读取某一 planning window 时，再补解析 exact recovery；
- 进入 fold 前构造非空组合 seed。

这是“存储/批量 API 可以延迟解析”与“fold contract 不可 nullable”的有意分层。

进入 fold 前用 resolved governing setup + exact recovery 构造组合 seed。

#### C. Prepared v5 reconstruction live route

reconstructor 对 `rawStartExclusive` 解析 recovery 后，必须走同一 seed factory。

这使 Prepared reopen 明确获得与 planning 相同的 replay-safe/start-anchor proof，而不是只依赖
fold 最后与 final recovery 的比较。

### 3.6 D0 tests

新增 focused contract tests：

#### Legal seed matrix

- `SystemPromptSetup / Empty`，suffix 从 `SessionCreated` 开始；
- `SessionCreated / Idle`，empty suffix 与 Observation suffix；
- terminal live/imported Action `/ Idle`；
- setup run after idle `/ Idle`；
- `ObservationAccepted / AwaitingAgentAction`；
- dependency-closed final `ToolResultObserved / AwaitingAgentAction`；
- `CompletionAttemptFailed / TurnFailed`。

#### Rejection matrix

- governing setup head 与 recovery head 不同；
- null recovery head / head kind；
- phase 与 head kind 不匹配；
- negative checkpoint；
- AwaitingCompletionDispatch / AwaitingCompletion / AwaitingToolExecution；
- 任意 pending tool / operation / Prepared / attempt / runtime identity；
- AwaitingAgentAction 缺 correlation；
- Idle / Empty / TurnFailed 错带 correlation。

#### Existing behavior gates

- Prepared v5 exact reconstruction bytes/commitment 不变；
- online selected-candidate materialization 的 canonical request 不变；
- current descriptor/materialization raw-authority coverage 不因删除旧 validator 而下降；
- history planning units/boundaries 不变；
- multi-tool final result 才是 replay-safe boundary；
- empty-lineage bootstrap 仍提交零 exact inputs；
- selected anchor 前 10k+ cold prefix 不增加 payload reads；
- `FullProjectionInvocationCount` 不增加。

### 3.7 D0 完成定义

- 无 production caller 的旧 single-candidate path 已删除，且必要 assertions 已迁到 current tests；
- history planning 与 Prepared reconstruction 两个 live caller 都只传组合 seed；
- `FoldSuffix` 不再接受 nullable recovery；
- `InferSeedPhase` 被删除；
- 没有新增 wire/public API/DerivedMemory dependency；
- focused tests、完整 `SessionJournal.Tests`、solution build 通过；
- reviewer 确认没有把 recovery 提前到批量 seed scan。

## 4. Simplification-D1：共享纯 operational semantics

### 4.1 允许进入共享层的规则

共享层暂名 `SessionEventSemantics` 或 `SessionOperationalSemantics`。它必须：

- internal；
- 无 IO；
- 不读取 EventJournal；
- 不沿 Parent traversal；
- 不访问 ref、store、provider、DerivedMemory；
- 不构造完整 conversation context；
- 只消费 value、decoded body 与已知 bounded state；
- deterministic，同一输入产生同一结果/violation。

第一批允许共享：

```text
kind classification
  IsSetupKind
  IsActionKind
  IsToolSegmentKind

phase classification
  IsReplaySafePhase
  IsIdleOrFailedPhase
  IsPreparedOrAttemptPhase

identity
  BuildObservationCorrelationId

local validators
  ValidateActionToolDeclarations
  ValidateToolRuntimeIdentityShape
  ValidatePendingToolCallMatch
  ValidateReservedToolSequence
  SelectNextPendingDeclaredCall
```

具体方法数应以能删除现有重复为准；不要为了形成“完整 API”加入无人使用的 predicates。

### 4.2 不能伪装成 kind-only predicate 的规则

下列结论依赖 state/body，不能仅凭 `SessionEventKind` 判断：

- Action 是否 terminal：取决于 `ToolCalls.Count == 0`；
- ToolResult 是否 dependency-closed：取决于 Action 声明与已观察 result set；
- setup 是否 reset 到 Idle：取决于 session 是否已经 created；
- ToolResult 是否可成为 completion boundary：取决于所有 calls 是否 settled；
- barrier 是否 replay-safe：取决于 pending Prepared/attempt/tool 字段是否为空。

因此 D1 不应提供名字诱人但语义不足的：

```text
IsTerminalKind(kind)
IsReplayBarrierKind(kind)
IsDependencyClosedKind(kind)
```

如确有调用方需要，应提供 state-aware predicate，并让命名显式包含 `State` / `Boundary`。

### 4.3 Shared validator boundary

共享 validator 负责局部、不依赖 traversal 的事实，例如：

- tool call id/name/raw arguments 非空；
- call id 在同一 Action 内唯一；
- terminal Action 不带 tool runtime identity；
- Action 含 tool calls 时必须带完整 runtime identity；
- started/result 指向当前 pending call；
- started raw arguments 与声明完全一致；
- result sequence 等于 active reserved sequence；
- started sequence 等于 `checked(checkpoint + 1)`；
- next pending call 始终按 Action 声明顺序选择，而不是按 result append 顺序选择。

消费者仍负责：

- `ev.Parent` 是否等于哪个 exact address；
- source Prepared/Started/Observation/Action 在真实 lineage 上是否存在；
- 当前 state 是否允许该 event；
- append CAS 与 uncertain outcome policy；
- context/provenance 输出。

这条边界允许 tail resolver 在收集完 dependency segment 后复用局部 validator，又不会让 validator
拥有 traversal authority。

### 4.4 Internal violation vocabulary

异常自然语言不是 public wire，但 differential tests 需要比较稳定的错误类别。D1 应建立小型
internal vocabulary，初始候选：

```csharp
internal enum SessionOperationalViolationCode {
    InvalidCorrelation,
    CheckpointMismatch,
    InvalidToolDeclaration,
    ToolCallMismatch,
    ToolRuntimeMismatch,
    ToolSequenceMismatch
}
```

只保留实际由共享 validator 或 shared predicate 产生的成员；不要一次枚举所有未来错误。

推荐 pure validator 返回 internal violation value：

```csharp
internal readonly record struct SessionOperationalViolation(
    SessionOperationalViolationCode Code,
    string Detail
);

// 具体实现可用 nullable value、Try-pattern 或等价 result。
ValidateX(...) -> SessionOperationalViolation?
```

消费者收到 violation 后，用共享 renderer/factory 加入 event kind/address/context，并构造原有
`InvalidDataException`。`InvalidDataException` 是 sealed，不能通过继承携带 code。

focused semantics tests 直接比较 violation code；reducer、suffix fold 与 resolver 的 integration
tests 继续比较 exception category 与 fail-fast，不把整句 message 或 exception metadata 变成合同。

必须保留的设计要求：

1. code 是 internal；
2. code 只覆盖共享规则；
3. 不改变现有 public exception category；
4. 不把 message 变成 wire/compatibility contract。
5. Parent/predecessor proof、bootstrap、final unclosed-dependency 等 consumer-owned 错误不进入这份
   local vocabulary。

### 4.5 D1 consumers

#### `SessionReducer`

- 复用 kind/phase classification、correlation、Action/tool local validators；
- 保留 full audit state、context projection 与 bootstrap tracking；
- 不为了共享代码而削弱 root-to-head strictness。

#### `SessionTailContextProjection`

- 复用同一 local validators；
- 保留组合 seed、suffix-local state、context units 与 final dependency-closed gate；
- 不让 pure semantics 读取 governing setup 或 artifact snapshots。

#### `SessionExecutionTailResolver`

- reverse dependency discovery、Prepared attempt chain、Action source validation继续独立；
- 收集到 Action + tool segment 后可复用 declaration/match/sequence validators；
- imported Action 的 bounded cut 保持原样；
- 不为了共享 fold 而扩大 header/payload reads。

#### `SessionJournalEngine`

- correlation identity 等纯 helper 可以复用；
- phase routing 只有在能删除真实重复时才迁移；
- 不在 D1 顺手拆 coordinator/driver。

#### 明确不是消费者

- `SessionJournalLegacyImporter`：验证 legacy export grammar；
- `SessionJournalOfflineValidator`：编排 full audit、Prepared reconstruction 与 tail differential；
- DerivedMemory/Maintainers：不解释 raw execution transition。

### 4.6 D1 differential matrix

建立 table/fixture-driven reference matrix。每个 legal scenario 同时提供：

```text
full prefix + suffix -> SessionReducer oracle
replay-safe cut + suffix -> suffix fold
exact final head -> SessionExecutionTailResolver
```

比较共同 observable：

- final head kind；
- phase；
- tool execution sequence checkpoint；
- active correlation id；
- pending tool call/runtime/operation（当 final head 允许 pending 时，仅 reducer/resolver 比较）；
- legal replay-safe boundary classification。

合法矩阵至少覆盖：

- setup-only genesis -> SessionCreated；
- SessionCreated -> Observation；
- Observation -> Prepared -> one/multiple Started -> terminal Action；
- Observation -> ImportedAction；
- Action -> single-tool start/result；
- Action -> multi-tool partial result -> next start -> final result；
- settled ToolResult -> continuation Prepared；
- Started -> known Failure；
- TurnFailed -> Observation；
- terminal Action -> setup run。

非法矩阵至少覆盖：

- wrong Parent / attempt chain；
- wrong correlation；
- wrong execution checkpoint；
- missing/extra tool runtime identity；
- duplicate call id；
- result before start；
- duplicate start/result；
- out-of-order tool call；
- raw argument mismatch；
- reserved sequence gap/repeat；
- setup during pending Prepared/tool；
- suffix 结束时仍有 open tool dependency。

focused validator tests 对共享规则比较 violation code；三种 consumer 的 integration tests
断言正确 exception category 与 fail-fast。对 traversal-specific、bootstrap 或 final closure
错误不要求统一 code。

### 4.7 D1 performance gates

- online `FullProjectionInvocationCount` 不增加；
- `SessionExecutionTailResolver` 不增加 chronological chain read；
- selected anchor 前添加 10k+ cold prefix，不增加 payload reads / decoded bytes；
- Prepared/Started exact reopen 不访问 DerivedMemory；
- branch/rewind 只读取 exact Parent lineage；
- pure helper 不引入 payload copies 或完整 manifest/context cache。

## 5. 实施工作包（历史执行计划）

### D0：Fold seed contract

D0 应保留两个独立、可单独 review 的提交：

- **D0a**：删除无 production caller 的 single-candidate path，并把必要 raw-authority tests
  迁到 current route；
- **D0b**：引入组合 seed、迁移两个 live fold caller、删除 nullable/inference branches。

不要把 D0a/D0b 压成一个难以判断“测试删除是否合理”的大提交。

主要文件：

- `prototypes/SessionJournal/SessionTailContextProjection.cs`
- 建议新增 `prototypes/SessionJournal/SessionDependencyClosedFoldSeed.cs`
- `prototypes/SessionJournal/SessionContextCandidateValidator.cs`
- `prototypes/SessionJournal/SessionContextCandidateContracts.cs`
- `prototypes/SessionJournal/SessionJournalEngine.cs`
- `prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs`
- `tests/SessionJournal.Tests/SessionContextCandidateContractTests.cs`
- `tests/SessionJournal.Tests/SessionContextCandidateProviderRouteTests.cs`
- `tests/SessionJournal.Tests/SessionTailContextProjectionTests.cs`
- `tests/SessionJournal.Tests/SessionHistoryPlanningTests.cs`
- `tests/SessionJournal.Tests/SessionPreparedRequestReconstructorTests.cs`

实施顺序：

1. 再次证明旧 single-candidate path 无 production caller，删除旧 path，并迁移必要 tests。
2. 建立组合 seed 与 factory invariants。
3. 为合法/非法 seed matrix 建 focused tests。
4. 一次迁移两个 live caller 与 fold signature。
5. 删除 nullable/checkpoint fallback/`InferSeedPhase`。
6. 运行 differential、performance 与 full test gates。
7. 独立 reviewer 检查 dead-surface coverage、anchor binding、批量 planning reads 与 Prepared exact
   reopen。

### D1a：Classification 与 correlation

主要文件：

- 建议新增 `prototypes/SessionJournal/SessionOperationalSemantics.cs`
- `SessionReducer.cs`
- `SessionTailContextProjection.cs`
- `SessionExecutionTailResolver.cs`
- `SessionJournalEngine.cs`
- 新增 focused semantics tests。

完成标准：

- 只提取已有、稳定、至少两个消费者使用的 pure rules；
- 删除原重复 helper；
- 不改变 exception category、read diagnostics 或 public surface；
- 不出现 mode flag 或 consumer-specific callback。

### D1b：Action/tool validators 与 violation vocabulary

实施顺序：

1. 先提取 Action declaration 与 pending-call match。
2. 再提取 runtime identity / sequence validators。
3. 为这些共享规则加入最小 violation codes。
4. 用共享 renderer/factory 保持消费者继续抛 `InvalidDataException`。
5. 逐消费者迁移；一次只迁移一个规则簇。
6. reviewer 检查 code 是否真的代表同一语义，而不是用同名掩盖不同 predecessor proof。

### D1c：Differential semantic matrix

- 优先复用现有 controlled writer fixtures 和 full reducer oracle；
- 新增共享 scenario builder 时，不绕过 `SessionEventCodec` 的 wire validation；
- malformed raw fixtures 可以直接 commit，但要明确它们是在测试 operational validation，而非 codec；
- 对 legal state 做 exact equality；
- focused pure-validator tests 比较 shared violation code，consumer integration tests 比较
  `InvalidDataException` 与 fail-fast；
- 同时冻结 read diagnostics。

## 6. D2 go/no-go

### 6.1 2026-07-28 决策：Deferred / NO-GO

当前**不实施**：

```text
SessionOperationalFold
  seed + one decoded event -> next bounded operational state + semantic effects
```

这不是对 pure forward fold 的永久否决，而是当前 hard prerequisites 未满足：

| 前置 | 复核结果 | 结论 |
| --- | --- | --- |
| D1 建立真实 single source | classification、correlation、Action/tool local validators 已共享 | 通过 |
| differential matrix 可捕获 drift | 19 legal + 17 illegal matrix 已落地，且冻结 reads/provider gates | 通过 |
| 更大 kernel 能降低总体复杂性 | production 相对基线按 `git numstat` 粗指标净增 98 行；行数不是复杂度本身，共享收益仍与 adapter/renderer 跳转成本并存，尚无“显著删分支”的实证 | 未通过 |
| shared state 已证明小而稳定 | 可列出 bounded state，但它已横跨 Prepared、attempt、Action/tool 与 provenance boundary，adapter 形状尚未证明无需 consumer mode | 未通过 |
| candidate A/A0 measurement 完成 | repo 中只有 measurement 要求，没有 benchmark/report/data；DM lifecycle、10k cold-prefix 与 exact reopen tests 不是 snapshot measurement | **hard blocker** |
| reverse resolver 保持 bounded traversal | D1 后 reads 未回归；尚未对更大 forward kernel 做 production spike | 尚未被 D2 改动 |

### 6.2 若做更大 fold，至少需要的 bounded state

Action/tool-only kernel 已不只是 D1 的 context-free validator。为了同时服务三种消费者，至少需要：

- current head kind/address、phase、checkpoint、active correlation；
- source Prepared 的 bounded summary（origin、execution、tool runtime identity）与 Prepared address；
- active provider attempt address；
- open Action 与声明顺序、observed result map、next pending call/index；
- pending operation id、Started flag、reserved sequence、tool runtime identity；
- source Observation/Action/latest checkpoint 等 recovery boundary provenance；
- prior kind/address，以便 forward caller证明 direct Parent continuity。

这些状态只在单个 in-flight Prepared/Action/tool segment 内有界，并非 fixed-size / O(1)；它们不等于
完整 conversation projection，也不需要 governing setup value、DerivedMemory 或无界 history
context。然而它们已经接近一份完整的 **operational** projection。full reducer
需要 context/provenance effects，suffix fold 需要 planning boundaries 与 final dependency closure，
reverse resolver 则先做 dependency discovery 再验证 gathered segment。若 kernel 为这些差异引入
`Full` / `Suffix` / `Resolver` mode、consumer callback 或大批 adapter，化简目标即告失败。

### 6.3 下一次解锁条件

D2 只有同时满足以下条件才重新评估：

1. 完成候选 A/A0 measurement，至少产出 canonical request、current Prepared v5 与 hypothetical
   snapshot 的 logical/stored bytes、100/1,000 requests 累计 amplification，以及 reopen
   reads/decoded bytes/peak live bytes；不得以 DM lifecycle 或 cold-prefix test 代替。
2. A0 数据表明 recipe-authoritative Prepared reconstruction 仍是长期主线；若转向
   snapshot-authoritative，先重画长期 consumer graph。
3. 在非 production spike brief 中逐字段冻结上述 bounded state，并证明不需要 consumer mode、
   traversal callback 或完整 projection。
4. 对 Action/tool segment 给出可审阅的 before/after 预算：删除的 production branches/helpers
   必须明显多于新增 kernel、adapter 与 renderer 跳转。
5. spike 必须保持 resolver chronological reads 为零、bounded payload reads 不增加，并继续让
   full audit、suffix output 与 reverse traversal 独立。

满足这些条件后仍只优先 spike Action/tool segment；不能直接扩到 Prepared/attempt 全状态机。

出现以下任一情况就停止 D2：

- shared state 退化成完整 `SessionProjection`；
- 需要 `Full` / `Suffix` / `TailResolver` mode flag；
- pure fold 开始拥有 EventJournal reader 或 Parent traversal；
- imported Action bounded cut 被取消；
- 为统一错误文本引入大量 adapter；
- 代码跳转与抽象数量超过实际删除的重复分支。

## 7. Review checklist

### Architecture

- [x] raw Parent chain 仍是 execution correctness authority。
- [x] SessionJournal 不依赖 DerivedMemory、Maintainers 或 Agent.Core。
- [x] reverse traversal 与 forward semantics 分离。
- [x] full audit、tail recovery、request context 三类 projection 仍然独立。

### D0

- [x] governing setup 与 recovery head exact 相等。
- [x] 已删除的 single-candidate path 确实没有 production caller，且 raw-authority coverage 已迁移。
- [x] seed phase/head-kind/correlation/pending-state matrix 被验证。
- [x] empty-lineage bootstrap 与 setup-only genesis 没有混淆。
- [x] `FoldSuffix` 没有 nullable execution seed。
- [x] `InferSeedPhase` 与 checkpoint fallback 已删除。
- [x] batch planning seed 没有新增 eager tail resolution。

### D1

- [x] 只共享 pure、local、deterministic semantics。
- [x] state-dependent 规则没有伪装成 kind-only predicate。
- [x] violation code 为 internal，public exception category 不变。
- [x] importer/offline validator 没有被错误并入 forward state machine。
- [x] differential matrix 同时覆盖合法与非法 transition。

### Performance / recovery

- [x] `FullProjectionInvocationCount` 不增加。
- [x] 10k+ cold prefix 不增加 bounded-path payload reads。
- [x] Prepared/Started reopen 不访问 DerivedMemory。
- [x] exact-head CAS 与 uncertain policy 不变。
- [x] branch/rewind 仍只沿真实 Parent lineage。

## 8. 给后续 Coding Agent 的一句话

D0/D1 已把 suffix fold 起点变成 anchor-bound typed fact，并共享了确实相同的局部规则。现在不要
继续写 `SessionOperationalFold`：先完成 A0 measurement，再用可量化的净删减、无 consumer mode
与 reads 不回归证据重新申请 D2。
