# DerivedRecap Grid WP-01A：Timeline Contracts 与 Partition Semantics

状态：Complete；independent review P0/P1=0；current production尚未切换

只需加载：Grid target、Master、WP-00 handoff、WP-01 overview与本文。

## Intent

先锁定不依赖durable backend的Timeline values、canonical identity、partition算法和HistoryLoad单一owner，避免先写Store再
反推语义。

## In scope

- `PartitionPolicyRevision`、`HistorySegmentDescriptor`、`TimelineHeadRef`、`HistoryRowProposal`；
- 首个`MeasuredHistoryLoad >= TargetHistoryLoad`的replay-safe boundary算法；
- `PartitionAlgorithmId`、`TargetHistoryLoad`、`MaxRawEvents`、`MaxRenderedBytes`与typed limit failure；
- `RowId`/`DescriptorDigest`无循环的identity preimage；
- canonical codec、strict decode、hash goldens与bounds；
- 将old Planner的HistoryLoad unit/estimator/projector/O200k goldens迁到唯一neutral owner；old Planner暂时引用新owner，禁止复制。

WP-00已经锁定`HistoryTimeline -> SessionJournal` direct edge；Timeline identity的正式types归本项目独占，后续
`RecapGrid.Abstractions`只消费这些typed values。迁移HistoryLoad时还必须处理三个已知handoff：

- `RecapHistoryLoadProjector`/O200k estimator当前依赖Planner内部`RecapNonFatalException`；新owner必须保留等价的
  fatal-exception filter，不能反向引用old Planner；
- `MaxRenderedBytes`需要每个replay-safe boundary的`MeasuredRenderedUtf8Bytes`累计值或等价partition evidence，不能只保留
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

WP-01B只接收`HistoryPartitionPoint`，由raw owner对`StartExclusive..EndInclusive`做exact selected-lineage rematerialize，
复核end/count/setups并返回带exact selected `RawRangeSha256`的`BoundHistorySegmentRange`。descriptor factory只接受该bound range，
不得读取或转抄whole-window `SessionHistoryPlanningWindow.RawRangeSha256`。`OfflineBootstrapRequired`仍由WP-01B定义。

## Implementation record（2026-08-10，independent-review candidate）

- `TimelineId`、`HistoryRowId`、policy/head/partition result/point、bound range、descriptor与proposal已落到
  `SessionJournal.HistoryTimeline`；policy与descriptor分别受4 KiB / 16 KiB canonical UTF-8上限约束；
- partition按raw order逐boundary增量测量，先判cap、再判target，且只测到当前boundary的`CompletedUnitCount`；
  consumed-prefix validation保证Selected/raw-limit/byte-limit terminal后不读取tail raw/boundary/unit/setup evidence；projector
  保持full-window validator行为，同时输出每个boundary的累计rendered UTF-8 bytes，两者共享measurement/fatal helper；
- policy把raw cap锁到65,536、rendered cap锁到32 MiB；共享legacy projector measurement保持`Load >= 1`、
  `RenderedUtf8Bytes >= 0`兼容，new partition在sealed-row evidence入口进一步要求每个已消费unit
  `RenderedUtf8Bytes >= 1`；
  descriptor factory逐字段核对point/bound range/policy/predecessor；`RowId`与typed
  `HistorySegmentDescriptorDigest`由同一不自指canonical body以
  不同domain hash导出，previous row进入body，outer head/generation/path不进入identity；
- empty Timeline head允许`HeadRowId=null`、raw fence为null且generation递增，以承载policy-only CAS；non-empty head仍要求
  positive generation与raw fence。`HistoryRowProposal`每次重编码descriptor，不暴露可变canonical backing array；
- HistoryLoad contracts/projector/O200k estimator及25-case baseline已从old Planner迁到Timeline唯一owner；Planner、CLI、Galatea
  及对应tests只机械改为引用新owner，production composition/cadence未切换；
- walking skeleton已改用正式Timeline descriptor/identity；architecture gate禁止test-only Timeline shape/hash，校验exact package
  allowlist、HistoryLoad declarations与`EstimatorId`唯一owner。
- final tail后串行验证：Timeline 54/54、walking skeleton 12/12、Planner full 42/42；`Atelia.sln` build为
  0 warning/0 error；scoped docs checker 15 files/0 diagnostics，`git diff --check`除既有`Atelia.sln` line-ending提示外
  无错误。CLI full为既有fingerprint golden单一失败（66/67），与WP-00 baseline一致；Galatea full为
  89 pass / 5 fail / 4 skip，其中四项是WP-00记录的cadence/stale/undo baseline debt，额外一项是同一已知
  route-overlap timeout flaky（本包早先focused曾通过，final isolated rerun再次超时）。
- 两条独立review分别覆盖contract/partition/canonical identity与owner/package/reference/docs迁移；两轮tail修复后
  最终结论均为P0/P1=0。

本记录不表示WP-01B已开工或current production已经切换。
