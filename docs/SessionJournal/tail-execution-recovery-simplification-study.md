# SessionJournal Tail Execution Recovery 后续化简候选

> **状态**：Current Research / CS-3D7 + DM-0～DM-8 后候选集
> **日期**：2026-07-28
> **当前基线**：
> [Tail-only Execution Recovery Design](tail-execution-recovery-design.md)、
> [DerivedMemory 实施方案](derived-memory-subsystem-implementation-plan.md)
> **已完成计划**：
> [CS-3D6：Coherent-only Request Manifest](done/coherent-request-manifest-simplification-plan.md)、
> [CS-3D7：Prepared / Provider Attempt 对称化](done/prepared-provider-attempt-symmetry-design.md)
> **目标**：只保留 current trunk 上尚未实施的化简候选；不以牺牲 crash recovery、raw
> provenance、exact reopen 或 bounded reads 换取表面简洁。

## 0. 当前结论

CS-3D6/D7 与 DM-0～DM-8 已完成以下收口：

- current wire 是 self-contained `CompletionRequestPrepared` v5 +
  `CompletionAttemptStarted`；
- raw Parent chain 不含 ArtifactSet definition/activation，DerivedMemory 只单向引用 raw；
- candidate contract 是 bounded two-phase discovery/materialization；
- shared epoch、parallel role settlement、ArtifactSet publication、online lifecycle、
  strict empty-lineage bootstrap 与 budgeted selection 已实施；
- online execution 不调用 `Project()`；Prepared/Started exact reopen 不打开 DerivedMemory。

已采纳的 Prepared/attempt 对称化与 ArtifactSet/raw-core 解耦不再作为候选展开。后者的最终设计、
迁移与验收统一见
[DerivedMemory 实施方案](derived-memory-subsystem-implementation-plan.md)。

当前仍值得研究的只有：

1. **候选 A：Exact request snapshot spike**；
2. **候选 D：显式 dependency-closed fold seed，再评估共享正向 semantics**；
3. **候选 E：Engine 职责拆分**，但只在 A/D 稳定后推进。

明确不再考虑：

- 合并 `RuntimeConfigSetup` / `SystemPromptSetup`；
- legacy `full-raw`、`explicit-artifact-tail` 或 silent/unbounded raw fallback；
- 为旧 Prepared 增加 compatibility decoder、缺省字段推断或 root replay fallback；
- 用完整 `SessionProjection` cache 替代 tail recovery；
- 把 reverse tail collector 合并进 forward full reducer。

current 唯一 raw-only bootstrap 是 strict `EmptyLineage` + 显式
`BootstrapRawSuffixTokenBudget`；它提交 Prepared v5 零 inputs，并在首个真实 set 发布后自动失效，
不属于 legacy fallback。

## 1. 不可删除的复杂度

以下复杂度来自外部副作用与 event-sourced correctness，不能用重命名或大类合并消除：

- Prepared 与 Started 分离：request origin 不等于物理 provider attempt；
- uncertain outcome：Started 后 transport failure 不能伪造成 known failure；
- exact Parent/CAS：Action、Failure、ToolStarted、ToolResult 必须继承正确 attempt/operation；
- paired sticky setup：runtime config 与 system prompt 独立演进；
- dependency-closed suffix：tool result 必须闭包到声明它的 Action；
- durable tool implementation/capability identity；
- full audit oracle 与 bounded online recovery 分离；
- untrusted import 的 strict O(raw inventory) offline validation。

任何化简都必须维持这些不变量。

## 2. 候选 A：Exact request snapshot spike

### 2.1 Current Prepared v5 成本

Prepared v5 不引用 derived identity，但 exact reopen 仍需要：

1. 读取并验证 paired setup payload；
2. 读取 exact raw range；
3. seed 并运行 dependency-closed suffix fold；
4. 聚合零个或多个 inline exact context snapshots；
5. 重建 canonical request 并核对 commitment。

这条路径正确且 bounded。待验证的问题是：直接保存 canonical request bytes，能否显著减少 reopen
逻辑与读取成本，而不会造成不可接受的 raw amplification。

### 2.2 Spike 对照

| 方案 | Online reopen | 主要代价 |
| --- | --- | --- |
| R：current v5 recipe-authoritative | 读取 refs/range、fold、render、校验 commitment | renderer/fold 长期属于恢复合同 |
| S：snapshot-authoritative | 读取 canonical bytes、校验 hash、decode request | 每个 Prepared 复制 bounded request bytes |

S 的 provenance 仍须保存 governing setup refs、raw range 与 exact context input hashes；不能只存
一段不透明 bytes 而失去 offline audit。

### 2.3 决策数据

只做 measurement spike，不先改 wire。至少比较：

- canonical request logical bytes；
- current Prepared v5 logical/stored bytes；
- snapshot logical/stored bytes；
- 100 / 1,000 requests 的累计 raw amplification；
- reopen header visits、payload reads、decoded bytes 与 peak live bytes；
- EventJournal 压缩后重复 system prompt、context snapshots、tools 和 suffix 的实际增量。

该 spike 不同时修改 attempt state machine、ArtifactSet selection 或 tool protocol。

## 3. 候选 D：共享正向 operational semantics

### 3.1 仍存在的复述

full reducer、dependency-closed suffix fold、online boundary validator 与部分 importer checks 都会解释：

- event kind 对 phase 的转移；
- Action/tool-call declaration；
- ToolStarted/ToolResult 的 sequence、correlation 与 completion；
- setup/config event 对 open tool state 的影响；
- terminal Action/Failure 的合法 predecessor。

重复规则增加漂移风险，但 reverse tail collector 的依赖发现方向与 forward fold 不同，不应强行合并。

### 3.2 已证实的低风险切口：显式 `DependencyClosedSeed`

current `SessionTailContextProjection.FoldSuffix(...)` 接受 nullable
`SessionExecutionRecovery? executionSeed`，并在缺失时通过 private
`InferSeedPhase(headKind)` 猜测初始 phase。实际 fold 只读取 recovery 的一个很小子集：

- seed head / head kind；
- exact execution phase；
- tool execution sequence checkpoint；
- active correlation id。

这暴露了一个仍未实施、且比“大一统 fold”更独立的化简机会：定义一个显式、不可空、已通过
dependency closure 验证的 `DependencyClosedSeed`（最终命名由具体设计确认），由调用方在进入 fold
前构造。`FoldSuffix` 只消费这份 bounded seed，不再同时承担“没有 recovery 时如何猜 phase”的策略。

预期收益：

- 删除 nullable execution seed 与 `InferSeedPhase` 这条隐式双路径；
- 让 planner materialization 与 Prepared v5 reconstruction 共用同一种 fold 前置条件；
- 把 dependency closure / replay-safe boundary 的证明留在 resolver/caller，fold 只做确定性正向转移；
- 为 differential tests 提供可直接构造、字段最小的 seed contract。

实施前须逐个核对 empty-lineage、setup-only genesis、Observation 与 dependency-closed ToolResult 四类合法
边界，不能用默认 phase 掩盖缺失证明；这项切口不改变 wire、traversal authority 或 public API。

### 3.3 后续边界

先提取无 IO、无 traversal、无 storage 的纯语义小核：

```text
SessionEventSemantics
  - kind classification
  - terminal/reset/barrier predicates
  - local transition validation

SessionOperationalFold
  - seed + one decoded event -> next bounded operational state
```

消费者继续各自拥有 traversal：

- `SessionReducer`：root-to-head full audit；
- suffix projector：seeded forward range；
- `SessionExecutionTailResolver`：head-to-root dependency collection；
- offline validator：untrusted inventory/read boundary。

### 3.4 后续小切口

完成显式 seed 后，再考虑共享稳定、无上下文的 classification 与 error vocabulary；随后用
differential tests 证明 full reducer、suffix fold 与 tail resolver 的合法/非法矩阵不变。不要把
classification/error vocabulary 与 `DependencyClosedSeed` 塞进同一提交，也不要直接发起覆盖所有
event kind 的“大一统 fold”重写。

## 4. 候选 E：Engine 职责拆分

`SessionJournalEngine` 仍同时承担：

- raw append/CAS；
- execution tail routing；
- context candidate evaluation；
- request preparation/reconstruction；
- provider/tool driver；
- public audit helpers。

可考虑的内部组件边界：

```text
SessionRawAppender
SessionExecutionRecoveryCoordinator
SessionContextPreparationCoordinator
SessionCompletionDriver
```

但这不是当前优先切片。若在语义仍重复时先拆文件，只会把耦合变成跨类跳转。应先完成候选 A 的数据
决策与候选 D 的纯语义小核，再按稳定调用图拆 Engine；public surface 暂不改变。

## 5. 推荐研究顺序

1. **A0：Prepared v5 snapshot measurement spike**：只产数据与结论；
2. **D0：显式 `DependencyClosedSeed`**：删除 nullable seed / `InferSeedPhase`，独立 review +
   boundary matrix tests；
3. **D1：classification/error vocabulary**：仅在 D0 后独立评估与实施；
4. **D2：pure operational fold 可行性**：仅在 D1 显示真实收益时推进；
5. **E：Engine split**：最后按稳定语义边界实施。

Prepared/attempt 对称化、ArtifactSet/raw 解耦、shared epoch、online lifecycle 与 budgeted selection
均已完成，不再列入未来 P2/P3。

## 6. 共同验收闸门

任何后续化简都必须证明：

- legal fixtures 与 full reducer oracle 的 execution state 一致；
- `Project()` / `ReplayHistory()` 保留 full semantics；
- online route 的 `FullProjectionInvocationCount` 不增加；
- selected anchor 前 10k+ cold prefix 不增加 payload reads；
- Prepared/Started exact reopen 不访问 DerivedMemory；
- branch/rewind 只沿真实 Parent lineage；
- wrong Parent/attempt/correlation/tool sequence fail-fast；
- exact-head CAS 与 uncertain recovery policy 不变；
- SessionJournal 不反向依赖 DerivedMemory、Maintainers 或 Agent.Core；
- wire 变化采用 direct cut + offline import/rebuild，不引入 silent compatibility。

## 7. 给后续 Coding Agent 的决策原则

- 先指出要删除的具体重复状态、分支或读取，再提出 abstraction。
- 不能把 bounded raw authority validation 外包给 provider/cache。
- 不能把 ordinal 当 token cost，不能把 raw suffix cost冒充 total request cost。
- derived state 可删除重建；Prepared 是已经影响外部调用的 raw execution fact。
- online 与 offline correctness path 可以共享 pure semantics，但不能共享 traversal authority。
- 发现文档与 current wire 不一致时，先修 current-state header，再保留明确标注的历史段。
