# DerivedRecap Grid WP-01B：Timeline Raw Integration 与 Branch Reconciliation

状态：Planned；依赖 WP-01A complete

只需加载：Grid target、Master、WP-01 overview、WP-01A handoff与本文。

## Intent

把pure partition接到exact selected raw lineage，闭合online bounded proof、offline bootstrap、rewind/fork与segment虚拟读取；
本包可用in-memory ledger，不决定最终backend。

## In scope

- online只消费`SessionJournalReadView`的bounded planning/raw/setup proof；
- `SessionHistoryPlanningWindow.RawRangeSha256`或raw owner提供的更窄verifier，不复制internal hasher；
- long-history首次建立走显式offline selected-lineage forward cursor；
- `OfflineBootstrapRequired`区分于OffLineage/RawHeadChanged/Invalid；
- captured-head APIs、`OpenSegment` raw rematerialization、common-prefix reconciliation；
- same Ref rewind/retarget、new Ref fork（V1不跨Ref dedup）。

## Validation matrix

1. online proof within bound成功；beyond bound零mutation返回OfflineBootstrapRequired；
2. fresh Timeline + frozen policy下，offline builder从root到captured head确定性地产生同descriptor chain；已有policy
   transitions必须显式提供ledger schedule，不能从raw猜切换时刻；
3. plan/materialize/commit间raw head变化不能错误promotion；
4. same Ref rewind到row boundary回指ancestor；落在row内部只保留前一完整row；
5. sibling retarget形成第二successor，旧row bytes不变；new Ref创建新TimelineId；
6. wrong Ref/head/off-lineage/hash/setup/count/load全部fail closed；
7. zero/empty raw与MoveRef-to-null产生Empty path而不伪造row。
8. same captured raw head下，`OpenSegment`传入same-Ref sibling Timeline row仍拒绝；reconcile只退共同prefix，不扫描latest successor。

## No-Go

- ordinary online path打开offline full cursor；
- Timeline复制SessionReducer或raw range hasher；
- 只用RefId、不用captured head证明lineage；
- raw branch“latest”或global ordinal selection。

## Done when

online/offline/branch/head-fence矩阵green，reviewer确认WP-01C只需替换in-memory ledger而不改变raw语义。

## Handoff to WP-01C

交付exact raw witness/factory contracts、branch fixture、typed result union与durable transaction必须保护的CAS fields。
