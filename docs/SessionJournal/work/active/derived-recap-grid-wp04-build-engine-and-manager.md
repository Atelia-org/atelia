# DerivedRecap Grid WP-04：Grid Build Engine 与 MaintainerManager

状态：Planned；依赖 WP-01/02/03

只需加载：目标设计、总计划、WP-03 handoff、本文和 WP-05 摘要。

## Intent

用 deterministic fake runtime 首次闭合Timeline + ControlPlane + GridStore：派生唯一RowBuildSpec、计算missing assignments、
逐row wavefront执行并提交Cell/RowView。先证明分析语义，再接真实Completion。

## In scope

- pure recipe-to-RowBuildSpec derivation；
- exact prior content projection与EvaluationKey；
- single-column overlay bootstrap；
- full-grid rebuild；
- prompt/definition A-B candidate；
- exact reuse、AlreadyFilled与Maintainer `KeepUnchanged`；
- row内并行接口与row间barrier；
- operation call/row/time budgets、cancellation与typed failure；
- progress完全由Store missing query恢复；
- operation开始冻结`{whole TimelineHeadRef, canonical recipe+definitions, through row}`；whole head包括Timeline/Ref/head row/
  active policy/selected raw fence/generation，RowBuildSpec直接携带typed `HistorySegmentDescriptorDigest`；
- operation的Timeline输入只来自WP-01C Reader snapshot/path/witness；composition可持有factory handle，但Manager不得接触
  Coordinator、SQLite、ledger port或自行从global End/ordinal解析row；
- candidate catch-up与可promote proof；active recipe CAS是显式后续control operation，不由Manager自动完成；
- 同一Send多个safe lifecycle boundary下的幂等re-entry。

## Runtime seam

本包只定义/使用一个row-batch接口：

```text
IRecapCellBatchExecutor.ExecuteAsync(FrozenRowBatch, CancellationToken)
  -> results indexed by EvaluationKey
```

`FrozenRowBatch`包含exact frozen recipe/row/projection和ordered missing work。fake executor确定性、可阻塞、可失败，用于证明
Manager的wavefront、row barrier、budget和commit；它可以内部模拟overlap，但Manager不实现family/lane/leader-follower scheduler。
WP-06是这一接口的唯一真实调度实现。不得在本包引用provider request、connection、lane、prefix cache或call-log types。
executor drain后必须为每个ordered work item返回closed outcome：`Updated | KeepUnchanged | Failed |
NotStartedDueToCallerCancellation`。只有首dispatch前global preflight/caller cancellation可整体返回且保证zero-started；一个
item throw/cancel不得抹掉已成功siblings。

## Core invariants

- 同row assignments看到同一HistorySegment与PriorInputProjection；
- overlay bootstrap只算RecomputedColumns，其余exact reuse base same-row cells；
- overlay追平后的新row与full recipe都assign全部target columns；
- 下一row只在上一RowView exact committed后开始；
- first successful EvaluationKey winner唯一；
- `KeepUnchanged`在first row或该column无exact prior正文时非法；成功时复制prior正文，因此相同ContentDigest是确定事实；
- partial candidate不改变active recipe/fulfilled main view；
- remote/fake evaluation期间无SQLite transaction。
- 每row首个dispatch前，必须证明整个predictable missing batch能容纳于剩余call budget；provider sibling成功可提交Cell，但
  budget不足不得人为制造partial row。
- explicit candidate build不因active pointer后来变化而中止；promotion用expected-active CAS处理。live-fill则绑定开始时的
  exact active recipe snapshot。

## Out of scope

- real Completion family/tool parser/cache/connection；
- ContextComposer/raw tail；
- Galatea/CLI production cut；
- durable Campaign/Attempt/Lease/Settlement；
- automatic dependency subsets或same-row convergence。

## Write scope

- new `SessionJournal.RecapGrid.Manager`/engine owner与tests；
- Abstractions中只增加必要 frozen input/success/failure contracts；
- deterministic fake runtime/test fixtures；
- 不修改 old Planner/Runtime production behavior。

## Validation matrix

1. empty Timeline/zero columns；
2. first row与multi-row normal fill；
3. add C overlay：A/B bytes不变，C顺序catch-up，active不提前切；
4. full-grid：batch内A/B/C overlap，下一row等待完整previous view；
5. A-v1/v2 coexist、candidate compare、CAS promotion conflict；
6. same visible projection/different previous view零调用复用；
7. changed definition/order/content产生新call；Keep生成新Cell且ContentDigest exact等于prior；无prior Keep拒绝；
8. crash/restart从missing query继续，仅pending assignments调用；
9. one sibling failure：drain started siblings、无partial RowView；
10. caller cancel与budget preflight零超额dispatch；
11. branch/head/recipe stale在首call前fail closed；
    Timeline handle已dispose、Reader Busy/Invalid、selected-path root/snapshot corruption、whole-head Stale或witness不属于exact frozen
    whole head也必须在首call前typed fail closed；
12. mystery fixture：`XSuspicion` overlay、future interaction、full rebuild retroactive wavefront。
13. Idle/pre-observation、ObservationAccepted、ToolResultObserved重复进入均幂等；非replay-safe边界零row commit；
14. build fulfilled后先返回promotable proof；ControlPlane CAS crash/conflict不会污染active view，active暂时unfulfilled时Getter fail closed。

## No-Go

- Manager持久化operation状态机；
- Store决定overlay/full；
- fake runtime contract泄入provider模型；
- partial RowView成为active；
- 为测试方便允许same-row cell dependency。

## Done when

- fake-runtime全语义矩阵green；
- state/API/operation-result ledger受控；
- builds/docs/diff green；
- reviewer确认 WP-05/06 可以分别接pure read与runtime adapter，不需要改变durable identities。

## Handoff to WP-05

交付Manager operation results、fulfilled-view semantics、mystery fixture与active/candidate read seam。
