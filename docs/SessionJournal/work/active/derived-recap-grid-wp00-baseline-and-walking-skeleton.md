# DerivedRecap Grid WP-00：Baseline、迁移账本与 Walking Skeleton

状态：Planned；前置仅为目标设计与总施工计划

上游：[`Grid target`](derived-recap-grid-target-design.md) · [`Master plan`](derived-recap-grid-rewrite-master-plan.md)

## Intent

冻结可复查的旧世界，建立新架构的 assembly/contract 边界和一条纯内存 executable skeleton；不改变 current production。
本包消除“先写大量底层代码，最后才发现 identity 无法首尾闭合”的风险。

## In scope

- fresh baseline：HEAD、branches、projects、production callers、durable paths、config、CLI、tests；
- 建立冻结 branch/tag 与 rewrite integration branch；
- 建立 `DRGRID-CUTOVER` migration ledger；
- 将旧 tests 分类为 Preserve/Rewrite/Delete；
- 锁定新 assembly dependency graph、namespace 与项目名；
- 建立纯内存 fixture：raw descriptor -> recipe -> projection -> EvaluationKey -> fake cell -> RowView -> context value；
- architecture boundary tests，禁止Timeline反向依赖Maintainer/Grid/Completion runtime/provider。

## Out of scope

- durable Timeline backend；
- SQLite dependency/schema；
- real Completion call、parallel scheduler或Galatea wiring；
- 删除、注释或改写旧 production callers；
- v8/v9 data migration。

## Candidate write scope

- `docs/SessionJournal/work/active/` 下本 program 的 migration/handoff 文档；
- 新 contracts/test-only skeleton projects，最终路径在 fresh inventory 后锁定，候选为：
  - `prototypes/SessionJournal.HistoryTimeline/`
  - `prototypes/SessionJournal.RecapGrid.Abstractions/`
  - 对应 `tests/*Tests/`
- solution/project registration 与 narrow architecture tests。

WP-00必须锁定而不是延后到SQLite施工时临时决定的目标图为：

```text
SessionJournal raw identities/contracts <- SessionJournal.HistoryTimeline
SessionJournal raw identities/contracts <- SessionJournal.RecapGrid.Abstractions
RecapGrid.Abstractions + Timeline read witness <- SessionJournal.RecapGrid.Control
RecapGrid.Abstractions + Microsoft.Data.Sqlite <- SessionJournal.RecapGrid.Store
Abstractions + Timeline + Control + Store <- SessionJournal.RecapGrid.Manager
```

`Control`与`Store`是siblings；SQLite Store不引用Control carrier，Control不引用Grid Store。是否合并
`Abstractions + Control`只能在WP-00用依赖/概念预算裁决，不能把新Grid塞进old `DerivedRecapEpochStore`大类后再等cutover整理。
箭头左侧是dependency。Abstractions/Control/Store都不得引用Completion runtime/provider或Galatea；Family tool/output contracts
使用provider-neutral canonical shapes。

禁止修改 old Store/Planner/Runtime/Maintainers 的业务行为。

## Walking-skeleton contract

fixture 必须在无 filesystem DB、无 provider、无 DI Host 情况下证明：

```text
HistorySegmentDescriptor
  -> GridBuildRecipe
  -> RowBuildSpec
  -> PriorInputProjectionDigest
  -> EvaluationKeyDigest
  -> deterministic fake CellArtifact
  -> RecapRowView
  -> fulfilled context value
```

它只验证 domain identity 与依赖方向，不提前实现后续 Store/Manager。
这里的records/hash helpers是throwaway/test-only shape spike，production不得引用；WP-01A/WP-02建立正式canonical owners后必须
删除或把fixture机械迁到正式types，不能留下第二套hash算法。

## Migration-ledger minimum

每条至少记录`Legacy symbol/path`、current callers、保留的behavior/invariant、target owner、
`Preserve | Move | Rewrite | Delete`、target WP、deletion gate与tests/evidence。首轮必须明确：

- Preserve：raw bounded-lineage/planning-window/range/setup与Prepared reconstruction contracts；
- Move：HistoryLoad estimator/projector/contracts及goldens；
- Rewrite：cadence/complete-roster policy、Store/Planner/Context/host composition；
- Delete：epoch/Building/Published/repair/layout-specific owners；
- Retarget：`recap history-load`、materialization inspection与progress UI；
- Keep connected until WP-08：current Galatea与CLI production callers，不注释、不加长期Obsolete shim。

## Validation

- fresh call-site/migration ledger有 exact `rg`/project evidence；
- skeleton first-row + second-row fixture；
- two distinct RowViews with same visible contents yield same projection/EvaluationKey；
- changed column order/content/definition yields different key；
- architecture test证明Timeline project无Maintainer/Grid/Completion runtime/provider/Galatea direct reference；
- old affected solution/tests仍 green；docs/diff checks green。

## Done when

- 冻结 baseline与rewrite branch均可定位；
- migration ledger覆盖所有 production call roots与旧 durable roots；
- 新项目依赖图可编译；
- walking skeleton贯穿核心 identities；
- 未改变 current production behavior；
- 独立 reviewer 确认 WP-01 不再依赖未决的上层类型。

## Handoff to WP-01

交付 exact contracts、assembly graph、raw fixture helper与 Timeline 所需 authority API inventory。若 skeleton 证明目标设计
字段不足，必须先修目标设计再关闭本包。
