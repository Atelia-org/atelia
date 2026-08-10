# DerivedRecap Grid WP-01B：Timeline Raw Integration 与 Branch Reconciliation

状态：Complete；focused/full/solution/docs validation green；两条independent closure review均GO；依赖 WP-01A complete

只需加载：Grid target、Master、WP-01 overview、WP-01A handoff与本文。

## Intent

把pure partition接到exact selected raw lineage，闭合online bounded proof、offline bootstrap、rewind/fork与segment虚拟读取；
本包可用in-memory ledger，不决定最终backend。

## In scope

- 每次operation先从outer composition冻结`<RefId, captured selected raw head>`，online只消费绑定该head的
  `SessionJournalReadView` bounded planning/raw/setup proof；commit前由raw authority重读同Ref current head并要求仍exact相等；
- fresh row的seed只来自`ReadSessionCreatedPlanningSeedAtBounded(capturedHead, policy.MaxRawEvents)`；successor row只用
  `CreateHistoryPlanningSeed(previous.EndInclusive, previous.EndSetups)`，不另造第三种seed route；
- outer window只用`ReadHistoryPlanningWindowAtBounded(capturedHead, seed, policy.MaxRawEvents)`。baseline只能调用
  `HistoryLoadBaselineResolver.Resolve(window.StartExclusive, window.Units.Count, window.ReplaySafeBoundaries, window.StartExclusive)`
  取得，并断言`CompletedUnitCount == 0`且
  `FirstLaterBoundaryIndex == 0`；不直接构造baseline，也不解析任意中段baseline；
- outer window只交给`HistoryPartitioner.Partition`。得到Selected `HistoryPartitionPoint`后，raw owner使用
  `ReadHistoryPlanningWindowAtBounded(point.EndInclusive, sameSeed, point.RawEventCount)`做exact selected-lineage
  rematerialize并计算selected range的exact `RawRangeSha256`；exact window同样通过resolver取得并断言0/0 baseline，再只用
  `HistoryPartitioner.Partition`与同一creation policy/estimator repartition；不得调用legacy full-window `HistoryLoadProjector`；
- repartition必须再次Selected，并要求point的Timeline/policy/start/end/setups/baseline count/end count/load/raw count/
  rendered bytes逐字段相等；
- 只有上述match通过才构造`BoundHistorySegmentRange`。它只是把raw owner已经验证的typed evidence交给descriptor factory，
  不成为raw authority；commit仍执行captured-head fence与raw-side复验。不得用whole-window
  `SessionHistoryPlanningWindow.RawRangeSha256`代替selected-range hash，Timeline也不复制internal hasher；
- long-history首次建立走显式offline selected-lineage forward cursor；
- policy hard caps沿用WP-01A：`MaxRawEvents <= 65,536`、`MaxRenderedBytes <= 32 MiB`。到达policy cap是
  `LimitExceeded(MaxRawEvents|MaxRenderedBytes)`；raw bounded reader在policy terminal之前返回`BeyondPrefix`才转换为
  `OfflineBootstrapRequired`，并始终与OffLineage/RawHeadChanged/Invalid分开；
- captured-head APIs、`OpenSegment` raw rematerialization、common-prefix reconciliation；
- `OpenSegment`按row的`PartitionPolicyDigestAtCreation`与`HistoryLoadEstimatorId`解析creation policy/estimator并复验，
  不读取或改用当前active policy/estimator；
- in-memory seam也要分开row append与policy CAS：append保留expected head的active policy；policy CAS不追加row，
  只在whole-head match后切policy并推进generation；
- same Ref rewind/retarget；new Ref仍按V1不跨Ref dedup，但new Timeline locator/create factory明确留给WP-01C。

## Typed outcomes

实现前用contract tests锁定closed typed integration union。`Empty`只属于online raw capture的真正null head；Plan与offline step不保留
dead `Empty` variant，SessionCreated-only lineage统一返回`NotEnough(0)`。其余明确包含pure partition的
`Selected | NotEnough | LimitExceeded`、`PartitionAlgorithmUnavailable`、`OfflineBootstrapRequired`、OffLineage、RawHeadChanged、stale Timeline head、creation
policy/estimator unavailable与invalid evidence。raw `BeyondPrefix`不是独立outcome，只能作为`OfflineBootstrapRequired`的typed
payload/cause。不得用exception message、`null`或generic Invalid吞并这些可恢复性不同的结果。

## Validation matrix

1. online proof within bound成功；policy cap返回LimitExceeded，raw BeyondPrefix零mutation返回OfflineBootstrapRequired；
2. fresh Timeline + frozen policy下，offline builder从root到captured head确定性地产生同descriptor chain；已有policy
   transitions必须显式提供ledger schedule，不能从raw猜切换时刻；
3. outer partition -> exact rematerialize -> repartition逐字段match；plan/materialize/commit间raw head变化不能错误promotion；
4. same Ref rewind到row boundary回指ancestor；落在row内部只保留前一完整row；
5. sibling retarget形成第二successor，旧row bytes不变；new Ref/new Timeline创建移交WP-01C locator/create gate；
6. wrong Ref/head/off-lineage/hash/setup/count/load全部fail closed；
7. null raw与MoveRef-to-null只在Capture产生Empty；SessionCreated-only online/offline均NotEnough(0)，reconcile可产生empty selected path而不伪造row；
8. same captured raw head下，`OpenSegment`传入same-Ref sibling Timeline row仍拒绝；reconcile只退共同prefix，不扫描latest successor；
9. active policy改变后，旧row仍由creation policy/estimator打开；append不切policy，empty/nonempty policy CAS只推进whole head。

## No-Go

- ordinary online path打开offline full cursor；
- Timeline复制SessionReducer或raw range hasher；
- 只用RefId、不用captured head证明lineage；
- raw branch“latest”或global ordinal selection。

## Implementation record（2026-08-10 final）

- production surface由public `HistoryTimelineCoordinator`与closed result unions承载；ledger port、exact-ID estimator registry、
  online raw port与in-memory semantic carrier均为internal，walking-skeleton tests仅通过IVT白盒验证，不形成可误用backend API；
- 这仍是public contract candidate而不是production factory；coordinator的composition/factory与new Ref/new Timeline locator/create
  gate延后到WP-01C，caller不能直接选择in-memory backend；
- coordinator绑定canonical colocated repository path；online capture同时绑定exact whole `TimelineHeadRef`、Ref与captured raw head，
  different-repository same Ref/head clone、same-repository wrong Ref、policy CAS后复用旧capture全部fail closed；
- `BoundHistorySegmentRange`与`HistoryRowProposal`不能由assembly外构造；ledger commit只接受携带owner-bound raw fence的opaque
  `HistoryRowCommitCandidate`，并在ledger lock内先复验raw current head再完成row insert + whole-head CAS；
- offline builder只消费caller已经完成audit并打开的`SessionSelectedLineageForwardCursor`。cursor新增owner-bound
  `IsBoundTo(repositoryPath, refId, capturedHead)`、`ReadCurrentHead()`与pending-range extension；builder不隐藏调用
  `BeginSelectedLineageAudit`/`OpenSelectedLineageForwardCursor`，失败或terminal后必须以fresh cursor重开；
- offline consecutive rows允许把前一row消费后的exact retained suffix扩展到新policy cap；显式policy schedule继续走
  `PutPolicy + CompareExchangePolicy`。新cap小于retained suffix时typed `OfflinePolicyRangeCapIncompatible`，不截断证据；
- `OpenSegment`先验证row属于exact selected predecessor chain，再只证明selected Timeline head endpoint位于bounded raw prefix；任意
  selected ancestor随后按creation policy/estimator exact rematerialize/repartition并比较完整canonical descriptor；
- reconcile不沿Timeline rows做unbounded coordinator scan。in-memory carrier为每个selected head维护structural-sharing immutable
  `RowId -> descriptor`与`EndInclusive -> descriptor` snapshot：row membership/boundary probe与ancestor snapshot切换不回扫root/head。
  online只遍历bounded raw prefix；prefix不足返回typed `OfflineBootstrapRequired`。显式offline reconcile则消费caller提供的fresh audited
  forward cursor，单次content-free bootstrap-to-captured-head streaming pass逐address probe exact boundary index，只保留latest match，
  final raw fence后复用同一opaque candidate与第四whole-head CAS；不物化candidate/row集合，也没有65,536候选cap或O(n²) reopen；
- same-policy CAS也推进generation；unknown partition algorithm在Plan/Open/OfflineStep均typed
  `PartitionAlgorithmUnavailable`且零mutation；final forward range保持`nextSeed=null`，只有消费replay-safe prefix才创建seed；
- 当前implementation证据：`SessionJournal.HistoryTimeline.Tests`完整90/90；online/offline/reconcile-open三类focused 36/36；
  `SessionSelectedLineageAuditTests` 19/19；serial `--no-restore`，build 0 warnings / 0 errors，`git diff --check` clean。cross-mode相同
  raw fixture/frozen policy的descriptor canonical bytes、RowId、typed DescriptorDigest chain完全相等；
- 最终严格串行closure evidence：Timeline 90/90、raw audit 19/19、walking-skeleton project-graph/architecture 12/12、
  `Atelia.sln` build 0 warnings / 0 errors、SessionJournal docs checker 15 checked / 0 diagnostics、`git diff --check` clean；
- 两条相互独立的contract/robustness closure review在one-shot inspection exhaustion尾修后均给出GO；Probe/Find/Seek不能复用
  exhausted cursor，new-operation `IsBoundTo`/`OpenOfflineBuilder`拒绝，而同一次offline reconcile仍可用owner-bound
  `ReadCurrentHead()`完成ledger-lock final raw fence；
- 本包只完成旁路HistoryTimeline contract/raw integration与in-memory semantic carrier，没有切换Galatea/CLI/current DerivedRecap
  production composition，也没有把internal carrier提升为production factory；durable backend、locator/create与production construction仍归WP-01C。

WP-01B gate已完成；上述两条independent GO与最终串行证据使WP-01C进入Ready，但不预先认证WP-01C implementation或production cutover。

## Done when

online/offline/branch/head-fence矩阵green，reviewer确认WP-01C只需替换in-memory ledger而不改变raw语义。

## Handoff to WP-01C

交付exact raw witness/factory contracts、branch fixture、closed typed result union、selected-path boundary index语义，以及WP-01C
四种durable transaction必须保护的whole-head与captured-raw-head fields。
