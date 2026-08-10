# DerivedRecap Grid WP-00：Baseline、迁移账本与 Walking Skeleton

状态：Complete on `feature/derived-recap-grid-rewrite`；WP-01A ready

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
- 新 assembly shells/test-only skeleton project，fresh inventory后锁定为：
  - `prototypes/SessionJournal.HistoryTimeline/`
  - `prototypes/SessionJournal.RecapGrid.Abstractions/`
  - `tests/SessionJournal.RecapGrid.WalkingSkeleton.Tests/`
- solution/project registration 与 narrow architecture tests。

WP-00必须锁定而不是延后到SQLite施工时临时决定的目标图为：

```text
SessionJournal.HistoryTimeline -> SessionJournal raw identities/contracts
SessionJournal.RecapGrid.Abstractions -> SessionJournal.HistoryTimeline
SessionJournal.RecapGrid.Control -> RecapGrid.Abstractions + Timeline read witness
SessionJournal.RecapGrid.Store -> RecapGrid.Abstractions + Microsoft.Data.Sqlite
SessionJournal.RecapGrid.Manager -> Abstractions + Timeline + Control + Store
```

`Control`与`Store`是siblings；SQLite Store不引用Control carrier，Control不引用Grid Store。WP-00已裁决
`Abstractions`与`Control`保持分离；Control留到WP-02建立，不能把新Grid塞进old `DerivedRecapEpochStore`大类后再等cutover整理。
箭头左侧是consumer/dependent。Abstractions/Control/Store都不得直接引用Completion runtime/provider或Galatea；Family tool/output contracts
使用provider-neutral canonical shapes。

`HistoryTimeline -> SessionJournal`的传递闭包当前包含`Completion.Abstractions/Completion.Tools`，因为raw planning units暴露
provider-neutral history messages；这不等于Timeline直接拥有client/provider/runtime。WP-00 architecture gate锁两个new
projects的direct `ProjectReference`/`PackageReference` allowlist、raw SessionJournal反向边和compiled assembly实际reference；
允许该中立传递闭包，禁止new assemblies实际引用`Completion` runtime、`Completion.Tools`、Galatea和old DerivedRecap owners。
尚未创建的Control/Store/Manager由各自WP在创建时补同等级executable gate，WP-00只锁其future direct-reference allowlist。

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
这里的records/hash helpers是throwaway/test-only shape spike，production不得引用；它们不锁正式canonical bytes或golden。
WP-01A/WP-02建立正式canonical owners后必须
删除或把fixture机械迁到正式types，不能留下第二套hash算法。

## Migration-ledger minimum

每条至少记录`Legacy symbol/path`、current callers、保留的behavior/invariant、target owner、
`Preserve | Move | Rewrite | Delete | Retarget | Keep-connected-until-WP08`、target WP、deletion gate与tests/evidence。
首轮必须明确：

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
- architecture test证明`Timeline -> SessionJournal -> ...`与`Abstractions -> Timeline`的direct edge精确，且传递闭包无
  concrete Completion runtime/Galatea/old DerivedRecap owner；
- solution build与new focused tests green；old affected suites必须与冻结baseline同命令对照：已知baseline failure可原样保留，
  但failure set不得扩大，且old production/tests restricted diff必须为零；docs/diff checks green。

## Done when

- 冻结 baseline与rewrite branch均可定位；
- migration ledger覆盖所有 production call roots与旧 durable roots；
- 新项目依赖图可编译；
- walking skeleton贯穿核心 identities；
- frozen-ref对照与restricted diff证明未改变current production behavior；
- 独立 reviewer 确认 WP-01 不再依赖未决的上层类型。

## Handoff to WP-01

交付exact ownership/assembly decisions、test-only shape evidence、raw authority API inventory与migration gates；不把临时
skeleton冒充正式contract。若skeleton证明目标设计字段不足，必须先修目标设计再关闭本包。

## Implementation record（2026-08-10）

- cut-start、archive与feature ref均从`5e1ba46eb84f784a6fa481829a0cabc14b73781f`建立；未push remote；
- solution新增空product shells `SessionJournal.HistoryTimeline`、`SessionJournal.RecapGrid.Abstractions`与独立
  `SessionJournal.RecapGrid.WalkingSkeleton.Tests`；current production没有切换；
- migration ledger以L/T/D三组可关闭entries覆盖旧symbols/tests/durable roots；不存在的v9只作negative guard；
- walking skeleton 10/10，覆盖two-row identity chain、content-equivalent projection reuse、order/content/definition变化、
  frozen prior、wrong row/prior/key/definition/timeline/recipe/predecessor fail-closed；
- `dotnet build Atelia.sln --no-restore -m:1 -nr:false`为0 warning/0 error；docs checker为15 files/0 diagnostics；
- old Store 32/32、Planner 67/67、Maintainers 29/29保持green。冻结ref与current同命令对照确认三组baseline debt完全相同：
  SessionJournal 420/421（read-view public surface expected 16 / actual 17）、CLI 66/67（completion fingerprint golden）、
  Galatea 90 pass / 4 fail / 4 skip（cadence两项、stale/exact、recent rewind）。一次较早current Galatea run还出现
  route-overlap timeout；同binary重跑与冻结ref均不出现，记录为既有flaky observation，不冒充green；
- old production/tests restricted diff为零；独立review最终P0=0/P1=0。

WP-01A接手时必须删除/替换fixture中的private Timeline shapes/hash，不能复制其preimage；architecture test从“product无
`.cs`”改为“无遗留test-only Shape/hash owner”，并把空`PackageReference` allowlist更新为HistoryLoad迁移实际需要的
exact tokenizer package allowlist。目标设计、WP-01B/C及其后工作包不需要语义改线。
