# SessionJournal HistoryLoad and Timeline partitioning

> **状态**：Implemented current contract
> **Owner**：`prototypes/SessionJournal.HistoryTimeline`

## 1. Meaning

`HistoryLoadUnit`是SessionJournal历史分区的provider-neutral尺度。它不等于provider tokens、billing、
context-window占用、event count或语义价值。V1 estimator identity为：

```text
atelia.history-load.o200k-base.history-unit-v1
```

该identity冻结正文选择、framing、tokenizer vocabulary和单HistoryUnit的数值语义。任何这些规则变化都
必须发布新的estimator identity；不同identity的数值不可直接比较。

## 2. Authority input

HistoryLoad只测量SessionJournal提供的dependency-closed `SessionHistoryPlanningWindow`：

- ordered units携带exact source `EventAddress`；
- boundaries是replay-safe points，而不是任意event count切点；
- baseline来自SessionCreated planning seed或exact previous Timeline row end；
- selected-lineage、raw contiguity和setup chain由SessionJournal raw owner验证。

`HistoryLoadProjector`和`HistoryLoadMeasurementEngine`不自行读取repository，也不拥有branch authority。

## 3. Partition policy

每个Timeline保存immutable `PartitionPolicyRevision`，至少绑定：

- `PartitionAlgorithmId`；
- `HistoryLoadEstimatorId`；
- `TargetHistoryLoad`；
- `MaxRawEvents`；
- `MaxRenderedBytes`。

当前partitioner在bounded window中选择第一个满足 `load >= TargetHistoryLoad` 的replay-safe boundary。
若尚未到target则返回typed `NotEnough`；若在找到safe boundary前触及raw/rendered hard cap则返回
`LimitExceeded`。exact-hit terminal probe必须区分“刚好N条且已结束”与“N+1条仍存在”。

`MinimumRecentHistoryLoad`不进入Timeline partition revision。它由per-Ref repo-owned RecapGrid Cadence
canonical V1 policy持有；同一policy还提交上述五个partition字段的exact期望值。目标部署值是
`TargetHistoryLoad B=60,000`、`MinimumRecentHistoryLoad R=24,000`。Timeline仍选择first-safe B boundary，
Cadence-owned seal operation另证明candidate之后的selected raw suffix至少保留R；因此理想首封门槛是84,000，
replay-safe overshoot时门槛相应上移。

## 4. Canonical row evidence

一个Timeline row descriptor提交exact raw range、rendered-byte evidence、creation policy digest、estimator
identity、previous RowId与descriptor digest。Plan之后必须从raw owner精确rematerialize到selected point，
使用同一seed/policy/estimator重新partition并逐字段比较，最后再做raw head与whole Timeline head fence。

RowId和DescriptorDigest使用分离的hash domain；任何policy/descriptor/previous-chain变化都会产生不同
identity。打开旧row时使用其creation policy和estimator，而不是当前active policy。

## 5. Online and offline

- Online只读取active policy允许的bounded selected-lineage prefix；`BeyondPrefix`要求显式offline path，
  不能扩大normal scan或fallback到latest row。
- Offline使用SessionJournal签发的owner-bound audited cursor，流式reconcile/build并在最终commit前重验
  captured raw head。同一audit snapshot可以签发独立cursors，但每个cursor都是one-shot且fail closed。
- policy change、append、reconcile和abandon/restore都比较whole `TimelineHeadRef`。policy CAS即使semantic
  no-op也推进generation，使旧capture失效。
- Online/offline/CLI writers只经Cadence seal facade提交row；同一次multi-row loop冻结一个Cadence snapshot，
  raw/Timeline/Cadence任一drift均fail closed。seal、build-read reserve anchor与Getter selection共享
  `HistoryRecentReserveOperationLimits.MaximumRawEvents = 262,144`；这是单operation work cap，不是segment cap或长期容量。

## 6. Consumers

RecapGrid Manager通过HistoryTimeline public Reader/build-read-session消费已提交rows与exact selected segment；
它不重新估算HistoryLoad或复制raw reducer。Getter沿selected row/view chains做pure read。CLI的
`recap-grid timeline history-load inspect`只报告该owner的measurement，不成为新的cadence authority。
`recap-grid cadence inspect` pure-read读取durable policy；`cadence set-reserve`要求exact Ref/head并只CAS更新R，
保留B/estimator/algorithm/caps。

Provider pricing/calibration可把HistoryLoad换算成估算成本，但换算参数属于operational tooling，不能进入
Timeline row、recipe、cell或fulfilled identity。

## 7. Verification

focused evidence由`SessionJournal.HistoryTimeline.Tests`、`SessionJournal.Tests`与Walking architecture
gates提供，覆盖first-safe boundary、cap exact/cap+1、online/offline descriptor一致性、branch reconcile、
policy change、raw drift与estimator unavailable。真实provider usage不是该contract的验收条件。
