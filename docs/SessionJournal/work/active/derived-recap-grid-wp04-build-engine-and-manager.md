# DerivedRecap Grid WP-04：Grid Build Engine 与 MaintainerManager

状态：Complete；两路independent review GO（P0=0，P1=0）；current production尚未切换

只需加载：目标设计、总计划、WP-03 handoff、本文和 WP-05 摘要。

## Intent

用 deterministic fake runtime 首次闭合Timeline + ControlPlane + GridStore：派生唯一RowBuildSpec、计算missing assignments、
逐row wavefront执行并提交Cell/RowView。先证明分析语义，再接真实Completion。

## Plan lock（2026-08-11）

- HistoryTimeline新增一个由exact selected `SessionJournalReadView` + estimators打开的owner-bound、
  mutation-free build read session。它只暴露Reader、`CaptureRaw(expectedWholeHead)`与
  `OpenSelectedSegment(capture, selectedRow/witness)`，内部复用现有Coordinator/OnlineRawPort实现；不暴露
  Coordinator/ledger，不复制raw reducer、partition或hash。
- 新`SessionJournal.RecapGrid.Manager`只direct reference HistoryTimeline + Abstractions + Control + Store，零package。
  Factory内部从同一selected read view/repository/Ref打开并持有Timeline build read session、Control reader handle与
  Store read/write handle；不接受external arbitrary Reader/source/handle注入。Handle使用operation refcount，Dispose
  drain已进入operation，一次operation绑定exact `StoreIdentity`。
- Build request为`LiveActive | ExplicitCandidate(recipeDigest)` + `ThroughRowId?`。非空Timeline的null through默认
  selected head；空Timeline只允许null并返回`NoRows`，不写Cell/RowView/Fulfilled。Budget同时限制
  `MaximumSelectedRows`、`MaximumRecipeRowSteps`、`MaximumNewCalls`与deadline/elapsed。
- `MaximumSelectedRows`是对本次冻结的完整selected head -> root path的admission bound，不是一个从root
  重放的调度量子；`MaximumRecipeRowSteps`限制row-major closure中的`recipe x row`总数。Recipe在empty
  Timeline注册时可有null bootstrap；之后Timeline出现rows时，full仍全部Evaluate，overlay将所有现有rows视为
  strictly after-bootstrap并使用`CreateNormal`，不为这些rows构建无用base artifacts。
- operation先用public Timeline Reader按head -> root有界分页冻结whole selected path，验证Ref/Timeline/
  predecessor/witness并做final whole-head fence，再reverse为root -> through。非空through必须在exact selected path；
  replay-safe由Timeline sealed row authority保证，Manager不接受raw boundary也不封row。
- 从frozen Control snapshot取exact requested recipe、整条base closure、definitions与families并复验scope/graph。
  采用row-major，每row按base -> candidate closure执行：requested recipe的required-through为请求through；每个base的
  required-through为`min(child required-through, child bootstrap)`。Grid reset/base artifact缺失时在同frozen current
  Timeline head下递归重建；禁止latest/global scan。每row `HistorySegmentContent`只open一次，只为
  requested recipe的final through写Fulfilled。
- pure derivation固定为：root使用FirstRow；后续row从exact previous candidate view及actual cells重算ordered
  PriorInputProjection；full始终`CreateFull`并all Evaluate；overlay在bootstrap闭区间用
  `CreateOverlayBootstrap`（recomputed Evaluate，其他same-row base Reuse）；bootstrap后用`CreateNormal`并all
  Evaluate。Existing EvaluationKey winner只免调用，不改写为Reuse。
- transient runtime contracts属于Manager，不扩张durable Abstractions。`FrozenRowBatch`包含whole frozen authority、
  HistorySegmentContent、RowBuildSpec、previous view + actual prior cells、projection以及ordered missing work + exact
  definition/family。Executor顶层closed result只是`RejectedBeforeDispatch(zero-started)`或
  `Completed(exact one outcome per work)`；item outcome只是`Updated | KeepUnchanged | Failed |
  NotStartedDueToCallerCancellation`。Duplicate/unknown/missing均为contract failure；catchable throw转typed runtime failure，fatal传播。
- 每row首dispatch前，整个predictable missing batch必须可容纳于剩余call budget。Executor返回后即使
  caller token已cancel，也先按ordinal settle全部started successes；成功siblings可PutCell，但任一failure都不写
  partial RowView，以最低ordinal failure为primary。`KeepUnchanged`仅在非首row且有same-column exact prior
  cell时合法，正文由Manager复制。下一row只在exact RowView complete后开始。
- Cell/RowView/Fulfilled的`CommitIndeterminate`只按Observed + same StoreIdentity reconcile，不重发executor；
  Busy只由caller以fresh operation重试。Fulfilled前复验Timeline/raw fences；Live复验whole Control head + active，
  Candidate只要求same Control instance/scope，后续active变化不中止。Stale immutable Cell/RowView可保留；若final
  Fulfilled put后authority才变化，也允许留下绑定old frozen head的exact immutable Fulfilled cache，但结果必须stale且不得返回proof。
- 只有请求through exact等于frozen selected Timeline head row及descriptor时，successful build才返回non-durable
  `RecapGridPromotableProof`，绑定ControlHead、TimelineHead、StoreIdentity、recipe、through row/descriptor、Fulfilled key/view。
  ancestor through只返回不含ControlHead expected tuple的`RecapGridFulfillmentReceipt`，不能直接promotion。Build绝不自动执行
  promotion；caller另行使用whole-head
  `ActivationPurpose.Promotion` CAS。没有durable Campaign/Attempt/Lease/Settlement；进度唯一由immutable
  Cell/RowView/Fulfilled与missing query重建。

## In scope

WP-03 handoff固定为public `RecapGridStoreFactory` owned handle及Reader/Writer typed results；本包不得引用Store internal SQLite、
schema、test hook或backend selector。`Busy`只允许caller以fresh operation重试；`CommitIndeterminate`先使用同一个owned Store handle
按exact intended locator读取结算：Found且canonical value exact相同才视为已提交，Missing/Busy等不能证明结果时返回
`SettlementRequired`，different/Invalid则fail closed。不得自动reopen、重发executor或把可能已提交的Cell/RowView/Fulfilled降成普通failure。
一次build operation持有同一个owned Store handle及其`StoreIdentity`；reset产生的新InstanceId即使同path也必须使旧operation stale，
不能跨identity继续冻结operation或复用missing/progress结论；caller只能另起fresh operation。

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
- operation的Timeline输入只来自WP-01C Reader snapshot/path/witness；`RecapGridManagerFactory`内部打开并拥有
  Timeline build-read session，Manager不得接触Coordinator、SQLite、ledger port或自行从global End/ordinal解析row；
- `RecapGridManagerFactory`内部用`RecapGridControlFactory.OpenReader(repositoryPath, RefId)`取得并拥有Control reader
  handle；composition只提交selected `SessionJournalReadView` + estimators，Manager消费其frozen snapshot，
  promotion只调用whole `ControlHeadRef` + whole `TimelineHeadRef` CAS并显式选择`ActivationPurpose.Promotion`。Busy/Stale/
  TimelineUnsupportedSchema/Disposed/Invalid均在首dispatch前closed fail；不得注入external Timeline Reader或持久化promotion proof；
  mutation返回`CommitIndeterminate(Intended, Observed?)`时不得自动重试或当作Invalid；先按Observed或同handle exact read reconcile，
  无法证明settled就返回`SettlementRequired`，再由caller决定是否以fresh operation继续；
- candidate catch-up、head-only可promote proof与partial-through receipt；active recipe CAS是显式后续control operation，不由Manager自动完成；
- 同一frozen Manager request的missing-only幂等re-entry；真实Host lifecycle boundary属于WP-07B。

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
- runtime input/result contracts由Manager项目拥有；WP-04不修改RecapGrid.Abstractions canonical/durable contracts；
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
13. 同一frozen Manager request连续三次进入：首次完成，后两次missing-free且zero-call；真实
    Idle/pre-observation、ObservationAccepted、ToolResultObserved lifecycle留给WP-07B；
14. head-through build返回`RecapGridPromotableProof`，ancestor-through只返回`RecapGridFulfillmentReceipt`；ControlPlane CAS
    crash/conflict不会污染active view，active暂时unfulfilled时Getter fail closed；
15. PutFulfilled后raw/control/Timeline drift可留下old-head exact Fulfilled cache，但必须返回stale/no proof。

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

交付Manager operation results、fulfilled-view semantics、mystery fixture与active/candidate read seam。WP-05不消费
`RecapGridPromotableProof`或partial receipt作为read authority；它只从current active Control snapshot + current exact Timeline head
独立构造Fulfilled key并resolve。WP-05不调用Manager mutation，也不取得Timeline Coordinator/ledger。Fulfilled selection与RowView/Cell
materialization必须保持同一个Store ReaderHandle/StoreIdentity；promotion仍是proof持有者显式执行的独立Control CAS。

## Handoff to WP-06

`IRecapCellBatchExecutor`、`FrozenRowBatch`和closed batch/item outcomes已经由Manager拥有。WP-06只实现这一provider-neutral seam及其
timeout、route、family lane、leader/follower与drain行为；不得向Manager泄漏provider request/client，不得写Cell/RowView/Fulfilled，
也不得引入durable attempt/campaign。Manager继续拥有whole-batch budget preflight、ordinal settlement与row barrier。

## Implementation and review record（2026-08-11）

- 新增`SessionJournal.HistoryTimeline.HistoryTimelineBuildReadSession`：从owner-bound selected read view打开，只暴露public Reader、
  captured raw fence与selected segment read。WP-01既有public Coordinator surface保持不变，但build session不暴露它，Manager也不引用
  Coordinator/ledger/OnlineRawPort。
- 新增独立`SessionJournal.RecapGrid.Manager`项目及external public-surface fixture。Factory内部拥有Timeline build read session、Control
  reader handle和Store read/write handle；project graph direct refs恰为HistoryTimeline、Abstractions、Control、Store，零NuGet package。
- 已实现whole selected path冻结、base closure required-through、full/overlay-bootstrap/normal exact derivation、missing-only row-major
  wavefront、same-row batch/next-row barrier、Keep复制、三类commit settlement、post-dispatch/final fences与non-durable proof。
- dense matrix包含：full/zero/multirow、null bootstrap、nested overlay add/remove/reorder/changed definition、content-equivalent prior、
  concurrent EvaluationKey winner、budget/time/cancel/failure/drain、Store identity reset、promotion conflict，以及mystery场景。Mystery先将
  `CulpritHypothesis` base设为active，再以explicit candidate加入`XSuspicion`；bootstrap exact reuse，future normal row从上一row的
  actual cells得到“原来如此”，candidate build后active仍为base；另有full retroactive same-row two-column batch证据。
- final serial tail evidence：Manager `57/57`。新增覆盖partial receipt/head proof promotion、三类wrong-intended settlement、
  reentrant/external-drain Dispose、Capture/Open cancellation、target ordinal、fatal propagation、post-Fulfilled三authority drift与descriptor mismatch。
  同轮Manager external public surface `1/1`、Walking architecture `15/15`、HistoryTimeline public surface `3/3`、
  `Atelia.sln` build 0 warning / 0 error；scoped docs checker 15/0，`git diff --check` clean。
- 两路independent closure review均为GO（P0=0，P1=0）：一路重点复核partial receipt/promotion proof、
  settlement intended authority与lifetime drain；另一路复核cancellation、ordinal/metrics、fatal propagation、old-head
  Fulfilled cache与WP-05 handoff。本包因而标记Complete，WP-05可以以本文的pure-read handoff为基线开工。
- current old Planner/Runtime/Maintainers、Galatea、CLI composition与production callers均未切换；真实
  Idle/ObservationAccepted/ToolResultObserved lifecycle属于WP-07B。本包只证明同一frozen Manager request的重复幂等进入。
