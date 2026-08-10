# DerivedRecap Grid WP-01B：Timeline Raw Integration 与 Branch Reconciliation

状态：Ready；依赖 WP-01A complete

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
- same Ref rewind/retarget、new Ref fork（V1不跨Ref dedup）。

## Typed outcomes

实现前用contract tests锁定closed typed integration union，并明确包含`Empty`，以及pure partition的
`Selected | NotEnough | LimitExceeded`、`OfflineBootstrapRequired`、OffLineage、RawHeadChanged、stale Timeline head、creation
policy/estimator unavailable与invalid evidence。raw `BeyondPrefix`不是独立outcome，只能作为`OfflineBootstrapRequired`的typed
payload/cause。不得用exception message、`null`或generic Invalid吞并这些可恢复性不同的结果。

## Validation matrix

1. online proof within bound成功；policy cap返回LimitExceeded，raw BeyondPrefix零mutation返回OfflineBootstrapRequired；
2. fresh Timeline + frozen policy下，offline builder从root到captured head确定性地产生同descriptor chain；已有policy
   transitions必须显式提供ledger schedule，不能从raw猜切换时刻；
3. outer partition -> exact rematerialize -> repartition逐字段match；plan/materialize/commit间raw head变化不能错误promotion；
4. same Ref rewind到row boundary回指ancestor；落在row内部只保留前一完整row；
5. sibling retarget形成第二successor，旧row bytes不变；new Ref创建新TimelineId；
6. wrong Ref/head/off-lineage/hash/setup/count/load全部fail closed；
7. zero/empty raw与MoveRef-to-null产生Empty path而不伪造row；
8. same captured raw head下，`OpenSegment`传入same-Ref sibling Timeline row仍拒绝；reconcile只退共同prefix，不扫描latest successor；
9. active policy改变后，旧row仍由creation policy/estimator打开；append不切policy，empty/nonempty policy CAS只推进whole head。

## No-Go

- ordinary online path打开offline full cursor；
- Timeline复制SessionReducer或raw range hasher；
- 只用RefId、不用captured head证明lineage；
- raw branch“latest”或global ordinal selection。

## Done when

online/offline/branch/head-fence矩阵green，reviewer确认WP-01C只需替换in-memory ledger而不改变raw语义。

## Handoff to WP-01C

交付exact raw witness/factory contracts、branch fixture、closed typed result union，以及WP-01C三种durable transaction必须保护的
whole-head与captured-raw-head fields。
