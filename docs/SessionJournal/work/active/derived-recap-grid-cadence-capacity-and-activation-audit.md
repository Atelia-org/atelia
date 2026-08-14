# RecapGrid cadence、长期容量与 Galatea 激活设计审计

状态：Active capacity/activation audit；cadence A0/A1/A2 complete，C3A/C3B/C3C/C3D complete；C3C两路independent
closure均GO（P0=0，P1=0）；C2A/C2B/C2C source complete且两路independent closure均GO（P0=0，P1=0）；
C2D real-provider/actual activation及C4/C5仍未完成。

核对基线：cutover `6f9ea7db`；cadence owner `0af28eea`、authority/durability fixes `397f2ab8`/`b0bce3b3`、
reserve-aware seal `1e8ea927`、reserve-aware selection `bac31986`。事实优先级仍是 current code、tests、canonical codecs，以及 raw events + selected
`RefId` Parent lineage。本文不会把计划或成本模型升级成 durable authority。

上游设计：[DerivedRecap Grid target design](derived-recap-grid-target-design.md)。后者已经正确描述大部分
Timeline/Grid/Control/Manager/Getter 边界，但其中把`MinimumRecentHistoryLoad`留给ContextComposer/SessionJournal
raw config的责任分配已经被A0-A2取代：它现在由per-Ref repo-owned RecapGrid Cadence sidecar持有，
Timeline seal与Getter selection消费同一durable policy。本文继续记录尚未关闭的长期容量与activation边界。

## 1. 为什么需要这份审计

用户在 Galatea 的旧 DerivedRecap 输出中观察到严重的长期遗忘：行为和产物表现得像是只保留了最近一段经历，较早的
自传与世界理解没有被可靠承继。这足以把旧 derived 正文判为不可信，并支持从较早的 clean raw 基点重新生成；但本文
不把事故机理武断归因为“旧实现完全没有传 previous recap”。Cutover 前的 v8 确实存在 `PriorRecapPack` 路径，质量事故还
可能来自 prompt、模型、route、运行时或 artifact settlement。根因若要定论，应另做历史二进制/请求证据复盘。

RecapGrid 的目标不是靠这项未证实归因成立，而是提供可直接验收的 previous-row projection：每条新 row 都把上一版完整
认知和本轮即将离开 recent context 的经历一起交给 Maintainer，得到下一版完整认知。

WP-00 至 WP-08 已经完成 Grid source cutover，但“rolling rewrite 正确”不自动证明 cadence、recent raw continuity、
长期容量与真实 Galatea 激活也正确。本轮对照用户目标发现：

1. Grid rolling row 语义基本正确；
2. `RecapBuildIntervalHistoryLoad = 60,000` 可以由 Timeline 表达；
3. `MinimumRecentHistoryLoad`现已有durable per-Ref contract与online/offline/Getter gate；目标部署值为24,000；
4. Online/CLI seal已不再只按B drain，必须证明candidate之后仍保留至少R；
5. `128` 不是 Timeline 总 row 上限，Manager的4,096 whole-path semantic cliff已由C3B删除；C3D V2又删除
   Timeline累计65,536-row/8 GiB lifetime caps与旧逐row immutable-trie path-copy放大，仍保留每页、单artifact与单operation边界；
6. Galatea 需要的“自传 + world-understanding + Opus 4.6”正式 built-in 与真实数据激活仍未完成。

因此 actual cyber activation 维持 **No-Go**，直到 §9 的 activation-blocking 项关闭。

## 2. 用户可见的目标承诺

### 2.1 历史与 derived authority

- raw SessionJournal events 与 selected `RefId` Parent lineage 是唯一历史事实源；recap 不能覆盖、修补或替代 raw。
- HistoryTimeline 保存过去怎样分段的 durable decision，不保存 History 正文。
- Control 保存完整 Family/Definition/Recipe、active recipe 与 operation receipts。
- RecapGrid Store 保存 immutable Cells、RowViews 与 Fulfilled refs。它们可从 raw + Timeline + Control
  重新求值，但由于 LLM 非确定性，不承诺 reset/rebuild 后得到 byte-identical 的旧模型正文。
- 某次真实请求已经看到的 exact context 由 Prepared request/snapshot raw evidence 审计；不能依赖以后重新调用 LLM
  来重造过去实际看到的文字。

### 2.2 Galatea 的首个正式 rolling grid

首个 production recipe 目标是同一 row 上的两列完整重写：

| 顺序 | 概念列 | Context target | 内容职责 |
|---:|---|---|---|
| 1 | `world-understanding` | `Observation / roleplay.world-understanding` | Galatea 当前对人物、环境、项目、事实、推断与 known unknowns 的工作理解 |
| 2 | `autobiography` | `Action / roleplay.first-person-autobiography` | Galatea 如何成为现在的自己：重要经历、关系、感受、承诺、犹豫与当前内在状态 |

目标实现形状：

- 一个 code-owned `galatea-rolling-rewrite-zh-cn-v1` built-in；
- 两个 ordered Maintainer Definitions和一个 Full recipe；两列共用现有
  [`recap-maintainer-family/system-zh-cn.md`](../../../Galatea/prompt/recap-maintainer-family/system-zh-cn.md)
  形成一个shared Family，专业差异来自各自的zh-CN user prompt；
- input scope 为完整 previous BuildTarget projection + current exact History segment；
- 两列使用同一个`SemanticModelId=null` exact route key；默认runtime config把它映射到Opus 4.6 connection，但actual model是
  可切换的运行时策略，不属于durable semantic identity；
- route、connection、lane、cache 与 usage 是 runtime/operation evidence，不因部署选用 Opus 4.6 就自动进入 Cell identity；
- `world-understanding` 与 `autobiography` 的专业内容准则继续来自现有 zh-CN user prompts；共享system prompt只承载共同
  rolling rewrite协议与证据纪律，不能把两个文档退化成同一种摘要。

C2 exact设计、程序集边界、未来RecapEditor/ExperienceRefiner扩展点与实施矩阵见
[`derived-recap-grid-c2-galatea-rolling-maintainers.md`](derived-recap-grid-c2-galatea-rolling-maintainers.md)。

当前 repo 只有 `mystery-investigation-v1` AgentControl code-owned built-in；上述Galatea operator asset仍是待实现目标。每列
`MaxContentUtf8Bytes = 32 KiB`作为C2首版工程默认；若真实canary证明不足，应发布新Definition revision调整，不能用runtime override
改变既有Definition语义。

### 2.3 Rolling rewrite 的跨 row 语义

设 row `n` 的两列为 `W_n` 与 `A_n`：

```text
row 0:
  inputs = FirstRow + segment_0
  outputs = W_0, A_0

row n > 0:
  shared prior = ordered [W_(n-1), A_(n-1)]
  inputs = shared prior + segment_n
  outputs = W_n, A_n
```

同一 row 的两列在语义上彼此独立，并看到相同的 previous-row projection。它们不会看到 sibling 在当前 row 刚生成的结果；
`W_n` 和 `A_n` 会一起成为 row `n+1` 的 prior。这是“跨 row 相互传播”，不是同一 row 内隐式串行讨论。

“语义独立”不等于“provider调用一定并行”。Current Runtime 对同route group先执行leader、再释放followers以支持cache；
C2已锁定single shared Family，因此两列走同一route group的leader/follower调度；其他domain拥有多个Families/route groups时才可能
并发。调度顺序不得改变Evaluation inputs，且不进入durable identity。

`KeepUnchanged` 仍表示 Maintainer 已读取当前 segment 后明确判断正文无需改变。它复制 prior content，但产生绑定
当前 row/EvaluationKey 的新 Cell；不能把它偷换成“没有执行”或直接复用旧 Cell。

## 3. Cadence 的 implemented target 语义

本节的R/B与authority边界已由A0-A2实现；它仍不等于C2 built-in、C3/C4长期容量或C5 actual activation已经完成。

### 3.1 两个独立量

```text
R = MinimumRecentHistoryLoad = 24,000
B = RecapBuildIntervalHistoryLoad = 60,000
```

- `B` 是一条新 Timeline row 目标吸收的 HistoryLoad；不是 exact size，因为只能在 replay-safe boundary 切分。
- `R` 是 row 之后仍须保留给主 Agent 的 recent raw HistoryLoad 下限。
- `HistoryLoad` 是 estimator-scoped provider-neutral 单位，不等于 provider token、账单、event count 或 context bytes。
- 理想边界恰好可切时，历史达到`R+B = 84,000`后，在下一次provider-facing context construction前的eligible
  lifecycle trigger提交第一条row；以后每新增约60,000，再在后续eligible trigger提交一条约60,000的row。

### 3.2 Exact seal eligibility

对当前 selected Timeline head 之后的 pending suffix，定义：

```text
G = pending suffix 的总 MeasuredHistoryLoad
L = first replay-safe candidate 实际吸收的 HistoryLoad，L >= B
```

一条 candidate 只有同时满足下式才可提交：

```text
L >= B
G - L >= R
```

因此 `84,000` 只是 `L == 60,000` 时的理想门槛。若 dependency closure 使 first replay-safe candidate
overshoot 到 61,000，则 pending 84,000 时只剩 23,000，必须继续等待到至少 85,000。

### 3.3 期望时间线

假设 replay-safe boundary 恰好在目标点；表中`pending HistoryLoad`是一次eligible lifecycle trigger捕获到的值：

| pending HistoryLoad | 期望 row commits | 期望 retained raw tail |
|---:|---:|---:|
| 59,999 | 0 | 59,999 |
| 60,000 | 0 | 60,000 |
| 83,999 | 0 | 83,999 |
| 84,000 | 1 | 24,000 |
| 120,000 | 1 | 60,000 |
| 144,000 | 2 | 24,000 |

每次成功提交都保证 recent raw tail `>=R`。停止时若下一条candidate实际大小为`L_next`，则tail满足
`tail < R + L_next`；只有理想的`L_next == B`边界下，稳定tail才位于`[R, R+B)`。无论overshoot多少，
都不是把每条row改成吸收`R+B`。

### 3.4 职责边界

已实现边界如下：

- Timeline partition policy 继续 durable 持有 `B`、estimator identity、segment raw/rendered caps，并确定
  first replay-safe `>=B` 的 row boundary。
- `R` 属于per-Ref repo-owned RecapGrid Cadence policy，而不是SessionJournal RuntimeConfig或row正文身份；
  它不能把row target从60k改成84k。canonical V1还绑定RefId、monotonic generation、partition algorithm、
  estimator、B与segment caps，物理位置为`control/recap-grid/v1/refs/<ref>/cadence/cadence.json`。
- SessionJournal raw owner只提供owner-bound planning window、selected-lineage cursor与captured-head fence；它不直接拥有
  `HistoryLoad`算法，也不形成SessionJournal→HistoryTimeline反向依赖。
- HistoryTimeline estimator/policy owner对该window测量candidate与suffix；seal orchestrator只有拿到exact candidate、
  exact suffix-after-candidate measurement以及raw owner authority proof后才可提交row。
- Online bounded、Online audited/offline、CLI online sync、CLI offline sync复用Cadence-owned seal operation；
  同一次multi-row loop冻结同一cadence snapshot，不能有任一writer继续只按`B` drain。
- Timeline B-only `PlanNextRow`/`CommitRow`/offline builder已关闭为internal friend surface；public mutation必须经
  Cadence seal operation及其non-forgeable operation token/reserve proof，operation dispose后candidate不可commit。
- commit 后重新 capture whole raw/Timeline heads再判断下一 row；stale/limit均 typed fail closed。
- Getter在同一owner lifetime内读取Cadence、Timeline、Control与Store：先验证current/crossed fulfillment完整性，
  再选择latest R-eligible fulfilled anchor，然后应用`NthPrevious`。健康ledger尚无R-eligible row时返回typed
  `ReserveBootstrapRawOnly`；SessionJournal只在同次lifecycle gate授权后折叠anchor之后到completion boundary的raw tail。

Cadence state由mutable SessionJournal owner授权创建/CAS；reader inspect/open为no-create。`cadence set-reserve`只改变R，
要求exact Ref、expected generation与domain digest，并保留B/estimator/algorithm/caps；下一operation才观察新generation。
R与Cadence digest不会进入RowId、HistorySegmentDescriptorDigest、EvaluationKey或CellDigest，所以只改变retention policy
不会重写同一raw segment的认知。Prepared恢复继续只使用frozen request/context；Started Refuse仍早于current derived access。

### 3.5 已有 nonempty Timeline 的 reserve 兼容语义

只在未来seal时强制`G-L>=R`仍不够。Cutover前已经按B-only规则生成的row、部署后提高R、branch rewind到row边界，均可能产生
“current row健康且fulfilled，但其anchor到completion的tail小于R”的状态。若Getter仍固定选择current row，主请求会违反R；若
把它当unfulfilled阻止raw append，系统又会永久等不到更多raw，形成死锁。

已实现selection规则是：

1. 从latest healthy fulfilled selected row向predecessor回退，选择第一个使`anchor→completion` raw tail满足`>=R`的row；
2. 再以该R-eligible row为基点应用显式`NthPrevious`；R-eligibility本身不能用`NthPrevious=1`冒充；
3. 如果Timeline已有健康row但尚无任何R-eligible predecessor（典型是只有first row），返回独立typed
   `ReserveBootstrapRawOnly`，允许raw继续增长；它不是unprovisioned、unfulfilled、corrupt或ordinary empty-lineage；
4. missing Fulfilled、broken predecessor、wrong recipe/target、corruption与authority drift始终fail closed，绝不能借
   `ReserveBootstrapRawOnly`退回raw；
5. 一旦有R-eligible fulfilled row，后续selection自动回到selected recap + raw tail，不需要重写旧row identity。

该兼容层让immutable B-only ledger、R升级和rewind可以安全过渡；fresh genesis仍应从第一条row起执行§3.2的新seal规则。

## 4. Current implementation audit

### 4.1 已经正确的部分

| 能力 | Current 状态 |
|---|---|
| raw selected-lineage authority | 已实现，Timeline/Grid不能替代 raw |
| first replay-safe `>=B` partition | 已实现，row boundary由raw+policy确定 |
| exact descriptor rematerialization | 已实现，commit前复验 raw range/policy/heads |
| previous-row full projection | 已实现，normal Full row的两列读取上一row全部ordered cells |
| same-row sibling-independent evaluation | 已实现；同batch共享prior/history输入，不引入sibling输出依赖；同route的实际provider dispatch为leader→followers |
| `KeepUnchanged` provenance | 已实现，仍生成current-row Cell |
| Getter anchor + SessionJournal raw tail | 已实现 no-gap/no-overlap ownership |
| missing-only restart | 已实现；已存在exact Cell不重发provider |
| Prepared/Started recovery boundary | 已实现；Prepared exact bind，Started Refuse不进入current derived path |

### 4.2 Cadence closure 后的current shape

durable Timeline partition policy仍只有：

```text
PartitionAlgorithmId
HistoryLoadEstimatorId
TargetHistoryLoad
MaxRawEvents
MaxRenderedBytes
```

`MinimumRecentHistoryLoad`刻意不进入partition revision，而由Cadence V1在同一个domain digest中提交上述五字段的
exact期望值加R。`BeginTimelineSeal`只有在Cadence mapper得到的partition revision与active Timeline policy exact匹配时
才签发operation；partitioner仍选择first replay-safe B boundary，seal admission另证明`G-L>=R`。Online/CLI共享该
online/offline facade，Getter共享Cadence snapshot与Timeline policy fence。

下列做法仍然禁止：

- 把 `TargetHistoryLoad`改为84,000；这会让每条row变成约84k且仍可能留下0；
- 设置`NthPrevious=1`；一整条旧row不是exact 24k，首row也会OrdinalUnavailable；
- 用canonical request byte cap代替R；byte cap是上界，R是HistoryLoad下界；
- 绕过Cadence facade另建B-only writer；
- 只判断`G >= R+B`；replay-safe overshoot仍可能破坏R。

### 4.3 `128`、`4,096`、`65,536` 与 `1,000,000`

这些数必须分开解释：

| 数值 | Current owner | 实际语义 |
|---:|---|---|
| 128 | Timeline `MaximumPathPageRows` | Reader/export单页row数；Manager会继续翻页，不是Timeline寿命 |
| 128 | Store `MaximumPageItems` | inspect/export/verify单页items；不限制normal inserts总数 |
| 128 | Getter `MaximumProvenanceRows` | full-chain诊断读取预算；超出后materialization仍可Available，但provenance为Incomplete |
| 4,096 | Getter `MaximumNthPrevious` | 可请求的strict predecessor ordinal上限，不是Timeline总row数 |
| 1 / 256 | Online每pass Timeline/recipe-row上限 / Host同请求catch-up pass上限 | 每pass最多seal一条Timeline row并发布一个recipe-row；只有typed continuation可在同请求内继续 |
| 128 | Online单recipe-row new-call上限 | 等于单recipe最大column数；不是durable累计cap，elapsed只在safe Manager边界soft检查 |
| 65,536 | Timeline per-segment `MaximumRawEvents` | 单个history segment允许吸收的raw event硬上限；不是row数或ledger寿命 |
| 256 / 4,096 / 4,096 | Control Family / Definition / Recipe caps | 单个Control state的catalog硬上限 |
| 16,384 / 32 MiB | Control terminal receipt count / state bytes | operation replay证据与whole state硬上限 |
| 262,144 | Timeline `HistoryRecentReserveOperationLimits.MaximumRawEvents` | seal、build-read anchor、Online与CLI共用的单次recent-reserve operation cap；达到后typed limit/backpressure，不等于partition segment cap |
| 1,000,000 | Control `MaximumBootstrapRows` / projected calls admission maximum | 单recipe注册时的cost/admission上界，不是Timeline或Grid累计寿命 |

所以“max rows 128”是误解。C3A已删除Grid Store的累计artifact count与8 GiB lifetime cap，C3B又以immutable
per-row assignment取代Manager whole-root freeze，删除`MaximumSelectedRows`，因此第4,097行不再有Manager语义上的
永久terminal。normal reopen会从exact healthy anchor只收集未建suffix；`MaximumRecipeRowSteps`只限制本次成功发布的
recipe-row assignments。

C3D将Timeline hard cut到Schema V2：mutable selected path由whole-head count/root commitment与O(log N) Merkle accumulator
证明，删除累计policy/row/node count和database/restore总bytes code-owned lifetime caps。真实public 4,097-row CLI sync与
Manager vertical现已通过；65,537 durable rows也通过reopen、path首尾、rewind/reselect、verify、backup/restore。4097/8194/65537
ledger约21.35/42.72/341.40 MiB，append约7.2/13.7/107秒；这关闭旧O(n²)fixture blocker，但不把SQLite/filesystem容量
虚称为无限。operation/page/artifact/raw caps继续作为资源边界，disk full/SQLite full/I/O仍typed fail closed。

### 4.4 reset / abandon 不是 rollover

- Grid reset只换成空Store identity；Timeline与Control不变。它是exact-confirm的destructive derived rebuild/recovery primitive，
  不只用于corruption。reopen后Manager仍须从root增量发现并重建全部rows；单operation budget会产生typed continuation，
  但不再有累计count/database-byte lifetime terminal。
- Timeline abandon创建新的empty TimelineId并切locator；旧ledger保持inert。Control recipes绑定旧TimelineId，不能自动复用。
- current first row只有`FirstRow` prior，没有“从上一generation的两列projection开始”的durable bootstrap contract。

因此 reset是destructive whole-Store replacement，abandon是显式authority replacement；两者都不是retention、GC或无缝rollover。

### 4.5 Current evidence map

上述current判断的主要owner pointers如下；它们用于复核本文，而不是把行号冻结成新authority：

- first-safe B partition：[`HistoryPartitioner.Partition`](../../../../prototypes/SessionJournal.HistoryTimeline/HistoryPartitioner.cs)；
- durable per-Ref R/B authority与seal facade：[`RecapGrid Cadence`](../../../../prototypes/SessionJournal.RecapGrid.Cadence/)；
- Online immediate commit loop与offline fallback：[`RecapGridOnlineContextHandle`](../../../../prototypes/SessionJournal.RecapGrid.Online/RecapGridOnlineContextHandle.cs)；
- CLI online/offline writer：[`RecapGridTimelineSyncCommand`](../../../../prototypes/SessionJournal.Cli/RecapGridTimelineSyncCommand.cs)；
- current Timeline artifact/page resource caps：[`HistoryTimelinePersistenceContracts`](../../../../prototypes/SessionJournal.HistoryTimeline/HistoryTimelinePersistenceContracts.cs)；
- reserve-aware build-read anchor：[`HistoryRecentReserveAnchor`](../../../../prototypes/SessionJournal.HistoryTimeline/HistoryRecentReserveAnchor.cs)；
- reserve-aware selection与`ReserveBootstrapRawOnly`：[`RecapGridContextHandle`](../../../../prototypes/SessionJournal.RecapGrid.Getter/RecapGridContextHandle.cs)；
- Manager head-to-anchor progression：[`RecapGrid Manager`](../../../../prototypes/SessionJournal.RecapGrid.Manager/)；
- previous projection与`KeepUnchanged` Cell创建：[`ManagerRowBuild`](../../../../prototypes/SessionJournal.RecapGrid.Manager/ManagerRowBuild.cs)；
- Store whole-database counters：[`StoreContracts`](../../../../prototypes/SessionJournal.RecapGrid.Store/StoreContracts.cs)；
- Getter ordinal/provenance caps：[`GetterContracts`](../../../../prototypes/SessionJournal.RecapGrid.Getter/GetterContracts.cs)；
- C2 shared Family prompt：[`recap maintainer family`](../../../Galatea/prompt/recap-maintainer-family/system-zh-cn.md)；
  两列内容prompt：[`world-understanding`](../../../Galatea/prompt/world-understanding-maintainer/rewrite-zh-cn/user.md)
  与[`autobiography`](../../../Galatea/prompt/autobiographical-maintainer/rewrite-zh-cn/user.md)。C2A把旧两份专业zh-CN system规则迁入
  对应user prompt后删除旧system文件；Git历史保留迁移证据，current source不留双authoring owner。

## 5. 长期运行的目标边界

“长期”应解释为：每次operation有明确预算和backpressure，但系统可以通过后续operation或显式generation transition继续，
不存在累计到固定row数后只能永久停机的业务语义。

### 5.1 Incremental Manager hot path

normal build应从 exact selected predecessor fulfillment/checkpoint增量构建未完成suffix，而不是每次从root重新冻结全链。
这之前需要一个C3 contract decision：current Store只能按已知RowViewDigest读取view，或按包含whole Timeline generation的完整
FulfilledViewKey读取mapping；仅凭previous row descriptor无法定位历史fulfillment。必须选择canonical progression checkpoint + CAS，
或建立按exact recipe/row authority查询的bounded validated index：

- checkpoint必须绑定Timeline membership/witness、recipe/target、previous-view recurrence、Store identity与whole authority fences；
- 不得扫描“latest”、mtime、任意view或模糊recipe寻找起点；
- 多个合法-looking checkpoints必须fail closed；policy CAS不加row、一次前进多row、branch/rewind、overlay/base closure与Store reset
  都必须有明确语义；
- full root rebuild继续作为offline verification/recovery path，不应是normal每row成本；
- operation budgets耗尽应形成可重入的partial progress，而不是第4,097 row后的永久 terminal。

### 5.2 Timeline-instance / epoch rollover and retention

Timeline V2已删除固定累计row/database cap，因此rollover不再是跨过65,536的前置条件。若未来为retention、归档、
physical compaction或bounded recovery horizon引入generation transition，仍必须满足：

- 本文的epoch/Timeline-instance不得与现有`TimelineHead.Generation`混称；后者只是同一Timeline的head transition counter；
- carry-forward checkpoint至少提交旧 Timeline/head/row/view proof、两列ordered contents/digests、raw boundary及该boundary的
  exact setup/anchor state；
- 新Timeline从该raw boundary继续，而不是重新从SessionCreated扫描；
- 新generation首row需要authenticated bootstrap projection，不能伪装成`FirstRow`；
- 新Control recipe、Store seed、locator/active切换必须有exact authority、operation receipt和crash settlement；这是跨三domain的
  crash-recoverable staged transition，不是假装成一个SQLite transaction，normal Host只能看到old-complete或new-complete；
- 旧generation先归档/inert；在retention contract决定前不能静默删除。

derived GC还需先裁决用户承诺：只长期保存active predecessor + supported `NthPrevious`窗口，还是保存所有从未进入main
request的中间Cells/Views。Prepared request/context bytes本身已自足，recovery不要求保留Grid artifacts；若未来还要用Grid
digest做额外审计，必须另行定义durable reference与“缺失只损失审计、不损失恢复”的结果。因为LLM重建不是byte-identical，
不能先删再宣称可恢复同一历史认知演化。Current Store没有artifact删除API；GC必须是新schema/maintenance工作包，按共享Cell
reachability与FK做exact mark-sweep，不能在normal runtime opportunistic delete。

## 6. 成本模型与单一 shared route

现有透明成本模型在理想可连续切分/`L=B`近似下，把recent suffix视为从`R`增长到`R+B`，平均为`R+B/2`。
一般replay-safe overshoot下它不是runtime精确均值。该近似只有§3 reserve invariant落地后才有意义；当前实现会从接近0
增长到B，不能拿现有runtime evidence反向证明该模型。

模型与浏览器calculator当前仍位于
[`SessionJournal.HistoryTimeline/tools`](../../../../prototypes/SessionJournal.HistoryTimeline/tools/)。calculator页面中的18k/21k
只是旧illustrative defaults，不是production authority；用户本轮确认的审阅目标是24k/60k。

`HistoryLoad`到provider tokens的换算、output size、cache TTL、cache creation/read价格与实际hit rate仍需真实telemetry校准。
两列使用同一个shared Family route后，应至少分两种成本情景。首次activation默认把该route配置到Opus 4.6，
但成本模型必须接受runtime实际选择的provider/model价格与telemetry，不能把Opus 4.6写成semantic identity：

```text
S  = exact shared prefix tokens
Ti = 第i列独有tail tokens
Oi = 第i列output tokens

未命中cache：
  input = basePrice * (2*S + T1 + T2)

已验证共享prefix cache：
  input = cacheWritePrice*S + basePrice*Tleader
        + cacheHitPrice*S   + basePrice*Tfollower

output = outputPrice * (O1 + O2)
```

Runtime复用同一个prefix对象并发出cache hint，不等于provider一定命中cache。只有真实usage明确报告creation/read或等价
provider evidence时，才能采用第二种账单模型。模型价格会变化，不能把本次价格记忆写入durable config或Cell identity。

## 7. Recovery 与 Context 不变量

任何cadence/capacity重构都必须保持：

1. Cadence A1把所有Timeline writers统一到reserve-aware seal；A2把Getter reserve结果与SessionJournal neutral lifecycle
   放进同一次authority gate。`PreObservation`可seal，`ObservationAccepted`/dependency-closed `ToolResultObserved`保留
   新event为raw tail并做readiness；它们不会走另一条B-only writer。
2. 每次provider-facing context construction以operation捕获的raw head和一次冻结的cadence snapshot检查R；
   Observation/ToolResult不得被错误吸收到本应保留的recent tail，dependency尚未闭合的中间ToolResult不单独seal。
3. Fresh/NewRequest在operation开头冻结raw head、governing setup/R与estimator。一次multi-row loop中expected Timeline whole head
   会随每次自身commit推进并逐iteration重绑；Control head属于最终readiness fence，不进入B/R candidate identity。
4. Prepared恢复只使用raw中已冻结的request、context snapshots、completion/tool identity；不打开current Timeline/Grid/Control/route。
5. Started Refuse早于本次current connection selection/client、route与derived access；explicit restart仍以Prepared frozen bytes为源。
6. active recipe + nonempty Timeline 的missing/partial/Invalid不能因reserve不足而fallback到raw-only。
7. empty Timeline在达到第一条eligible row前使用ordinary raw-only bootstrap；已有健康rows但没有R-eligible anchor时只允许§3.5的
   `ReserveBootstrapRawOnly`。两者都不能掩盖corruption、unfulfilled或authority drift。
8. 满足R后若final canonical request又超过byte cap，必须在Observation append前typed fail closed；不得静默缩短R。

## 8. 禁止的局部修补

后续重构不得采用以下“顾此失彼”方案：

- 用84k取代B=60k；
- 用`NthPrevious=1`、request byte cap、event count或provider token近似R；
- 为满足R而选择一个不同的非first-safe B boundary，使同raw/policy的row identity依赖任务启动时间；
- 只修online fast path，遗漏offline audit与CLI writer；
- 只给future seal加R gate，却不处理已有B-only rows、R升级或rewind后的selection bootstrap；
- 只修已知caller，却继续允许public Capture/Plan/Commit绕过reserve authorization；
- 让SessionJournal core直接实现HistoryLoad estimator，制造raw owner到Timeline policy的反向依赖；
- 把R放进Cell/Definition/route identity，导致retention policy变化触发内容重算；
- 把单次budget删成“无限”，失去故障隔离；正确方向是bounded incremental continuation；
- 只提高4,096/65,536常量，继续保留whole-root算法和永久cap；C3B/C3D已经分别删除这两类semantic cliff；
- 把Grid reset或Timeline abandon包装成rollover；
- 为节省Cell数把`KeepUnchanged`改成未执行/旧Cell直接复用；
- 让同一row的第二列依赖第一列刚生成的结果，却仍声称两列并行共享同一Evaluation输入；
- 把shared prefix构造或cache hint当成真实provider cache hit；
- 删除old derived artifacts后声称能通过非确定性LLM byte-identical恢复历史认知；
- source tests green后直接写actual cyber repo，跳过disposable real-provider canary与停服备份。

## 9. 重构工作包与顺序

### A0：Repo-owned Cadence authority（complete）

- 独立`SessionJournal.RecapGrid.Cadence`拥有per-Ref canonical V1 policy、generation/domain digest、strict Linux durability、
  mutable-owner CAS与reader no-create；R不是RuntimeConfig字段；
- physical leaf为`control/recap-grid/v1/refs/<ref>/cadence/`，不与可reset Grid root或Control state混写；
- operator目标policy冻结为B=60,000、R=24,000；codec仍允许显式operator policy用于fixtures/未来migration。

### A1：Reserve-aware Timeline seal（complete）

- Timeline保留first-safe B boundary；Cadence seal operation冻结一次snapshot并要求active partition policy exact；
- Online/Offline/CLI writers统一消费`G-L>=R`proof，public B-only mutation surface已关闭；
- offline cursor跨页只在replay-safe boundary推进，open dependency与operation cap返回typed proof unavailable；
- raw/Timeline/Cadence drift、operation dispose与wrong repository/token均zero mutation/fail closed。

### A2：Reserve-aware Getter/context（complete）

- build-read session、Online、CLI与Getter anchor共用262,144 raw-event operation cap；不再把partition `MaxRawEvents`误作whole suffix cap；
- Getter先验证current/crossed Fulfilled/View/Cells健康，再找latest R-eligible anchor，再应用Nth；
- 无eligible predecessor但authority健康时返回`ReserveBootstrapRawOnly`，missing/corrupt/unfulfilled仍fail closed；
- SessionJournal neutral lifecycle在同一次gate授权raw history；Galatea readiness保留独立reserve-bootstrap state。

A3增加provider-free `recap-grid cadence inspect|set-reserve` operator surface；`set-reserve`只CAS更新R，不能改B。

### C2：Galatea rolling built-in and route（activation blocking）

- C2A-C2C source已由commits `bf4beff0`、`eb3743dd`、`62b93f9a`及其closure tail实现，两路independent review均GO；
- `galatea-rolling-rewrite-zh-cn-v1` operator asset提供一个shared Family、两Definition和provider-free Full recipe composition；
- 固定ordered targets、strict canonical bytes/goldens与runtime identity；
- 两列capability显式`SemanticModelId=null`；runtime route/config默认选择Opus 4.6但允许以后切换model，无fallback、
  provider/client保持lazy，actual provider/model/connection只进入operation evidence；
- fake Host已证明previous-row rolling、same-row shared prefix、Keep、missing-only restart及model A/B只补missing；
- 用真实Opus 4.6 disposable clone证明terminal protocol、输出质量、cache/usage、latency和费用。

### C3：Incremental Manager and capacity observability

- C3A已落immutable per-row assignment、exact recurrence与Store lifetime-cap removal；
- C3B已落head-to-anchor minimal suffix、overlay独立anchors与one recipe-row budget，删除Manager
  `MaximumSelectedRows` semantic cliff；
- C3D hard cut Timeline V2：V1 root inert/no fallback；mutable selected path以whole-head count/root与O(log N) Merkle
  commitment取代immutable trie snapshot，删除累计row/node/database-byte lifetime cap；
- public 4,097-row Timeline/Manager vertical与65,537 durable reopen/path/rewind/reselect/verify/backup/restore均已通过；
- readiness/CLI pure read报告Timeline/Cell/View/Fulfilled、Control Family/Definition/Recipe/receipt/state bytes、offline-audit
  progress与各自watermarks；不得只显示最先撞到的Store cap；
- full rebuild仍可bounded offline执行。

### C4：Timeline-instance/epoch rollover and retention

- 定义含setup/anchor state的carry-forward checkpoint、new-epoch first prior、staged old-or-new switch与crash recovery；
- Control catalog/active recipe与仍需replay的terminal receipts通过exact staged transition进入new instance；已settled旧receipt按明确
  retention/compaction proof退休，不能靠清空Control规避16,384/32 MiB cap；
- offline reconcile/audit以owner-bound、raw-head-bound checkpoint做bounded streaming continuation；262,144是单operation预算，
  不能成为长repo永远无法reconcile的总历史上限；
- 裁决Prepared引用、NthPrevious支持窗口、archive/export与derived GC承诺；
- 用小caps fixture验证exact/cap+1、wrong checkpoint、crash before/after publish与旧generation inert。

### C5：Cyber rebuild and production activation

- 优先从`prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json`
  作为用户指定的cleaner-base candidate新建SessionJournal repo；这是一项activation选择，不是本文对旧事故根因的证明；
- `prototypes/Galatea/.atelia/galatea/sessions/cyber-session-journal`只作为旧repo备份/审计输入；旧derived正文不得迁入新Grid。
  用户已明确允许舍弃疑似受故障运行影响的一段经历，但真正切换前仍须exact确认import文件hash、selected Ref、目标repo与停服窗口；
- 上述`.atelia`路径均为operator-local ignored data，不属于fresh checkout/source gate证据；runbook不能假定另一台机器存在同样文件；
- provider-free阶段完成import validation、Timeline/Cadence/Control/Grid create、sync、provision、recipe与fake vertical；derived-only步骤不改raw；
- 真实调用与actual repo操作以[Galatea G2A staging acceptance](../../operations/galatea-g2a-staging-acceptance.md)为外部门禁：
  先取得用户对exact disposable clone、provider/model、maximum calls/estimated cost、无自动retry与日志不落secret的明确授权；
- 在disposable clone上用`prototypes/Galatea/.atelia/galatea/connections.json`和Opus 4.6执行bounded rebuild；
- canary通过后再次取得actual activation确认；停服、备份actual repo/config并确认selected Ref/raw head/phase，才替换正式cyber repo；
- 首次new raw append前可以回退binary/repo selection；append后不得用旧backup覆盖raw，只能证明旧binary可replay或forward-fix。

A0→A1→A2与C2A-C2C source closure已经完成；C2D仍是真实LLM写入前的硬门禁。C3D已由commit `7a9c0b3b`完成；C3C orchestration
已Complete且两路independent closure均GO。
C4是retention/rollover与跨operation recovery优化，不再是跨越固定65,536 lifetime cap的前置条件；是否作为首次activation门禁
取决于用户要求的retention/rollback horizon，不能再以旧累计cap论证。

## 10. 最小验收矩阵

### Cadence

- 83,999：0 commit；84,000：1 commit/tail 24,000；120,000：1 commit/tail 60,000；
  144,000：2 commits/tail 24,000。
- candidate 61,000 + pending84,000：0 commit；pending85,000：1 commit/tail24,000。
- online bounded、online audited/offline、CLI online、CLI offline产生相同row descriptors与commit count。
- restart/reopen、branch/rewind、raw/T/C drift均保持exact结果或typed zero-mutation failure。
- 不同R对同一raw+B candidate产生相同descriptor/RowId，只改变何时获得seal authorization。
- Cadence generation/domain/policy drift、R cap/cap+1、unsupported schema与public Coordinator绕过均fail closed。
- proof后pre-commit crash为zero mutation；commit-indeterminate/post-commit crash reopen后只接受old或exact-new head。
- existing B-only ledger、部署后R提高、rewind到row边界时，选择latest R-eligible fulfilled predecessor；只有健康且尚无
  eligible predecessor才返回`ReserveBootstrapRawOnly`，missing/corrupt不得fallback。
- ObservationAccepted与dependency-closed ToolResult让历史越过门槛时，下一次provider-facing context construction前完成
  reserve-aware seal，且新event仍位于retained raw tail；dependency未闭合的中间ToolResult不seal。

### Rolling two-column Grid

- row0两列均为FirstRow；row1两列看到相同且包含row0两列正文的ordered prior。
- 任一列不读取current-row sibling输出；row2可以看到row1双方更新。
- `KeepUnchanged`正文相同，但EvaluationKey/CellDigest变化且CellCount增加。
- fake provider验证相同route key可映射到不同runtime model且durable identity不变；首次real canary使用配置中的Opus 4.6
  connection。missing/no-build/readiness保持零client construction。

### Context/recovery

- selected anchor后的raw tail无gap/overlap且每次seal后独立测得`>=24,000`。
- PreObservation、ObservationAccepted、dependency-closed ToolResult三种trigger保持同一row identity/reserve规则；
  ToolContinuation recovery仍保持frozen profile/completion顺序。
- Prepared在R/current config/Timeline/Grid均缺失或变化时仍按frozen request恢复；Started Refuse零current derived/client读取。
- empty raw-only、`ReserveBootstrapRawOnly`、active unfulfilled、reserve not reached、corruption五类结果不可相互fallback。

### Capacity

- 128/129 path rows证明128只是分页；provenance 129仍Available但明确Incomplete。
- Manager以多row、restart、branch/rewind、nested overlay与anchor corruption fixtures锁定incremental suffix；
  C3D已恢复真实4,097+ Timeline/Manager integration，65,537 gate另锁reopen/path、rewind/reselect、verify与backup/restore。
- V2 selected-path normal ReadSelectedRow/page/reconcile均验证assignment、whole-head count/root与Merkle proof；middle delete、
  ordinal swap、off-branch insert sticky Invalid，page复用operation-local proof cache，节点总量保持O(N)。
- Control catalog/receipt/state byte caps与offline audit event cap做exact/cap+1 typed backpressure，不能被误报为Timeline寿命；
  随后用new instance/compaction与checkpointed audit跨多个operation继续到成功，证明cap不是永久停止点。
- rollover前后previous projection、raw boundary与active recipe exact连续；wrong authority与crash不能发布半generation。

### Activation

- clean legacy export hash、import event/ref/phase validation；derived stages不改变raw bytes。
- real-provider canary在用户授权的exact disposable clone与call/cost上限内运行，记录provider/model/connection fingerprint、calls、
  usage/cache evidence、elapsed与输出bytes；不自动retry、不记录secret。
- 两列内容人工审阅：旧自传/世界理解中仍有效的信息被承继，新segment被整合，两列职责没有串位。
- actual cutover记录停服、备份、old/new repo fingerprints、首次new raw write与rollback/forward-fix边界。

## 11. 后续 retention / activation 审阅清单

以下项目不阻塞C2 source实施，但在对应retention/activation阶段仍需用户确认或现场authority：

1. 长期保留承诺：是否需要保存所有中间Cells/Views，还是保留active/Nth/Prepared所需窗口并允许显式GC。
2. C4 generation rollover是否为首次production activation硬门禁，还是允许在明确容量水位下先做bounded pilot。
3. 用户已允许放弃疑似故障期经历；activation时仍需确认clean import文件hash、selected legacy Ref、目标repo与停服时点。

shared Family与prompt来源、model/runtime identity边界已经裁决：Opus 4.6不是durable semantic identity，C2使用
`SemanticModelId=null`；model切换后保留既有Cells并由新model补missing work也属于已接受的runtime provenance。
C2首版32 KiB/列、world-first顺序与prompt规则迁移作为工程决策处理。R=24,000、B=60,000及repo-owned Cadence authority
也已不再是待裁决项。C3/C4/C5其余裁决仍未完成；
本文不授权修改actual cyber repository或发起真实provider调用。
