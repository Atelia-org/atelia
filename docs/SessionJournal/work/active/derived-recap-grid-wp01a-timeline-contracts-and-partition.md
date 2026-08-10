# DerivedRecap Grid WP-01A：Timeline Contracts 与 Partition Semantics

状态：Ready；WP-00 implementation handoff complete

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

WP-00已经锁定`HistoryTimeline -> SessionJournal` direct edge；Timeline identity的正式types归本项目独占，后续
`RecapGrid.Abstractions`只消费这些typed values。迁移HistoryLoad时还必须处理三个已知handoff：

- `RecapHistoryLoadProjector`/O200k estimator当前依赖Planner内部`RecapNonFatalException`；新owner必须保留等价的
  fatal-exception filter，不能反向引用old Planner；
- `MaxRenderedBytesPerSegment`需要每个replay-safe boundary的累计rendered bytes或等价partition evidence，不能只保留
  整个suffix总数；
- current planning window的`RawRangeSha256`覆盖整窗；当首个`>=B` boundary早于captured head时，选中segment的exact
  range commitment必须由WP-01B rematerialize/verify，WP-01A不得把整窗hash冒充selected range hash。
- 正式Timeline types落地时删除/替换walking skeleton中的private descriptor/hash shapes；不得复制test-only preimage或留下
  第二套identity算法。architecture gate同步从“product无`.cs`”改为“无遗留test-only Shape/hash owner”；
- WP-00锁定的空`PackageReference` allowlist必须改成HistoryLoad迁移实际需要的exact tokenizer package allowlist，不能为
  施工方便删除该gate。

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
