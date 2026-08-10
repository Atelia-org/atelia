# DerivedRecap Grid WP-01A：Timeline Contracts 与 Partition Semantics

状态：Planned；依赖 WP-00 complete

只需加载：Grid target、Master、WP-00 handoff、WP-01 overview与本文。

## Intent

先锁定不依赖durable backend的Timeline values、canonical identity、partition算法和HistoryLoad单一owner，避免先写Store再
反推语义。

## In scope

- `PartitionPolicyRevision`、`HistorySegmentDescriptor`、`TimelineHeadRef`、`RowProposal`；
- 首个`MeasuredHistoryLoad >= TargetHistoryLoad`的replay-safe boundary算法；
- `MaxRawEventsPerSegment`、`MaxRenderedBytesPerSegment`与typed limit failure；
- `RowId`/`DescriptorDigest`无循环的identity preimage；
- canonical codec、strict decode、hash goldens与bounds；
- 将old Planner的HistoryLoad unit/estimator/projector/O200k goldens迁到唯一neutral owner；old Planner暂时引用新owner，禁止复制。

本包write scope显式允许old Planner/CLI/Galatea的csproj、using/import和对应HistoryLoad tests做机械owner迁移，但不改变
cadence behavior或production composition；任何业务改写仍留到cutover。

## Out of scope

- raw filesystem/journal访问、branch reconcile、durable backend、Maintainer/Grid、production composition。

## Validation matrix

1. B-1返回NotEnough；第一个replay-safe `>=B` boundary被选择；
2. tool-call/result/error dependency unit不可切断；
3. 60K rows不因policy改90K而重解释，新row记录新policy/estimator；
4. 达到raw/byte ceiling仍不可达时typed failure且无无限增长；
5. typed construction canonicalize输入；strict decoder拒绝whitespace/property reorder/unknown/duplicate/null/invalid UTF；
6. old/new owner estimator golden exact一致，production只有一个EstimatorId算法。

## No-Go

- 引入recent-reserve R到row identity；
- 用“任务启动时最后eligible boundary”造成运行时机依赖；
- HistoryLoad与provider token/billing混为一谈；
- 留两套HistoryLoad contract/hash owner。

## Done when

pure contracts/tests/build/docs/diff green，reviewer确认WP-01B无需修改descriptor identity。

## Handoff to WP-01B

记录exact descriptor/preimage/policy shapes、limit semantics、moved symbols与raw owner仍需提供的最窄seam。
