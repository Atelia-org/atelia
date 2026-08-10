# DerivedRecap Sparse Versioned Grid 目标设计

状态：Proposed target design；independently reviewed；尚未实施，不描述 current production

## 1. Intent

重新定义 DerivedRecap，使 History 分段、Maintainer 状态、执行批处理和对外 Context 视图彼此正交。

核心模型是一张稀疏、可版本化的二维表：

- row 是一个已经确定边界的 `HistorySegment`；
- column 是一个 `MaintainerDefinitionRevision`；
- cell 是该 Maintainer 对该段 History 做 rolling maintenance 后产生的 immutable recap；
- `HistorySegmentContent` 是 raw SessionJournal 的按需 View，不是值拷贝；
- 同一 row 的 cells 都只依赖上一 row 的选定 recap view 与本 row 的 HistorySegment，因此可以并行执行；
- 一组 cells 恰好共享 prompt prefix 或一次并行执行，只是 runtime 优化，不决定 durable identity。

本设计先定义理想目标，不保留 current complete-roster epoch、v8/v9 wire、repair/reseal 或 migration 兼容层。
实现迁移另行规划。

## 2. Goals and non-goals

### 2.1 Goals

1. `HistoryTimeline` 不知道有哪些 Maintainer 存在，只确定性地划分 raw History。
2. 新 Maintainer 可以从 Timeline 起点逐 row 回放，只填自己的 column。
3. 单个 Maintainer 的 prompt/capability 新版本可以独立 rebuild、比较和 promotion。
4. 需要 cross-maintainer 信息融合时，可以从某 row 起逐 row rebuild 一个完整 Grid revision。
5. 主线 Context 可以读取某个 row 对exact BuildTarget membership-complete的视图，并分别检查其GridBuildRevision
   provenance与当前row的prior-view alignment。
6. 缺失、损坏或取消的 derived cell 可重新生成；raw selected lineage 仍是正文事实 authority，Timeline ledger
   是既有分段决策的 authority。
7. 同 row、同 family、同 history segment 的工作仍可并行并共享 completion prefix。
8. durable model 的独立语义概念、状态数和 authority 路径必须受到明确预算约束。

### 2.2 Non-goals

- 不在本设计中决定旧 DerivedRecap 如何迁移或兼容；旧 sidecar 可以 reset/rebuild。
- 不让 Agent 在运行时生成任意代码、system prompt 或 tool schema；动态创建首先是受控 family 的声明式实例。
- 不承诺 exactly-once Maintainer 调用。cell 未 durable commit 时允许安全重试并产生重复远端调用。
- 不让 Timeline 持久化 `HistorySegmentContent` 正文。
- 不在首版支持任意 cell dependency graph、同 row 互相依赖或跨多 row 的自由引用。
- 不把 scheduler 的 family/lane/cache 状态写入 cell identity。

## 3. Mental model

```text
                         columns / Maintainers
                 A                  B                  C
          +----------------+------------------+------------------+
row 0     | A(0)           | B(0)             | C(0)             |
          +----------------+------------------+------------------+
row 1     | A(1)           | B(1)             | C(1)             |
          +----------------+------------------+------------------+
row 2     | A(2)           | B(2)             | C(2)             |
          +----------------+------------------+------------------+

virtual first column: HistorySegment(row), materialized from raw on demand

Cell(column, row) =
  Maintainer(
    Definition(column),
    HistorySegment(row),
    SelectedRowView(row - 1)
  )
```

同一 row 没有 cell-to-cell dependency。所有 cross-maintainer 信息都来自上一 row，因此依赖图是严格向前的
wavefront，而不是循环图。

## 4. Ownership

### 4.1 HistoryTimeline

只拥有 raw History 的分段事实与读取能力：

- 根据 selected `RefId` Parent lineage、HistoryLoad estimator、BuildInterval 与 replay-safe boundary 形成 row；
- 为每个 row 持久化不可变 `HistorySegmentDescriptor`；
- 按 descriptor 从 raw materialize 并验证 `HistorySegmentContent`；
- 列举 row、定位相邻 row、判断当前 tail 是否足以形成下一 row；
- 对 rewind/off-lineage descriptor fail closed，不静默映射到另一条 lineage。

Timeline 不引用 Maintainer catalog、Completion runtime、RecapGridStore 或 Context block。

已经封口的row边界是耐久历史决策，不随DerivedRecap cell reset或当前配置改变而丢失。Timeline因此拥有独立
ledger和生命周期；raw保留History正文authority，Timeline ledger保留“过去怎样分段”的authority。Timeline在
branch/rewind下形成row DAG：若fork落在旧row内部，只复用共同boundary之前的row prefix，并从最后共同boundary
创建新row chain；旧row不改写。`RowId + predecessor chain`才是身份，禁止用全局ordinal或“最新文件”猜测path。
Timeline ledger为每条选定path维护CAS更新的`TimelineHeadRef`；同一predecessor出现多个合法successor时，只有显式
head选择决定哪条chain服务当前selected raw ref。

首版`TimelineId`绑定一个exact `RefId`，只在同一Ref内复用row prefix；不同Ref即使共享raw prefix也各自建立
descriptor chain。跨Ref dedup延后，避免把Ref selection从row identity中再拆出一层映射。

### 4.2 MaintainerControlLog / MaintainerManager

`MaintainerControlLog` 是定义、Grid build revision与active revision选择的唯一逻辑authority；
`MaintainerManager` 是执行协调器：

- 注册 logical column 与 definition revisions；
- 区分 runtime capability catalog、active/live definition 与 candidate definition；
- 查询缺失 cells，按 row wavefront 调度；
- 为同 row 的 work items做 family/lane/shared-prefix batching；
- 管理单列 bootstrap/rebuild、full-grid rebuild、A/B candidate 与 promotion；
- 不决定 History row 边界，不拥有 raw History。

它只需要三个mutating operations；读取面是返回definitions、revisions与active revision的immutable `ReadSnapshot`：

```text
RegisterDefinition(definition)
RegisterGridBuildRevision(revision)
CompareExchangeActiveRevision(expectedRevisionDigest, nextRevisionDigest)
```

Galatea 自主创建 Maintainer 的决定必须进入该control log。raw SessionJournal action、独立catalog journal与
versioned operator config只是三种候选物理carrier；一次production composition必须且只能选择其中一个，不能同时成为
authority。Derived Grid Store不能成为该决定的唯一真相源，否则 reset 后定义与active选择会丢失。

动态注册只允许引用allow-listed `FamilyDefinitionDigest`并提交受限DeclarativeSpec；control plane必须校验column count、
topic/prompt data长度、replay/call预算、可读数据scope以及create/activate/promotion capability。topic data不能改变
FamilyDefinition拥有的system prompt、tool schema或output protocol。

### 4.3 RecapGridStore

只保存 immutable cells、row views及可重建索引：

- cell commit 是唯一业务写入；
- 已 committed cell 不原地 rewrite/repair；
- prompt、definition或输入变化产生新的 cell identity；
- partial progress表现为某些 cells存在、另一些缺失，不需要整 row transaction；
- Missing assignment可以正常生成；但已committed cell/view hash mismatch或SQLite corruption使整个Grid Store typed
  `Invalid`，首版只允许关闭Store后reset/rebuild，不做targeted delete、quarantine、repair或salvage；
- Store不读取 Completion配置，也不决定该运行哪个 Maintainer。

### 4.4 RecapGridReader / Getter

只提供exact Grid纯读：

- `FindMissingAssignments(rowBuildSpec)`；
- `TryReadCell(evaluationKeyDigest)`；
- `ReadView(rowViewDigest)`；
- `ResolveFulfilledView(selectedRawRef, timelineHeadRef, activeGridBuildRevision)`。

“row 是否完整”永远相对于一个明确的 `BuildTarget`，不是 row 的绝对属性。`BuildTarget`是由MaintainerControlLog
和本次operation冻结出的`LogicalColumnId -> MaintainerDefinitionDigest`值；它只表达membership，不表达overlay/full
rebuild的provenance。

### 4.5 DerivedRecapContextComposer

在composition root中一次性解析selected raw ref、TimelineHead与authoritative `GridBuildRevision`，再要求Grid提供exact
healthy fulfilled view；随后独立从Timeline读取raw tail，组合成主线Context。它是无状态composer，不允许GridReader
扫描“latest”、逐列找最新cell或委托Timeline读取History。

### 4.6 Campaign and live selection

single-column backfill、full-grid rebuild与A/B由immutable `GridBuildRevision`表达，首版没有通用durable Campaign实体或
Pending/Running/Paused/Failed状态机。进度仍由immutable cells/views的missing query推导，不持久化per-call attempt、
reservation或settlement。live/candidate也不是不同artifact类型：view永远是immutable view，revision是否active只由
`MaintainerControlLog.CompareExchangeActiveRevision`决定。Grid内的exact `FulfilledViewRef`只是绑定active revision与
Timeline head的可重建projection/cache，不是promotion authority。

### 4.7 Runtime batch executor

只负责高效执行一组已经冻结的 cell work items：

- family/lane 分组；
- shared system prompt、tool schema、previous row view 与 history segment prefix；
- leader/follower cache策略、并行上限、调用计数与取消；
- 返回按 work item identity索引的结果。

这些信息不进入 Timeline、cell semantic identity或 row completeness。

## 5. Domain values and durable artifacts

### 5.1 PartitionPolicyRevision

```text
PartitionPolicyRevision {
  TimelineId
  PartitionPolicyRevisionId
  PartitionPolicyId
  HistoryLoadEstimatorId
  DefaultTargetHistoryLoad
  PolicyDigest
}
```

修改默认分段大小或算法只影响尚未形成的新 rows。已经形成的 row不会重切。

### 5.2 HistorySegmentDescriptor

```text
HistorySegmentDescriptor {
  TimelineId
  PartitionPolicyDigestAtCreation
  RowId
  PreviousRowId?
  RefId
  StartBoundary
  EndBoundary
  GoverningSetups
  TargetHistoryLoadAtCreation
  MeasuredHistoryLoad
  RawEventCount
  RawRangeCommitment
  DescriptorDigest
}
```

边界、创建时目标长度和实际长度都是已经发生的值事实。以后修改 BuildInterval不能改变旧 descriptor。
正文始终按 Start/End 从 raw读取，不保存在 descriptor。同一Timeline row chain允许相邻rows采用不同
`PartitionPolicyDigestAtCreation`，但不得跨`TimelineId`或脱离selected `TimelineHeadRef` chain。

### 5.3 TimelineHeadRef

```text
TimelineHeadRef {
  TimelineId
  RefId
  HeadRowId?
  RawAnchor
  Generation
}
```

append row与切换partition policy都以expected generation/head CAS；fork产生另一条显式head/path，不覆盖旧rows。

### 5.4 MaintainerDefinitionRevision

```text
MaintainerDefinitionRevision {
  LogicalColumnId
  DefinitionRevisionId
  FamilyDefinitionDigest
  Target
  CapabilityFingerprint
  DeclarativeSpec
  MaxContentBytes
  DefinitionDigest
}
```

`LogicalColumnId` 表示长期概念身份；`DefinitionDigest` 表示一次确切实现版本。Developer prompt A/B使用不同
definition revisions，不覆盖同一版本。

system prompt、tool schema与output protocol只属于immutable FamilyDefinition；column definition只能引用
`FamilyDefinitionDigest`，不存在独立override入口。`DeclarativeSpec`只能进入family定义的动态user/data区域。

### 5.5 BuildTarget

```text
BuildTarget {
  OrderedColumns [
    LogicalColumnId -> MaintainerDefinitionDigest
  ]
  TargetDigest
}
```

`TargetDigest`只由domain/schema与canonical ordered `(LogicalColumnId, MaintainerDefinitionDigest)`计算；不得加入
每次operation随机生成的ID。

### 5.6 GridBuildRevision and RowBuildSpec

`BuildTarget`只回答“有哪些exact definitions”；同一个target可以合法产生single-column overlay与full-grid
cross-integrated两条不同view chain。必须用一个immutable control-plane value区分它们：

```text
GridBuildRevision {
  TimelineId
  BootstrapThroughRowId?
  BuildTarget
  BaseRevisionDigest?
  RecomputedColumns [LogicalColumnId]
  RevisionDigest
}
```

- full-grid revision没有base，`RecomputedColumns`必须exact等于BuildTarget全部columns；
- overlay revision引用一个exact base revision，`RecomputedColumns`是BuildTarget的非空有序子集；
- overlay中所有未列入`RecomputedColumns`且仍保留的columns必须与base revision使用相同definition；base-only column
  可以在新target中被显式移除；
- `BootstrapThroughRowId`锁定该revision最初需要catch-up到的Timeline位置；追平后未来新rows仍属于同一revision，
  normal fill可以求值全部active columns；
- `RevisionDigest`提交domain/schema及以上全部canonical fields；同target的overlay/full revision digest必然不同；
- active/live选择只能由`MaintainerControlLog`中的CAS决定，不能由磁盘时间、最新view或当前catalog重新推导；
- Grid reset后，control log中的definition与revision完整值仍足以重建同一语义revision；模型非确定性可使新cell/view digest
  不同，但不会把overlay误恢复成full rebuild。

control log注册revision时必须验证base已存在、属于同一`TimelineId`且只向较早已注册revision引用，从而保证revision
graph无环；`RecomputedColumns`必须按BuildTarget order投影并进入digest。`BootstrapThroughRowId`在非空Timeline上必须是
注册时exact `TimelineHeadRef` chain的ancestor，`null`只表示该chain尚无sealed row；off-chain row不得派生spec。

revision到RowBuildSpec的派生是唯一规范，不是Manager policy：

- full-grid revision从Row 0起每个row都令Assignments exact等于全部target columns，ReusedCells为空；
- overlay revision从Row 0到bootstrap row闭区间令Assignments exact等于`RecomputedColumns`，其余target columns从base
  revision的exact same-row view复用；
- overlay在bootstrap之后的新rows令Assignments exact等于全部target columns，ReusedCells为空；
- 已存在相同EvaluationKey winner可以让assignment零remote call完成，但不能把assignment改写成任意reuse。

Manager为每个待构建row产生一个非durable纯值，而不是把revision语义交给Store猜测：

```text
RowBuildSpec {
  GridBuildRevisionDigest
  RowDescriptorDigest
  PreviousRowViewDigest?
  OutputBuildTarget
  Assignments [{ LogicalColumnId, MaintainerDefinitionDigest }]
  ReusedCells [LogicalColumnId -> exact CellDigest]
}
```

`Assignments`与`ReusedCells`必须disjoint，union exact覆盖OutputBuildTarget，且每个logical column/definition都与target
exact匹配。所有新求值cells共同使用顶层`PreviousRowViewDigest`；reused cell必须属于当前exact row/column/definition，
但允许它曾读取不同prior view。在overlay bootstrap闭区间，new/recomputed subset进入Assignments，其余target columns进入
ReusedCells；bootstrap之后全部target columns进入Assignments。full-grid revision始终把全部columns放入Assignments。
创建RowView时只能使用该spec的exact winners与reused cells。
Store只实现`FindMissingAssignments(RowBuildSpec)`与exact put-if-absent，不知道overlay/full mode。

### 5.7 RecapCellArtifact

```text
RecapCellArtifact {
  RowDescriptorDigest
  LogicalColumnId
  MaintainerDefinitionDigest
  InputRowViewDigest?
  EvaluationKeyDigest
  Outcome: Updated | KeepUnchanged
  Content
  ContentDigest
  CellDigest
}
```

```text
EvaluationKeyDigest = Hash(
  domain/schema,
  RowDescriptorDigest,
  LogicalColumnId,
  MaintainerDefinitionDigest,
  InputRowViewDigest | FirstRowSentinel
)
```

BuildTarget不进入key，除非未来Maintainer实际看到它。同key并发得到不同模型文本时，第一个成功commit者获胜；
loser读取winner artifact并返回AlreadyFilled，不产生integrity failure。

首 row没有 previous view。`KeepUnchanged`仍产生绑定新 row与新 input view的cell；它证明 Maintainer
看过本 row输入后决定正文不变，而不是“没有调用”或“缺失”。

### 5.8 RecapRowView

```text
RecapRowView {
  GridBuildRevisionDigest
  RowDescriptorDigest
  PreviousRowViewDigest?
  BuildTargetDigest
  Columns [LogicalColumnId -> {MaintainerDefinitionDigest, CellDigest}]
  RowViewDigest
}
```

它是小型immutable selection manifest，不内联所有cell正文。完整性要求 columns与BuildTarget exact匹配。
首row的`PreviousRowViewDigest`必须为空；其他row必须引用其`PreviousRowId`对应的exact RowView。
RowView允许结构共享，也允许single-column backfill形成mixed/overlay view：在overlay bootstrap区间，reused columns可
引用此前已存在、因而曾读取不同prior view的cells；new/recomputed columns引用candidate上一row view。full-grid
bootstrap区间全部columns都是assignments，因此每row都PriorViewAligned。overlay追平后的normal rows也可以
PriorViewAligned，但revision provenance仍是overlay。

三个谓词/证明必须分开：

- `MembershipComplete(view, buildTarget)`：exact definitions/columns全部有选定cell；Getter据此决定可读。
- `PriorViewAligned(view)`：首row的每个member cell都使用FirstRowSentinel；其他row的每个member cell都满足
  `InputRowViewDigest == view.PreviousRowViewDigest`。它只证明当前row的local provenance；overlay追平后的新row也可为true。
- `FullRebuildChain(view, revision)`：revision无base、`RecomputedColumns` exact等于target，且从Row 0到该view的每个
  predecessor view都提交同一`RevisionDigest`并满足`PriorViewAligned`。full/overlay身份只由revision表达，不能由单row
  对齐情况猜测。

“完整”不得暗示full rebuild，“一致”也不得被用来掩盖mixed provenance。

## 6. Core workflows

### 6.1 Normal row fill

1. Timeline冻结新 row descriptor。
2. Manager读取上一 row的complete view和本 row HistorySegment。
3. 按active GridBuildRevision为本row产生RowBuildSpec并查询缺失assignments。
4. 在首个remote call前解析全部待执行definition/runtime bindings。
5. 同 row cells并行执行并分别commit。
6. exact完整后创建RecapRowView。
7. 主线模型通过`ResolveFulfilledView(selectedRawRef, timelineHeadRef, activeGridBuildRevision)`取得exact fulfilled view，
   再加该row之后尚未封段的raw tail。

### 6.2 Add one Maintainer

1. MaintainerControlLog注册新definition和一个引用当前active revision、只重算新column的overlay GridBuildRevision；
2. 新column从Row 0开始顺序填充；每个cell读取上一row candidate view，其中旧columns可引用live既有cells，
   新column引用自己的上一candidate cell；
3. candidate追到目标row前，live view不变；
4. catch-up完成且exact RowView存在后，control plane CAS激活candidate GridBuildRevision；
5. 后续normal row fill包含新column。

首版只支持从Row 0 bootstrap；任意中途起点是后续功能。

### 6.3 Prompt tuning / A-B

同一个LogicalColumnId可以有多个definition revision及cell chain。incumbent继续服务live view，challenger独立回放；
比较完成后显式激活challenger definition对应的GridBuildRevision。不能原地rewrite incumbent cells。首版challenger
的prior BuildTarget以新definition替换同LogicalColumnId incumbent，因此默认且唯一语义是排除incumbent；若实验需要把
incumbent当额外peer，必须未来通过显式只读alias/input设计，不能塞进当前一对一BuildTarget map。

### 6.4 Cross-maintainer full rebuild

若新column应反向影响旧columns，则注册一个无base、重算全部target columns的full-grid revision，从Row 0开始逐row
wavefront rebuild全部columns。
同 row可并行；下一 row必须等待上一 row candidate view complete。

### 6.5 Reuse and skip

- Effective input digest完全相同：允许零调用精确复用同一cell artifact。
- 输入变化但预计结果不变：首版必须由exact Maintainer invocation返回`KeepUnchanged`，再提交新的cell。
- 后续可引入明确的column dependency declaration；只有依赖证明成立时才能结构性跳过。首版不做自动依赖推断。

## 7. Consistency and failure rules

1. raw events + selected Parent lineage是History正文事实authority。
2. Timeline ledger是既有row边界、长度和predecessor决策的authority；它不保存History正文，也不随cell reset丢失。
3. MaintainerControlLog是definition、GridBuildRevision与active revision CAS的唯一逻辑authority；具体composition只选
   一个物理carrier。执行进度由missing query恢复，不获得独立durable campaign lifecycle。
4. cell、row view及其查询索引是derived immutable artifacts，可删除重建。
5. cell只依赖当前row descriptor和同一TimelineHead predecessor chain上的上一row view，不允许同row依赖。
6. row view只引用exact definition revision对应的cells，不混用“碰巧同LogicalColumnId”的其他版本。
7. remote call期间不持有Store transaction/lock；成功结果才短事务commit。
8. crash前没有committed cell等同于Missing；允许重复remote call，不持久化复杂Attempt/Settlement状态机。
9. 同一`EvaluationKeyDigest`的并发结果使用atomic put-if-absent决胜；系统不宣称远端调用exactly once。
10. unknown schema、hash mismatch、wrong row/column/version、off-lineage raw proof均fail closed。
11. partial candidate永远不会被Getter误报成live complete view。
12. 只有FulfilledViewRef、进程内cache与普通查询索引可由healthy canonical artifacts重建；canonical bytes与
    row_view_member/locator不一致必须使whole Store Invalid，不得在线补表形成第二authority。
13. committed artifact损坏使whole Grid Store invalid；不得为绕过unique EvaluationKey删除单cell再补写。

## 8. Persistence backend decision

Grid的主要访问模式是：

- 按row列出expected/missing cells；
- 按column顺序读取和回填全部rows；
- 按BuildTarget判断row completeness；
- atomic插入immutable cell和小型row view；
- 同raw row并存多个definition/cell/view revisions；
- 按reachability做candidate/旧revision retention与GC；
- inspect、导出和精确诊断损坏记录。

候选仅比较两种单一真源，不采用“SQLite metadata + JSON cell files”双介质方案：

### 8.1 Directory + canonical JSON

优点：直接可读、diff/golden友好、单artifact损坏隔离、无需数据库依赖。风险：row×column×revision文件数增长；
missing/completeness/latest/reachability需要自行维护索引、inventory、锁与跨文件原子协议，容易重新实现一个脆弱数据库。

### 8.2 SQLite

优点：二维Grid、版本引用、missing-cell query、unique/foreign-key约束、短事务commit、并发读和GC查询天然匹配；
避免全目录inventory。风险：需要schema与SQLite依赖；单文件损坏影响面更大；没有专用工具时Coding Agent不如直接读JSON方便；
必须提供first-party inspect/export命令，不能要求Agent或operator直接猜内部表。

### 8.3 Decision：RecapGrid选择SQLite

理想Grid的dynamic columns、single-column frontier、missing assignment、A/B views、CAS promotion和未来reachability
查询都是关系操作。首版选择单一SQLite数据库作为RecapGrid derived artifacts/index的唯一durable store；canonical JSON
保留为逻辑artifact bytes、export、diagnostic和golden contract，不作为第二live布局。authoritative GridBuildRevision
与active selection仍属于control plane。Timeline拥有独立Store/API；即使两者最终都使用SQLite，
也必须是两个数据库，禁止`ATTACH`、跨库SQL join或跨库事务。Timeline row先独立commit，Grid cell随后引用opaque
`RowId + SegmentCommitment`。

首版不使用EF Core；采用`Microsoft.Data.Sqlite`、显式checked-in schema SQL和薄repository。最小Grid表面只需：

```text
cell_artifact(
  evaluation_key unique,
  cell_digest primary key,
  row_descriptor_digest,
  logical_column_id,
  definition_digest,
  prior_view_digest,
  canonical_artifact_bytes,
  unique(cell_digest, logical_column_id, definition_digest)
)

row_view(
  view_digest primary key,
  grid_build_revision_digest,
  row_descriptor_digest,
  previous_view_key not null,
  build_target_digest,
  canonical_artifact_bytes,
  unique(view_digest, grid_build_revision_digest,
         row_descriptor_digest, build_target_digest),
  unique(grid_build_revision_digest, row_descriptor_digest,
         build_target_digest, previous_view_key)
)

row_view_member(
  view_digest,
  column_ordinal,
  logical_column_id,
  definition_digest,
  cell_digest,
  primary key(view_digest, column_ordinal),
  unique(view_digest, logical_column_id),
  foreign key(view_digest) to row_view,
  foreign key(cell_digest, logical_column_id, definition_digest) to cell_artifact
)

fulfilled_view_ref(
  ref_id,
  timeline_id,
  timeline_head_generation,
  through_row_descriptor_digest,
  grid_build_revision_digest,
  build_target_digest,
  view_digest,
  primary key(ref_id, timeline_id, timeline_head_generation,
              through_row_descriptor_digest, grid_build_revision_digest),
  foreign key(view_digest, grid_build_revision_digest,
              through_row_descriptor_digest, build_target_digest) to row_view
)
```

artifact insert API只接受完整canonical artifact；repository在内部decode并写query locator columns。canonical bytes
是唯一semantic authority，locator/member rows只是受控denormalized indexes；row-view header与完整member set在同一
transaction写入，读取时必须重新核对digest、locator与member exact equality；每个member还必须核对cell的exact
logical column与definition，而不是只确认cell digest存在。使用`STRICT` tables、foreign keys、
unique/check constraints作为第二层防护；任何不一致都是typed StoreInvalid，不在线猜测或局部修补。
`previous_view_key`只是locator：canonical null必须映射为固定FirstRowSentinel，避免SQLite unique constraint把多个NULL
视为互不冲突。
除digest主键与unique evaluation key外，首个spike必须为
`(grid_build_revision_digest, row_descriptor_digest, build_target_digest, previous_view_key)`和exact fulfillment key
建立索引，并用大Grid fixture的`EXPLAIN QUERY PLAN`证明主路径没有无界full scan。

Grid fulfillment ref由exact active revision、Timeline head与through-row descriptor确定；`RowId`可以另作诊断列，但不
参与authority。同exact key+same view返回`AlreadyFulfilled`；同key+different view必须typed StoreInvalid，不能
last-write-wins。该ref删除后，Manager可用同一
GridBuildRevision重建RowBuildSpec并解析唯一EvaluationKey winners；whole Grid reset后也可按同一revision语义重新求值，
即使模型非确定性使新view digest变化，也不改变control-plane active revision。

Completion调用、History materialization和prompt构造全部在transaction外并行；成功结果只执行短transaction。
correctness来自SQLite transaction、unique/FK constraints和CAS，不依赖进程内single-writer queue。connection PRAGMA、
bounded `SQLITE_BUSY` commit retry、rollback journal/WAL与backup策略属于后续spike，不在Shape文档锁死；retry只能重试
本地commit，不能重新触发remote call。Spike还必须考虑两个官方边界：`Microsoft.Data.Sqlite` async方法实际同步执行
（[Async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)）；WAL候选必须运行时核验已包含
WAL-reset修复的SQLite版本或官方backport（[SQLite WAL](https://www.sqlite.org/wal.html#the_wal_reset_bug)）。

当前开发环境没有`sqlite3` executable，因此first-party可观察性属于首批contract，不是后补便利功能：

```text
recap-grid inspect
recap-grid export --canonical-json
recap-grid verify
recap-grid reset
```

Coding Agent日常通过稳定CLI、checked-in SQL、canonical export和golden审阅；不得手工编辑live数据库。除`reset`外，
所有inspect/verify/export命令必须read-only/no-create、bounded输出；数据库不存在返回typed Absent，正文导出需要显式
选项。`reset`只允许在Grid Store已关闭后执行。数据库损坏走`verify -> reset/rebuild`，不设计SQLite page级salvage或
Published repair。

首个spike不实现cell/view GC：除whole-store reset外不得删除committed artifacts。retention、generation与忘记
EvaluationKey reservation的规则必须另立设计，不能借“清理旧candidate”偷偷引入targeted repair。spike至少覆盖cell
put-if-absent、row-view header+members atomic commit、fulfillment ref commit三个child-process crash/reopen窗口；两连接
contention与bounded `SQLITE_BUSY` local-commit retry；read-only/no-create CLI；runtime SQLite version/schema/PRAGMA报告；
大Grid bounded query/materialization；以及hash/locator/FK/integrity任一失败统一进入whole-store `Invalid`。

不采用“SQLite metadata + JSON cell blobs”混合方案；除非未来cell经测量长期达到多MiB且BLOB/VACUUM成为真实瓶颈，
否则它只会引入第二durability domain、orphan inventory和跨介质backup/GC。

### 8.4 Backend invariants

无论后续SQLite实现细节如何，都必须满足：

- raw History不进入DerivedRecap数据库/文件正文；
- schema不兼容时允许reset/rebuild，不引入长期migration matrix；
- artifact hash和foreign identity仍由应用层严格验证；
- 提供canonical JSON export供review、fixture和bug report；
- completion call在transaction外；
- 一次cell commit、row-view commit和fulfilled-ref commit都有清楚、可crash-test的边界。
- Grid的artifact/ref commits与MaintainerControlLog active-revision CAS是独立crash boundaries，不做跨库transaction；
  active revision暂时unfulfilled时Getter fail closed。

## 9. Complexity budget

### 9.1 首版必须概念

- HistorySegmentDescriptor
- TimelineHeadRef
- MaintainerDefinitionRevision
- GridBuildRevision
- RecapCellArtifact
- RecapRowView

`MaintainerControlLog`是以上definition/revision的authority service，必须选择单一物理carrier；
`PartitionPolicyRevision`、`BuildTarget`、campaign和Manager不再各自扩张成一套durable artifact lifecycle。Grid是immutable
dependency DAG的二维投影视图，不是每个坐标只有一个可变值的Excel；
同一`(RowId, LogicalColumnId)`可以因definition或prior view不同拥有多个cell artifacts。

### 9.2 首版禁止概念

- complete-roster epoch publication transaction
- mutable Published repair/reseal
- per-call durable attempt/settlement journal
- same-row dependency graph
- arbitrary dependency inference
- per-column自定义Timeline
- runtime family/lane/model/connection进入durable identity
- silent partial/live view混合
- SQLite和文件双写或双真源
- 每列latest cell临时拼成的Frankenstein row
- durable scheduler batch、lease或reservation
- 自动branch merge、partial-row live promotion或复杂GC

### 9.3 Complexity acceptance

实现候选必须用概念/state/API ledger证明：

- Timeline可以在零Maintainer注册时独立运行和测试；
- 新column只增加column-local durable state，不修改旧cell bytes；
- full rebuild与single-column rebuild共用同一cell primitive，不是两套Store；
- row completeness只有一个实现，并显式接收exact BuildTarget；
- scheduler可以替换而不改变Timeline/Store wire；
- backend可以用最小fixture证明Missing/commit/crash/reopen/fulfillment projection，而不引入repair状态机。

## 10. Target acceptance scenarios

1. B=60K形成Rows 0/1；随后B改为90K，Rows 0/1 descriptor bytes不变，Row 2使用新目标且其cells合法读取
   旧policy创建的Row 1 view。
2. 零Maintainer时Timeline仍能形成/读取row，且不创建cell Store内容。
3. A/B两columns同row并行，均看到同一HistorySegment和exact上一row view。
4. 新增C只顺序填C列；A/B cell bytes不变，catch-up前live view不含C，promotion后完整包含。
   该candidate RowView明确是mixed/overlay provenance，不宣称A/B曾读取C。
5. C需要反向影响A/B时，candidate full-grid revision逐row rebuild，任何partial row都不成为live。
   对同一BuildTarget，overlay与full rebuild拥有不同GridBuildRevisionDigest和RowBuildSpec，missing/fulfillment query不会
   混淆两者，control plane可明确选择其一。
6. A prompt v1/v2同LogicalColumnId并存；A/B comparison后promotion只改变authoritative active GridBuildRevision中的
   definition selection，随后fulfilled-view projection指向对应complete view；不覆盖v1 cells。
7. exact input复用零调用；输入变化的KeepUnchanged产生新cell identity。
8. crash before cell commit留下Missing；retry允许第二次调用；crash after commit不重复生成healthy cell。
   两个worker并发完成同一EvaluationKey时，put-if-absent只接受一个cell，另一方读取AlreadyFilled。
9. rewind使row descriptor off-lineage时Getter fail closed；不会选择相同ordinal的另一branch row。
10. 删除RecapGrid数据库后，durable Timeline ledger、raw History与MaintainerControlLog仍在，可按exact active
    GridBuildRevision完整rebuild cells/views。revision已经active但fulfilled view cache尚未更新或已丢失时，Getter fail
    closed，恢复不会把overlay与full rebuild混淆，也不需要跨库repair。
11. inspect/export能在不加载Completion provider和secret时列row、column、missing、view与hash证据。
12. 大grid fixture验证按row/column查询不依赖无界目录扫描或全表内存materialization。
13. committed cell hash mismatch或SQLite integrity failure只允许whole-Grid reset/rebuild，不出现targeted repair状态。
14. Agent请求未知FamilyDefinition、越权scope或超预算创建column时，control log零变化；合法control event在Grid reset后仍在。

## 11. Open decisions before implementation

1. SQLite spike是否通过查询、crash、版本、可观察性与复杂度gate；若失败才重新打开Directory+JSON候选。
2. Timeline ledger的物理backend、backup与operator reset边界；它不能和可随意reset的Grid数据库同生命周期。
3. `MaintainerControlLog`采用raw SessionJournal action、独立catalog journal还是versioned operator config；production必须
   选且只选一个carrier，并明确Host/Agent写权限。
4. Timeline row descriptor的最小raw commitment与可重建索引形状。
5. candidate/旧cell retention与GC规则。

## 12. Implementation boundary

本文通过只表示Shape/Rule锁定及SQLite目标选择，不表示旧系统迁移方案或production implementation已经批准。
下一步应先完成SQLite backend spike、Timeline ledger spike与contract fixture，再写migration/work-package计划。

## 13. Independent review record

2026-08-10由三个相互独立的只读review视角复核，并在当前版本完成tail closure：

- semantics：Timeline policy变化与branch-safe head、overlay/full revision、Cell/RowView provenance；
- storage：SQLite exact keys/indexes、whole-store invalid/reset、crash/contention/CLI/retention spike gates；
- complexity：authority、durable lifecycle、concept/state/API budget及runtime optimization隔离。

最终gate均为P0=0/P1=0。该结论只认证本文Shape/Rule闭合，不认证SQLite spike、Timeline implementation、migration
或production readiness。
