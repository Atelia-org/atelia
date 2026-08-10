# DerivedRecap Grid WP-06：Completion Runtime、Family 与 Shared Prefix

状态：Planned；依赖 WP-04 runtime seam，WP-05不依赖本包实现

只需加载：目标设计、总计划、WP-04 runtime handoff、本文和 WP-07A摘要。

## Intent

把真实Completion适配到`IRecapCellBatchExecutor.ExecuteAsync(FrozenRowBatch)`，保留同row并行、family结构共享与
prefix-cache优化，同时证明
provider/runtime信息不会污染Timeline、Recipe、Cell identity或Store。

## In scope

- immutable FamilyDefinition owning SystemPrompt、OrderedTools、OutputProtocol、InputRenderingProtocol；
- declarative MaintainerDefinition -> bound runtime；
- exact family/lane reference affinity与lazy route resolution；
- shared prefix一次构造、leader/follower scheduling、per-lane cap；
- strict output parser：`Updated | KeepUnchanged`；
- drain后按ordered EvaluationKey返回closed per-item outcomes；单item failure/cancel保留successful siblings，deterministic primary
  只影响operation报告，不丢失结果；
- call budgets、timeouts、caller cancellation、started siblings drain；
- cache hint/usage/call-log telemetry与Cell identity分离；
- dynamic Agent-created topic/spec只能进入family允许的user/data tail。
- row首call前pre-resolve全部pending bindings；结果按EvaluationKey返回，等待remote期间不持SQLite transaction；
- identity只消费WP-02 canonical Family/Maintainer hashers，删除current runtime内第二套Semantic/Capability fingerprint算法；
- `InputRenderingProtocol`与PriorInputProjection的字段、顺序、canonical preimage同源。

## Reuse guidance

可以复用 current Completion abstractions/provider adapters与已经验证的lane/family算法；不得让 old
`RecapEpoch`/`RecapBlockPlan`/Published types进入新runtime contract。若机械复用需要大量adapter，优先提取provider-neutral
primitive，而不是建立长期legacy bridge。

## Out of scope

- Store schema、Timeline、ControlPlane carrier；
-改变EvaluationKey；
- Galatea production cutover；
- provider-specific payload成为durable Cell authority；
- scheduler batch/lease持久化；
- warm-up-only remote call（首版仍leader real call后followers）。

## Write scope

- new runtime adapter/family owners与tests；
- current generic Completion library仅做必要provider-neutral seam；
- call-log schema若变化必须独立version并更新goldens；
- 不改 old DerivedRecap production composition。

## Validation matrix

1. same family exact shared SystemPrompt/tools/output protocol实例；
2. two same-group cells：1 Leader + followers，prefix bytes/fingerprint exact相同、tail不同；
3. different lane/connection天然并行隔离；
4. leaders admission优先、followers不抢占未seed family；
5. provider output missing/duplicate/wrong tool/unknown fields fail closed；
6. Updated/Keep正确转换Cell success；
7. call budget在首dispatch前完整preflight；
8. cancellation/timeout/failure选lowest ordinal primary并drain started siblings；
9. NoBuild/missing-free read path零client/lane/logger construction；
10. request/cache/call logs不进入Definition/EvaluationKey/CellDigest；
11. Anthropic/OpenAI/Gemini provider projection focused regressions；
12. optional disposable provider canary不重试并诚实记录environment-blocked。
13. provider-neutral frozen input的ProjectionDigest preimage与actual runtime renderer逐字段同源，无第二套reorder/pack；
14. Agent新column按allow-listed Family/semantic runtime key解析route，不要求config枚举每个LogicalColumnId；exact route缺失拒绝；
15. fake batch与real batch产生相同indexed success/failure contract，Manager不感知leader/follower。

## No-Go

- member覆盖family SystemPrompt/tools/parser；
- Maintainer直接持有ICompletionClient/model/options；
- route fallback或default connection；
- implicit provider retry突破call budget；
-为了cache把runtime group/family ID写入durable identity。

## Done when

- fake runtime与real adapter对同一Manager contract；
- parallel/cache/cancel/error tests green；
- affected Completion builds/tests、docs/diff green；
- reviewer确认WP-07A/07B composition只做route/control wiring。

## Handoff to WP-07A

交付host-level registry、deferred binding、call-log evidence seam与disposable fake provider harness。
