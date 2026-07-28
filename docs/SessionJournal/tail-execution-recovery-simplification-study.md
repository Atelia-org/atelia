# SessionJournal Tail Execution Recovery 后续化简候选

> **状态**：Current Research / CS-3D7 + DM-0～DM-8 后候选集
> **日期**：2026-07-28
> **当前基线**：
> [Tail-only Execution Recovery Design](tail-execution-recovery-design.md)、
> [DerivedMemory 实施方案](derived-memory-subsystem-implementation-plan.md)
> **已完成计划**：
> [CS-3D6：Coherent-only Request Manifest](done/coherent-request-manifest-simplification-plan.md)、
> [CS-3D7：Prepared / Provider Attempt 对称化](done/prepared-provider-attempt-symmetry-design.md)
> **已批准待实施**：
> [候选 D0/D1：Dependency-closed Fold Seed 与共享 Operational Semantics](tail-operational-semantics-simplification-plan.md)
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
2. **候选 D：D0/D1 已完成具体设计，等待分片实施；D2 仍须 go/no-go**；
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

本候选的 D0/D1 已完成代码核对与具体设计，实施合同、边界矩阵、工作包与 review checklist 统一见
[Dependency-closed Fold Seed 与共享 Operational Semantics 实施计划](tail-operational-semantics-simplification-plan.md)。

保留在本调研中的结论只有：

- D0 先删除无 production caller 的旧 single-candidate validation/materialization path，再删除
  nullable fold seed、不可达 `InferSeedPhase` 与 checkpoint fallback，并把 governing setup 与
  exact recovery 绑定到同一 raw anchor；
- D1 只共享无 IO、无 traversal 的 kind/phase classification、correlation identity 与局部
  Action/tool validators；
- terminal Action、dependency-closed ToolResult 与 replay-safe barrier 都依赖 state/body，不能简化为
  kind-only predicate；
- Legacy importer 验证旧 export grammar，不属于 current raw operational semantics；offline
  validator 编排 full reducer 与 tail resolver，也不是第四套正向状态机；
- full reducer、suffix projector 与 reverse tail resolver 继续分别拥有 traversal authority；
- D2 的 pure operational fold 只在 D0/D1 与 differential matrix 完成后重新决策，优先 spike
  Action/tool segment，不默认全面实施。

候选 A 的 measurement 不是 D0/D1 的技术前置条件，但必须在 D2 go/no-go 前完成，因为
snapshot-authoritative Prepared 可能改变 request reconstruction 对 forward fold 的长期需求。

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

但这不是当前优先切片。若在语义仍重复时先拆文件，只会把耦合变成跨类跳转。应先完成候选 D0/D1，
再结合候选 A 数据与 D2 go/no-go 得到稳定调用图，之后才拆 Engine；public surface 暂不改变。

## 5. 推荐研究顺序

1. **D0：显式 dependency-closed fold seed**：按已批准计划删除 nullable seed /
   `InferSeedPhase`，独立 review + boundary matrix tests；
2. **D1：pure classification、local validators、internal violation vocabulary 与 differential
   matrix**：按已批准计划分小包实施；
3. **A0：Prepared v5 snapshot measurement spike**：可与 D0/D1 独立安排，但须在 D2 前完成；
4. **D2：pure operational fold 可行性**：仅在 D1 显示真实收益、且 A0 数据支持时推进；
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
