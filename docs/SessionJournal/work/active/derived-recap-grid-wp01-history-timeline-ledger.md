# DerivedRecap Grid WP-01：HistoryTimeline 总览

状态：Complete；WP-01A、WP-01B、WP-01C均complete；WP-02 Ready

只需加载：[`Grid target`](derived-recap-grid-target-design.md)、[`Master`](derived-recap-grid-rewrite-master-plan.md)、
[`WP-00 handoff`](derived-recap-grid-wp00-baseline-and-walking-skeleton.md) 与本文。实际施工再只加载对应子包。

## Intent

实现第一个production layer：与Maintainer数量为零仍可独立工作的HistoryTimeline。它持久化既定分段决策，
`HistorySegmentContent`始终从raw selected lineage按需materialize，不做值拷贝。

## Subpackage graph

```text
WP-01A contracts + partition semantics + HistoryLoad owner [complete]
  |
WP-01B raw integration + branch reconciliation [complete]
  |
WP-01C single durable ledger + crash/operator surface [complete]
```

- [`WP-01A`](derived-recap-grid-wp01a-timeline-contracts-and-partition.md)
- [`WP-01B`](derived-recap-grid-wp01b-timeline-raw-integration.md)
- [`WP-01C`](derived-recap-grid-wp01c-timeline-durable-ledger.md)

子包允许独立commit；WP-01C completion gate已确认没有留下competing estimator、backend或半连接public surface。

## Locked semantic boundary

- 首版从上一row `EndInclusive`之后开始，选择累计HistoryLoad首次达到B的replay-safe boundary；
- `MinimumRecentHistoryLoad`不进入Timeline；recent raw tail由SessionJournal context policy负责；
- online bounded proof不足返回`OfflineBootstrapRequired`，不会暗中全History scan；
- descriptor提交exact start/end setups、count/load、raw-range hash、estimator/policy identity；
- `DescriptorDigest`的formal type是HistoryTimeline独占的`HistorySegmentDescriptorDigest`，Grid不得以裸string重定义；
- 同Ref也可能rewind/retarget，所有plan/open/reconcile都绑定captured raw head；
- sealed row不随当前B、estimator、Maintainer或Grid reset重新解释；
- Timeline损坏不做普通reset：restore包含当前active head的verified backup，或通过canonical `ActiveTimelineLocator` CAS显式
  abandon旧TimelineId并以指定initial policy创建new Timeline。

## Target API family

```text
ReadSnapshot()
PutPolicy(canonicalPolicy)
CompareExchangePolicy(expectedWholeTimelineHead, nextPolicyDigest)
PlanNextRow(expectedTimelineHead, capturedSelectedRawHead)
CommitRow(proposal)
ReconcileSelectedPath(expectedTimelineHead, capturedSelectedRawHead)
ReconcileSelectedPathOffline(expectedTimelineHead, auditedForwardCursor)
OpenSegment(selectedTimelineHead, capturedSelectedRawHead, rowId)
ListPath(head, cursor, limit)
```

`Proposal`冻结whole expected `TimelineHeadRef`、outer captured raw head及canonical descriptor bytes。commit coordinator必须内部重读
bound raw ref的current head，不能信caller或`BoundHistorySegmentRange`充当authority；row insert + whole-head CAS是单一ledger
transaction，append保留expected active policy。`PutPolicy`只存immutable value；`CompareExchangePolicy`是独立transaction，
只切active policy并推进generation，不追加row。
若commit后raw立即MoveRef，immutable row只成为合法candidate，post-fence/下一次reconcile fail closed并选择共同prefix，不把它
当selected path。普通append增长不使已sealed range自动非法。

Timeline artifact scope只提交`RefId + TimelineId`；canonical colocated repository path只是runtime binding/locator，不进入
descriptor identity。`PlanNextRow`只能使用`ReadSnapshot`中的exact active policy，不能接受未注册policy bytes。
initial empty head为generation 0；empty head上的policy CAS仍保持row/raw fence为null但推进到generation 1+。
即使next policy digest与当前digest相同，成功CAS也必须推进generation，使旧capture与同expected contender变stale。

WP-01B只冻结public operation/result contract candidate；WP-01C已交付production factory、`ActiveTimelineLocator`
create/open与new Ref创建new TimelineId，并把in-memory semantic carrier迁到test assembly。public surface不暴露ledger/backend selector；
两路independent review均GO后，该交付现为stable WP-02 handoff。

## Global write boundary

- new `SessionJournal.HistoryTimeline` contracts/owner/tests；
- necessary provider-neutral raw selected-lineage read seam；
- Timeline-only inspect/export/verify/backup/restore/abandon surface；
- 不接current DerivedRecap composition。

禁止引用RecapGrid、MaintainerControlPlane、MaintainerManager、Completion runtime/provider或Galatea。允许通过SessionJournal
history-message neutral contract间接引用Completion abstractions；不得因此让Timeline知道provider request/runtime。

## Package gate

- 01A hash/partition/HistoryLoad contracts green；
- 01B online/offline/raw/branch/head-fence green；
- 01C single backend/CAS/crash/backup/operator green；
- zero Maintainer fixture能建立、读取Timeline；
- independent review确认WP-02只需opaque Timeline values和read witness。

Completion evidence：Timeline full 156/156、raw audit 19/19、walking architecture/package gates 13/13、public surface 2/2、
solution build 0 warning / 0 error、docs checker 15/0、diff check clean、两路independent review GO。commit evidence由包含本次变更的
containing commit提供，不预写hash。current production仍未cutover；Timeline V1 durability为Linux-only。

## Handoff to WP-02

交付stable Timeline contracts、fixture builder、selected-path witness、`OpenSegment` seam、backend/operator选择和任何对target
design的正式修订。
