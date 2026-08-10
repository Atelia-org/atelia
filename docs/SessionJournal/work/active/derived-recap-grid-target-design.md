# DerivedRecap Sparse Versioned Grid 目标设计

状态：Proposed target design；WP-01A complete、WP-01B ready；尚未切换 current production

## 1. Intent

重新定义 DerivedRecap，使 History 分段、Maintainer 状态、执行批处理和对外 Context 视图彼此正交。

DerivedRecap不只是“从旧History召回事实”的缓存，也是一套纵向历史分析基底。一个column可以维护人物、剧本、
假设、疑点或长期思绪；它在每个row重新阅读本段History与上一row的Recap视图，因而能随证据累积而修正认知，并把
自己的发现通过下一row输入传递给其他columns。

核心模型是一张稀疏、可版本化的二维表：

- row 是一个已经确定边界的 `HistorySegment`；
- column 是一个 `MaintainerDefinitionRevision`；
- cell 是该 Maintainer 对该段 History 做 rolling maintenance 后产生的 immutable recap；
- `HistorySegmentContent` 是 raw SessionJournal 的按需 View，不是值拷贝；
- 同一 row 的 cells 都只依赖上一 row 的选定 recap view 与本 row 的 HistorySegment，因此可以并行执行；
- 一组 cells 恰好共享 prompt prefix 或一次并行执行，只是 runtime 优化，不决定 durable identity。

本设计先定义理想目标，不保留 current complete-roster epoch、v8/v9 wire、repair/reseal 或 migration 兼容层。
实现迁移由[`Grid Rewrite 总施工计划`](derived-recap-grid-rewrite-master-plan.md)及其分包文档约束。

## 2. Goals and non-goals

### 2.1 Goals

1. `HistoryTimeline` 不知道有哪些 Maintainer 存在，只确定性地划分 raw History。
2. 新 Maintainer 可以从 Timeline 起点逐 row 回放，只填自己的 column。
3. 单个 Maintainer 的 prompt/capability 新版本可以独立 rebuild、比较和 promotion。
4. 需要 cross-maintainer 信息融合时，首版可以从Timeline起点逐 row rebuild 一个完整 Grid recipe chain。
5. 主线 Context 可以读取某个 row 对exact BuildTarget membership-complete的视图，并分别检查其GridBuildRecipe
   provenance与当前row的prior-view alignment。
6. 缺失、损坏或取消的 derived cell 可重新生成；raw selected lineage 仍是正文事实 authority，Timeline ledger
   是既有分段决策的 authority。
7. 同 row、同 family、同 history segment 的工作仍可并行并共享 completion prefix。
8. durable model 的独立语义概念、状态数和 authority 路径必须受到明确预算约束。
9. 相同Maintainer实际可见输入必须产生相同`EvaluationKeyDigest`，允许跨不同view/recipe exact reuse；不同输入不得因
   正文碰巧相同而混成同一次求值。

### 2.2 Non-goals

- 不迁移或兼容旧DerivedRecap roots；它们对新runtime保持inert，只能由独立offline exact-confirm procedure归档/删除。
  `reset/rebuild`只针对新Grid artifacts。
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

### 3.1 从历史召回到历史分析

例如Galatea参与悬疑探案时，可以先有一个`CulpritHypothesis` column维护“凶手会是谁？”。当她后来开始怀疑人物X，
control plane可以注册`XSuspicion` column，让它从Row 0逐段提取“X的行为是否可疑？”：

- column-only overlay先只回填`XSuspicion`，不伪称旧`CulpritHypothesis` cells当时已经看过这些新发现；
- candidate追平并激活后，未来normal rows的全部Maintainers都读取包含`XSuspicion`的上一row view，原有凶手假设可随
  新证据自然更新；
- 若希望新专题发现反向影响全部旧History的分析，则启动full-grid recipe，从Row 0逐row重算。X的疑点在某row被提炼后，
  最早于下一row进入其他Maintainers输入，最终可能收敛为“原来如此，那些疑点都对得上了”的新认知。

这种信息交换严格有一row延迟；不允许同row循环讨论或无证明的瞬时固定点。更深的交叉推理通过多row wavefront或显式
full rebuild获得，而不是给Store增加循环依赖求解器。

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

每个Ref另有一个canonical `ActiveTimelineLocator`选择当前TimelineId。它只通过expected locator generation CAS改变；
abandon必须在Host关闭时以exact `<RefId, TimelineId, locator generation>`确认并原子切到显式initial policy创建的新
TimelineId。旧ledger与backup变成inert bytes，runtime永不扫描它们找“latest”。restore只接受绑定exact
TimelineId/RefId/schema/generation/head且包含当前active head的verified backup，在Host关闭与expected version通过后原子替换；
更旧backup只能通过abandon建立new Timeline，不能回滚当前authority。

首版partition规则固定为：从上一row的`EndInclusive`之后开始，沿selected lineage选择累计
`MeasuredHistoryLoad >= TargetHistoryLoadAtCreation`的**第一个**replay-safe boundary。这样同一raw path和policy的row
边界不受某次后台任务启动早晚影响。`MinimumRecentHistoryLoad`不属于Timeline partition；若主线Context需要保留recent
tail，由ContextComposer/SessionJournal raw-tail policy负责。达到该policy revision记录的`MaxRawEvents`或
`MaxRenderedBytes` segment cap仍无法到达目标时返回typed limit failure；online bounded evidence不足时返回
`OfflineBootstrapRequired`，不得偷偷打开全History scan。

Timeline拥有HistoryLoad estimator的provider-neutral contracts、metric identity和goldens；provider token/cost估算仍与
HistoryLoad分离。旧Planner中的同类pure contracts在施工时迁到这个单一owner，不能复制第二套EstimatorId或算法。

assembly ownership从`SessionJournal <- HistoryTimeline <- RecapGrid.Abstractions`开始。`TimelineId`、`HistoryRowId`与
`HistorySegmentDescriptorDigest`由HistoryTimeline定义，Grid只消费这些typed values，不能重新包装一套字符串identity；是否将来再拆
轻量contracts assembly只能由实测依赖重量驱动，首版不预建第三个项目。

首版`TimelineId`绑定一个exact `RefId`，只在同一Ref内复用row prefix；不同Ref即使共享raw prefix也各自建立
descriptor chain。跨Ref dedup延后，避免把Ref selection从row identity中再拆出一层映射。

### 4.2 MaintainerControlPlane / MaintainerManager

`MaintainerControlPlane` 是definition、Grid build recipe与active recipe选择的唯一逻辑authority；它不要求实现成一套
独立append-only log。
`MaintainerControlPlane`注册Family/definition/recipe并执行active CAS；`MaintainerManager`只派生RowBuildSpec、查询missing、
按row wavefront调用opaque batch executor、commit artifacts并产出promotable proof。single-column/full/A-B的语义来自recipe；
promotion由caller拿proof显式CAS。family/lane/shared-prefix scheduling只属于runtime batch executor。Manager不决定History row
边界，不拥有raw History，也不实现第二套scheduler或active authority。

definition和recipe都是content-addressed immutable values；只有active pointer是真正可变状态：

```text
PutFamilyDefinition(family) -> FamilyDefinitionDigest
PutMaintainerDefinition(definition) -> MaintainerDefinitionDigest
PutBuildRecipe(recipe) -> RecipeDigest
CompareExchangeActiveRecipe(expectedRecipeDigest, nextRecipeDigest)
ReadSnapshot() -> definitions + recipes + ActiveRecipeDigest
```

`Put*`必须保存并验证canonical value，不能只记不可逆digest。内容哈希回答“这是什么、是否已经求值过”，active CAS回答
“当前选择哪一个”；hash不能从磁盘上所有候选中推导授权意图，也不能自动激活刚出现的A/B challenger。

Galatea 自主创建 Maintainer 的决定必须进入该control plane。raw SessionJournal action、独立control journal与
versioned operator config只是三种候选物理carrier；一次production composition必须且只能选择其中一个，不能同时成为
authority。Derived Grid Store不能成为该决定的唯一真相源，否则 reset 后定义与active选择会丢失。

一个ControlPlane实例绑定exact `(canonical colocated repository runtime binding, RefId, TimelineId)` scope；repository path只
用于找到同库旁置carrier，不进入artifact identity。不得用全repository唯一
`ActiveRecipeDigest`含糊覆盖不同branch/timeline。它必须保存FamilyDefinition、MaintainerDefinition与GridBuildRecipe三类
完整canonical values，而不是只保存不可逆digest。注册recipe时使用Timeline只读witness验证bootstrap row位于exact selected
head chain；该witness不进入RecipeDigest，ControlPlane也不读取Grid。

runtime对该tuple只打开一个确定性的canonical carrier/path；backup、quarantine或export永不参与discovery。restore只允许在
Host关闭、expected scope/version验证通过后原子替换canonical carrier；显式reinitialize也只替换同一canonical carrier并
推进generation，crash后只允许old或new完整状态，旧副本永久inert。allowlist/scope/budget/capability policy只裁决新的
Put/activate mutation，不在`ReadSnapshot`时过滤、重解释或自动deactivate已接受state；budget只是admission ceiling，不形成
durable spent-call/campaign counter。runtime缺少active definition的exact family implementation时typed
`BindingUnavailable`，不得fallback到当前catalog或旧recipe。

动态注册只允许引用allow-listed `FamilyDefinitionDigest`并提交受限DeclarativeSpec；control plane必须校验column count、
topic/prompt data长度、replay/call预算、可读数据scope以及create/activate/promotion capability。topic data不能改变
FamilyDefinition拥有的system prompt、tool schema或output protocol。

### 4.3 RecapGridStore

只保存 immutable cells、row views及可重建索引：

- Cell commit 是唯一模型输出写入；RowView与fulfilled ref只是derived selection/projection writes；
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
- `ResolveFulfilledView(selectedRawRef, completionBoundary, timelineHeadRef, activeGridBuildRecipe, nthPrevious)`。

“row 是否完整”永远相对于一个明确的 `BuildTarget`，不是 row 的绝对属性。`BuildTarget`是由MaintainerControlPlane
和本次operation冻结出的`LogicalColumnId -> MaintainerDefinitionDigest`值；它只表达membership，不表达overlay/full
rebuild的provenance。

### 4.5 DerivedRecapContextComposer

在composition root中解析selected raw ref、TimelineHead与authoritative `GridBuildRecipe`，再要求Grid提供exact healthy
fulfilled view，并把row contributions、anchor setups与completion boundary交给neutral SessionJournal candidate contract。
它是无状态composer，不允许GridReader扫描“latest”或逐列找最新cell。

组合raw tail的最终owner仍是SessionJournal core：SessionJournal独立fold completion boundary之后的raw tail并把结果冻结进
Prepared。无active nonempty recipe时可以显式授权raw-only，无论Timeline是否已有sealed rows；active recipe存在但
partial/unfulfilled/Invalid时不得fallback到raw-only或旧recipe。

`NthPrevious`不要求Store为旧row合成新的current-head fulfillment key：先exact解析current Timeline head + active recipe的
fulfilled RowView，再沿该view的`PreviousRowViewDigest`链走n步；每一步都复验same RecipeDigest和exact Timeline
predecessor descriptor。broken/missing/damaged predecessor chain立即fail closed，不扫描任意RowView找替代品。

### 4.6 Campaign and live selection

single-column backfill、full-grid rebuild与A/B由immutable `GridBuildRecipe`表达，首版没有通用durable Campaign实体或
Pending/Running/Paused/Failed状态机。进度仍由immutable cells/views的missing query推导，不持久化per-call attempt、
reservation或settlement。live/candidate也不是不同artifact类型：view永远是immutable view，recipe是否active只由
`MaintainerControlPlane.CompareExchangeActiveRecipe`决定。Grid内的exact `FulfilledViewRef`只是绑定active recipe与
Timeline head的可重建projection/cache，不是promotion authority。

### 4.7 Runtime batch executor

只负责高效执行一组已经冻结的 cell work items：

- family/lane 分组；
- shared system prompt、tool schema、previous row view 与 history segment prefix；
- leader/follower cache策略、并行上限、调用计数与取消；
- drain后返回每个ordered work item的closed outcome：`Updated | KeepUnchanged | Failed | NotStartedDueToCallerCancellation`。
  只有首dispatch前global preflight/caller cancellation可以整体返回且保证zero-started；单个throw/cancel不得丢失已成功siblings。

这些信息不进入 Timeline、cell semantic identity或 row completeness。

## 5. Domain values and durable artifacts

### 5.1 PartitionPolicyRevision

```text
PartitionPolicyRevision {
  TimelineId
  PartitionAlgorithmId
  HistoryLoadEstimatorId
  TargetHistoryLoad
  MaxRawEvents
  MaxRenderedBytes
  PolicyDigest
}
```

`MaxRawEvents`与`MaxRenderedBytes`是每个revision自己的segment caps；V1 construction以code-owned upper limits约束为
`1 <= MaxRawEvents <= 65,536`与`1 <= MaxRenderedBytes <= 32 MiB`，不能由config放宽这些upper limits。

修改默认分段大小或算法只影响尚未形成的新 rows。已经形成的 row不会重切。

### 5.2 HistorySegmentDescriptor

```text
HistorySegmentDescriptor {
  TimelineId
  PartitionPolicyDigestAtCreation
  RowId
  PreviousRowId?
  RefId
  StartExclusive
  EndInclusive
  StartSetups
  EndSetups
  HistoryLoadEstimatorId
  TargetHistoryLoadAtCreation
  MeasuredHistoryLoad
  MeasuredRenderedUtf8Bytes
  RawEventCount
  RawRangeSha256
  DescriptorDigest: HistorySegmentDescriptorDigest
}
```

边界、创建时目标长度和实际长度都是已经发生的值事实。以后修改 BuildInterval不能改变旧 descriptor。
正文始终按 Start/End 从 raw读取，不保存在 descriptor。同一Timeline row chain允许相邻rows采用不同
`PartitionPolicyDigestAtCreation`，但不得跨`TimelineId`或脱离selected `TimelineHeadRef` chain。
`RowId`与`HistorySegmentDescriptorDigest`必须由不含二者自循环的同一canonical identity body、以不同domain hash确定性导出；
Grid只把typed `HistorySegmentDescriptorDigest`当semantic commitment。

### 5.3 TimelineHeadRef

```text
TimelineHeadRef {
  TimelineId
  RefId
  HeadRowId?
  ActivePartitionPolicyDigest
  SelectedRawHeadAtCommit?
  Generation
}
```

append row与切换partition policy都以whole expected `TimelineHeadRef` CAS，但属于两个transaction：append保留active policy，
policy CAS不追加row，只替换active policy并推进generation。fork产生另一条显式head/path，不覆盖旧rows。
`SelectedRawHeadAtCommit`只是该次head transition观察到的fence，不代替每次operation由composition root重新冻结的raw head。

```text
ActiveTimelineLocator {
  RefId
  ActiveTimelineId
  LocatorGeneration
}
```

initial empty ref的canonical head是`HeadRowId=null, SelectedRawHeadAtCommit=null, Generation=0`并引用显式initial policy。
empty head执行policy CAS后仍保持两个nullable field为null，但`Generation > 0`；policy value content-addressed持久化，只有一个
active policy pointer；它不是另一套operation lifecycle。

### 5.4 MaintainerDefinitionRevision

```text
MaintainerDefinitionRevision {
  LogicalColumnId
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

```text
FamilyDefinitionDigest = Hash(
  domain/schema,
  SystemPrompt,
  OrderedToolSchema,
  OutputProtocol,
  InputRenderingProtocol
)

MaintainerDefinitionDigest = Hash(
  domain/schema,
  LogicalColumnId,
  FamilyDefinitionDigest,
  Target,
  CapabilityFingerprint,
  canonical DeclarativeSpec/UserPromptTemplate,
  MaxContentBytes
)
```

实际provider request bytes可以另记fingerprint用于cache/diagnostic，但semantic identity哈希provider-neutral typed inputs；
不能让provider JSON formatting或connection选择意外改变cell identity。若model版本本身是A/B变量，则必须显式进入
definition语义，而不是借runtime route偷偷改变。

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

### 5.6 GridBuildRecipe and RowBuildSpec

`BuildTarget`只回答“有哪些exact definitions”；同一个target可以合法产生single-column overlay与full-grid
cross-integrated两条不同view chain。必须用一个immutable control-plane value区分它们：

```text
GridBuildRecipe {
  TimelineId
  BootstrapThroughRowId?
  BuildTarget
  BaseRecipeDigest?
  RecomputedColumns [LogicalColumnId]
  RecipeDigest
}
```

- full-grid recipe没有base，`RecomputedColumns`必须exact等于BuildTarget全部columns；
- overlay recipe引用一个exact base recipe，`RecomputedColumns`是BuildTarget的非空有序子集；
- overlay中所有未列入`RecomputedColumns`且仍保留的columns必须与base recipe使用相同definition；base-only column
  可以在新target中被显式移除；
- `BootstrapThroughRowId`锁定该recipe最初需要catch-up到的Timeline位置；追平后未来新rows仍属于同一recipe，
  normal fill可以求值全部active columns；
- `RecipeDigest`提交domain/schema及以上全部canonical fields；同target的overlay/full recipe digest必然不同；
- active/live选择只能由`MaintainerControlPlane`中的CAS决定，不能由磁盘时间、最新view或当前catalog重新推导；
- Grid reset后，control plane中的definition与recipe完整值仍足以重建同一语义recipe；模型非确定性可使新cell/view digest
  不同，但不会把overlay误恢复成full rebuild。

control plane写入recipe时必须验证base已存在、属于同一`TimelineId`且只向较早已写入recipe引用，从而保证recipe
graph无环；`RecomputedColumns`必须按BuildTarget order投影并进入digest。`BootstrapThroughRowId`在非空Timeline上必须是
注册时exact `TimelineHeadRef` chain的ancestor，`null`只表示该chain尚无sealed row；off-chain row不得派生spec。

recipe到RowBuildSpec的派生是唯一规范，不是Manager policy：

- full-grid recipe从Row 0起每个row都令Assignments exact等于全部target columns，ReusedCells为空；
- overlay recipe从Row 0到bootstrap row闭区间令Assignments exact等于`RecomputedColumns`，其余target columns从base
  recipe的exact same-row view复用；
- overlay在bootstrap之后的新rows令Assignments exact等于全部target columns，ReusedCells为空；
- 已存在相同EvaluationKey winner可以让assignment零remote call完成，但不能把assignment改写成任意reuse。

Manager为每个待构建row产生一个非durable纯值，而不是把recipe语义交给Store猜测：

```text
RowBuildSpec {
  GridBuildRecipeDigest
  RowDescriptorDigest: HistorySegmentDescriptorDigest
  PreviousRowViewDigest?
  PriorInputProjectionDigest | FirstRowSentinel
  OutputBuildTarget
  Assignments [{ LogicalColumnId, MaintainerDefinitionDigest }]
  ReusedCells [LogicalColumnId -> exact CellDigest]
}
```

`Assignments`与`ReusedCells`必须disjoint，union exact覆盖OutputBuildTarget，且每个logical column/definition都与target
exact匹配。所有新求值cells共同使用从顶层`PreviousRowViewDigest`确定性投影出的`PriorInputProjectionDigest`；reused cell
必须属于当前exact row/column/definition，但允许它曾读取content-equivalent的其他prior view。在overlay bootstrap闭区间，
new/recomputed subset进入Assignments，其余target columns进入ReusedCells；bootstrap之后全部target columns进入Assignments。
full-grid recipe始终把全部columns放入Assignments。创建RowView时只能使用该spec的exact winners与reused cells。
Store只实现`FindMissingAssignments(RowBuildSpec)`与exact put-if-absent，不知道overlay/full mode。

### 5.7 PriorInputProjection

上一row输入按Maintainer实际可见的provider-neutral typed shape做content-addressing，而不是直接使用整个RowView identity：

```text
PriorInputProjection {
  OrderedCells [
    LogicalColumnId,
    ContentDigest
  ]
  ProjectionDigest
}
```

首版所有Maintainers看到上一row BuildTarget中的完整ordered cells；因此同一RowBuildSpec的assignments共享一个projection。
`LogicalColumnId`和顺序必须进入hash，不能把内容摘要当无序集合。若未来prompt真实展示额外metadata或只读取声明过的
column subset，必须升级projection schema并只提交exact可见字段，不能靠实现猜测。

`PreviousRowViewDigest`保留为row-chain provenance；`ProjectionDigest`表达Maintainer实际看到的前行内容。两个不同view若
产生相同canonical projection，就可以安全复用同一cell winner。这正是避免“上游artifact identity变化但可见正文未变”
导致级联重算的关键。

本row `HistorySegmentContent`不复制进Grid；`RowDescriptorDigest`提交exact raw boundaries与range commitment，
`FamilyDefinitionDigest`提交input rendering protocol。执行前必须从raw materialize并复验descriptor，不能只信digest
字符串。由此，动态user input的semantic value等价于
`{RowDescriptorDigest, PriorInputProjectionDigest | FirstRowSentinel}`；静态user-prompt template已进入definition。

### 5.8 RecapCellArtifact

```text
RecapCellArtifact {
  RowDescriptorDigest: HistorySegmentDescriptorDigest
  LogicalColumnId
  MaintainerDefinitionDigest
  PriorInputProjectionDigest | FirstRowSentinel
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
  MaintainerDefinitionDigest,
  PriorInputProjectionDigest | FirstRowSentinel
)
```

`MaintainerDefinitionDigest`已经提交LogicalColumnId与family/user-prompt semantics，因此key不再重复字段。BuildTarget、recipe、
previous RowView identity和runtime route不进入key，除非Maintainer实际看到了它们。同key并发得到不同模型文本时，第一个
成功commit者获胜；loser读取winner artifact并返回AlreadyFilled，不产生integrity failure。

首 row使用FirstRowSentinel。`KeepUnchanged`仍产生绑定新 row与新input projection的cell；它证明 Maintainer
看过本 row输入后决定正文不变，而不是“没有调用”或“缺失”。

### 5.9 RecapRowView

```text
RecapRowView {
  GridBuildRecipeDigest
  RowDescriptorDigest: HistorySegmentDescriptorDigest
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
bootstrap区间全部columns都是assignments，因此每row都PriorInputAligned。overlay追平后的normal rows也可以
PriorInputAligned，但recipe provenance仍是overlay。

三个谓词/证明必须分开：

- `MembershipComplete(view, buildTarget)`：exact definitions/columns全部有选定cell；Getter据此决定可读。
- `PriorInputAligned(view)`：首row的每个member cell都使用FirstRowSentinel；其他row先从exact
  `PreviousRowViewDigest`重算canonical projection，再要求每个member cell的`PriorInputProjectionDigest`与之相等。它只
  证明当前row的local input equivalence；overlay追平后的新row也可为true。
- `FullRebuildChain(view, recipe)`：recipe无base、`RecomputedColumns` exact等于target，且从Row 0到该view的每个
  predecessor view都提交同一`RecipeDigest`并满足`PriorInputAligned`。full/overlay身份只由recipe表达，不能由单row
  对齐情况猜测。

“完整”不得暗示full rebuild，“一致”也不得被用来掩盖mixed provenance。

## 6. Core workflows

### 6.1 Normal row fill

1. Timeline冻结新 row descriptor。
2. Manager读取上一 row的complete view和本 row HistorySegment。
3. 按active GridBuildRecipe为本row产生RowBuildSpec并查询缺失assignments。
4. 在首个remote call前解析全部待执行definition/runtime bindings。
5. 同 row cells并行执行并分别commit。
6. exact完整后创建RecapRowView。
7. 主线模型通过`ResolveFulfilledView(selectedRawRef, completionBoundary, timelineHeadRef, activeGridBuildRecipe, nthPrevious)`取得
   exact fulfilled view，
   再加该row之后尚未封段的raw tail。

同一个`Send`可在pre-observation、ObservationAccepted和每个ToolResultObserved后的安全未Prepared边界多次调用这一
lifecycle。Timeline只在replay-safe boundary封row；Manager operation必须幂等，不能假设“一次Send只调用一次”。

### 6.2 Add one Maintainer

1. MaintainerControlPlane写入新definition和一个引用当前active recipe、只重算新column的overlay GridBuildRecipe；
2. 新column从Row 0开始顺序填充；每个cell读取上一row candidate view，其中旧columns可引用live既有cells，
   新column引用自己的上一candidate cell；
3. candidate追到目标row前，live view不变；
4. catch-up完成且exact RowView存在后，control plane CAS激活candidate GridBuildRecipe；
5. 后续normal row fill包含新column。

首版只支持从Row 0 bootstrap；任意中途起点是后续功能。

### 6.3 Prompt tuning / A-B

同一个LogicalColumnId可以有多个definition revision及cell chain。incumbent继续服务live view，challenger独立回放；
比较完成后显式激活challenger definition对应的GridBuildRecipe。不能原地rewrite incumbent cells。首版challenger
的prior BuildTarget以新definition替换同LogicalColumnId incumbent，因此默认且唯一语义是排除incumbent；若实验需要把
incumbent当额外peer，必须未来通过显式只读alias/input设计，不能塞进当前一对一BuildTarget map。

### 6.4 Cross-maintainer full rebuild

若新column应反向影响旧columns，则写入一个无base、重算全部target columns的full-grid recipe，从Row 0开始逐row
wavefront rebuild全部columns。
同 row可并行；下一 row必须等待上一 row candidate view complete。

### 6.5 Reuse and skip

- `MaintainerDefinitionDigest + RowDescriptorDigest + PriorInputProjectionDigest`完全相同：允许零调用精确复用同一cell
  artifact，即使PreviousRowView或GridBuildRecipe identity不同；
- 上游cell artifact变化但ordered visible contents不变：projection不变，避免级联重算；column label/order/content任一
  可见字段改变：projection变化；
- 输入变化但预计结果不变：首版必须由exact Maintainer invocation返回`KeepUnchanged`，再提交新的cell。
- 后续可引入明确的column dependency declaration；只有依赖证明成立时才能结构性跳过。首版不做自动依赖推断。

## 7. Consistency and failure rules

1. raw events + selected Parent lineage是History正文事实authority。
2. Timeline ledger是既有row边界、长度和predecessor决策的authority；它不保存History正文，也不随cell reset丢失。
3. MaintainerControlPlane是definition、GridBuildRecipe与active recipe CAS的唯一逻辑authority；具体composition只选
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
14. `Prepared`/`Started` request已经冻结exact context与completion recipe；恢复这两相时不得读取Timeline、Grid、
    ControlPlane或DerivedRecap active/current route config。Prepared仍按frozen completion identity从Host registry exact bind；
    `Started`默认Refuse在client creation前零derived write，显式restart只从Prepared frozen bytes产生新attempt。
15. `DerivedContext.NthPrevious=n`沿exact selected Timeline predecessor chain选择第n个sealed row，再要求同一active
    recipe的exact fulfillment；missing/damaged/off-lineage不得跳过邻居或按全局ordinal猜测。

## 8. Persistence backend decision

Grid的主要访问模式是：

- 按row列出expected/missing cells；
- 按column顺序读取和回填全部rows；
- 按BuildTarget判断row completeness；
- atomic插入immutable cell和小型row view；
- 同raw row并存多个definition/cell/view variants；
- 按reachability做candidate/旧recipe retention与GC；
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
保留为逻辑artifact bytes、export、diagnostic和golden contract，不作为第二live布局。authoritative GridBuildRecipe
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
  prior_input_projection_digest,
  canonical_artifact_bytes,
  unique(cell_digest, logical_column_id, definition_digest)
)

row_view(
  view_digest primary key,
  grid_build_recipe_digest,
  row_descriptor_digest,
  previous_view_key not null,
  build_target_digest,
  canonical_artifact_bytes,
  unique(view_digest, grid_build_recipe_digest,
         row_descriptor_digest, build_target_digest),
  unique(grid_build_recipe_digest, row_descriptor_digest,
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
  grid_build_recipe_digest,
  build_target_digest,
  view_digest,
  primary key(ref_id, timeline_id, timeline_head_generation,
              through_row_descriptor_digest, grid_build_recipe_digest),
  foreign key(view_digest, grid_build_recipe_digest,
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
`(grid_build_recipe_digest, row_descriptor_digest, build_target_digest, previous_view_key)`和exact fulfillment key
建立索引，并用大Grid fixture的`EXPLAIN QUERY PLAN`证明主路径没有无界full scan。

Grid fulfillment ref由exact active recipe、Timeline head与through-row descriptor确定；`RowId`可以另作诊断列，但不
参与authority。同exact key+same view返回`AlreadyFulfilled`；同key+different view必须typed StoreInvalid，不能
last-write-wins。该ref删除后，Manager可用同一
GridBuildRecipe重建RowBuildSpec并解析唯一EvaluationKey winners；whole Grid reset后也可按同一recipe语义重新求值，
即使模型非确定性使新view digest变化，也不改变control-plane active recipe。

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
- Grid的artifact/ref commits与MaintainerControlPlane active-recipe CAS是独立crash boundaries，不做跨库transaction；
  active recipe暂时unfulfilled时Getter fail closed。

## 9. Complexity budget

### 9.1 首版必须概念

- HistorySegmentDescriptor
- TimelineHeadRef
- ActiveTimelineLocator
- FamilyDefinition
- MaintainerDefinitionRevision
- GridBuildRecipe
- RecapCellArtifact
- RecapRowView

`MaintainerControlPlane`是以上definition/recipe的authority service，必须选择单一物理carrier；
`PartitionPolicyRevision`只包含content-addressed policy values与TimelineHead上的一个active pointer，不扩张成operation
lifecycle；`BuildTarget`、campaign和Manager也不各自扩张durable lifecycle。Grid是immutable
dependency DAG的二维投影视图，不是每个坐标只有一个可变值的Excel；
同一`(RowId, LogicalColumnId)`可以因definition或prior input projection不同拥有多个cell artifacts。

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
5. C需要反向影响A/B时，candidate full-grid recipe逐row rebuild，任何partial row都不成为live。
   对同一BuildTarget，overlay与full rebuild拥有不同GridBuildRecipeDigest和RowBuildSpec，missing/fulfillment query不会
   混淆两者，control plane可明确选择其一。
6. A prompt v1/v2同LogicalColumnId并存；A/B comparison后promotion只改变authoritative active GridBuildRecipe中的
   definition selection，随后fulfilled-view projection指向对应complete view；不覆盖v1 cells。
7. 两个不同PreviousRowViews若ordered visible column/content projection相同，则相同definition/row产生同一
   EvaluationKey并零调用复用；column label、顺序、内容或definition任一可见语义变化都会改变key。输入变化但输出正文
   不变时，KeepUnchanged仍产生新cell identity。
8. crash before cell commit留下Missing；retry允许第二次调用；crash after commit不重复生成healthy cell。
   两个worker并发完成同一EvaluationKey时，put-if-absent只接受一个cell，另一方读取AlreadyFilled。
9. rewind使row descriptor off-lineage时Getter fail closed；不会选择相同ordinal的另一branch row。
10. 删除RecapGrid数据库后，durable Timeline ledger、raw History与MaintainerControlPlane仍在，可按exact active
    GridBuildRecipe完整rebuild cells/views。recipe已经active但fulfilled view cache尚未更新或已丢失时，Getter fail
    closed，恢复不会把overlay与full rebuild混淆，也不需要跨库repair。
11. inspect/export能在不加载Completion provider和secret时列row、column、missing、view与hash证据。
12. 大grid fixture验证按row/column查询不依赖无界目录扫描或全表内存materialization。
13. committed cell hash mismatch或SQLite integrity failure只允许whole-Grid reset/rebuild，不出现targeted repair状态。
14. Agent请求未知FamilyDefinition、越权scope或超预算创建column时，control plane零变化；合法control event在Grid reset后仍在。
15. 悬疑分析fixture中，`XSuspicion` overlay回填不改旧`CulpritHypothesis` cells；激活后的新rows允许后者读取前一row的
    X疑点而更新。full-grid recipe则从Row 0重算全部columns，证明新专题发现可沿wavefront逐row传播，同时不存在同row循环。
16. `NthPrevious=1`严格选择exact selected Timeline chain上的前一sealed row及同一active recipe fulfillment；目标slot
    missing/damaged时fail closed，不跳到更早healthy row或同ordinal sibling branch。
17. Prepared后删除Grid、改变active recipe并使Timeline/Control不可用，request仍按frozen bytes byte-identical恢复；Started
    Refuse零client/零derived write，explicit restart只产生新的provider attempt。

## 11. Open decisions before implementation

1. SQLite spike是否通过查询、crash、版本、可观察性与复杂度gate；若失败才重新打开Directory+JSON候选。
2. Timeline ledger的物理backend、backup与operator abandon边界；它不能和可随意reset的Grid数据库同生命周期。
3. `MaintainerControlPlane`采用raw SessionJournal action、独立control journal还是versioned operator config；production必须
   选且只选一个carrier，并明确Host/Agent写权限。
4. WP-01A锁定canonical raw commitment/preimage；WP-01C只裁决durable locator与可重建索引形状。
5. candidate/旧cell retention与GC规则。

## 12. Implementation boundary

本文通过只表示Shape/Rule锁定及SQLite目标选择，不表示旧系统迁移方案或production implementation已经批准。
施工计划已经拆为WP-00至WP-08；WP-00 baseline/walking skeleton与WP-01A Timeline contracts/partition已经完成，
WP-01B raw integration处于Ready。
每个backend、carrier或cutover选择仍
必须在所属工作包取得实证Go，不因本文或计划存在而预先视为implemented/production-ready。

## 13. Independent review record

2026-08-10由三个相互独立的只读review视角复核初始Sparse Grid模型，并完成当时版本的tail closure：

- semantics：Timeline policy变化与branch-safe head、overlay/full recipe、Cell/RowView provenance；
- storage：SQLite exact keys/indexes、whole-store invalid/reset、crash/contention/CLI/retention spike gates；
- complexity：authority、durable lifecycle、concept/state/API budget及runtime optimization隔离。

当时最终gate均为P0=0/P1=0。其后本文按用户裁决把ControlLog/Revision收缩为content-addressed
ControlPlane/BuildRecipe，并以`PriorInputProjectionDigest`替代整份RowView identity作为Cell输入key。

2026-08-10又由三条独立只读review线对该refinement及施工计划完成tail closure，最终P0=0/P1=0，覆盖：

- Timeline partition/head/active-locator、captured raw authority、HistoryLoad owner与01A/B/C边界；
- Control canonical carrier/scope/allowlist authority、SQLite opaque fulfillment/reset/concurrency/CLI与project dependency；
- row-batch scheduler ownership、strict NthPrevious、Prepared/Started frozen recovery、Agent control入口与atomic cutover ledger。

该review只批准Shape/Rule与可施工计划，不认证SQLite/Timeline spike、implementation、migration或production readiness。
