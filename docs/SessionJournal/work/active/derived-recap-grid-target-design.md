# DerivedRecap Sparse Versioned Grid 目标设计

状态：WP-00至WP-08 与 Store v3 切片已完成；当前 [Timeline 单一行身份](../../../Galatea/timeline-row-identity-simplification-plan.md)实施中，最终验证待补。旧测试数不认证本次新格式。

当前单一 HistoryRowId、CellSlot、普通结果 ID、SQL 单份数据与来源诊断以[Timeline 计划](../../../Galatea/timeline-row-identity-simplification-plan.md)及 [Store v4](../../current/contracts/recap-grid-store-sqlite-v4.md)和当前源码为准。本文已同步对应 Shape/Rule；末尾旧工作包与审查记录只认证当时实现。

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
9. 同一 `CellSlot(RecipeDigest, HistoryRowId, LogicalColumnId)` 沿用首个已提交结果。不同 recipe 不再隐式共享
   同输入/同正文缓存，包括首行；Overlay 只通过显式 Reuse 引用 base cell。

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
边界不受某次后台任务启动早晚影响。`MinimumRecentHistoryLoad`不属于Timeline partition，而由per-Ref repo-owned
RecapGrid Cadence V1 policy持有；Cadence同时提交exact expected partition fields，并由non-forgeable seal operation
在first-safe B candidate之外证明recent tail `>=R`。达到该policy revision记录的`MaxRawEvents`或
`MaxRenderedBytes` segment cap仍无法到达目标时返回typed limit failure；online bounded evidence不足时返回
`OfflineBootstrapRequired`，不得偷偷打开全History scan。

Timeline拥有HistoryLoad estimator的provider-neutral contracts、metric identity和goldens；provider token/cost估算仍与
HistoryLoad分离。旧Planner中的同类pure contracts在施工时迁到这个单一owner，不能复制第二套EstimatorId或算法。

assembly ownership从`SessionJournal <- HistoryTimeline <- RecapGrid.Abstractions`开始。`TimelineId` 与 `HistoryRowId`
由 HistoryTimeline 定义，Grid 只消费这些 typed values，不再有第二个 descriptor digest；是否将来再拆
轻量contracts assembly只能由实测依赖重量驱动，首版不预建第三个项目。

首版`TimelineId`绑定一个exact `RefId`，只在同一Ref内复用row prefix；不同Ref即使共享raw prefix也各自建立
descriptor chain。跨Ref dedup延后，避免把Ref selection从row identity中再拆出一层映射。

### 4.2 MaintainerControlPlane / MaintainerManager

`MaintainerControlPlane` 是definition、Grid build recipe与active recipe选择的唯一逻辑authority；它不要求实现成一套
独立append-only log。
`MaintainerControlPlane`注册Family/definition/recipe并执行active CAS；`MaintainerManager`只派生RowBuildSpec、查询missing、
按row wavefront调用opaque batch executor并commit artifacts。只有through exact等于frozen selected head时才产出promotable proof；
ancestor-through只产出不含ControlHead expected tuple的fulfillment receipt。single-column/full/A-B的语义来自recipe；promotion由caller拿
head proof显式CAS。family/lane/shared-prefix scheduling只属于runtime batch executor。Manager不决定History row
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
- Missing assignment可以正常生成；但已 committed cell/view 关系无效或 SQLite corruption使整个Grid Store typed
  `Invalid`，首版只允许关闭Store后reset/rebuild，不做targeted delete、quarantine、repair或salvage；
- Store不读取 Completion配置，也不决定该运行哪个 Maintainer。

### 4.4 RecapGridReader / Getter

只提供exact Grid纯读：

- `FindMissingAssignments(rowBuildSpec)`；
- `TryReadCell(cellSlot)`；
- `ReadView(rowResultId)`；
- Getter public入口是owner-bound factory，而非caller-supplied authority tuple：
  `RecapGridContextFactory.Open(selectedSessionJournalReadView)`取得owned handle，再调用
  `handle.Resolve(completionBoundary, nthPrevious)`；factory内部打开canonical Timeline/Control Readers，Resolve内部读取current whole
  heads/active recipe并在需要时lazy-open唯一Store ReaderHandle。调用方不能注入heads、recipe、Reader或backend。

“row 是否完整”永远相对于一个明确的 `BuildTarget`，不是 row 的绝对属性。`BuildTarget`是由MaintainerControlPlane
和本次operation冻结出的`LogicalColumnId -> MaintainerDefinitionDigest`值；它只表达membership，不表达overlay/full
rebuild的provenance。

### 4.5 DerivedRecapContextComposer

在composition root中解析selected raw ref、TimelineHead与authoritative `GridBuildRecipe`，再要求Grid提供exact healthy
fulfilled view，并把row contributions、anchor setups与completion boundary交给neutral SessionJournal candidate contract。
它是无状态composer，不允许GridReader扫描“latest”或逐列找最新cell。

组合raw tail的唯一owner仍是SessionJournal core：Getter只返回contributions与sealed-row anchor；SessionJournal独立fold anchor之后、
completion boundary之前的raw tail并把结果冻结进Prepared。raw-only只有两条规则：(1) 无active recipe时，无论Timeline是否已有
sealed rows都授权raw-only且不打开Store；(2) Timeline仍empty时，即使recipe已经active也授权raw-only且不打开Store，以允许原始历史
继续增长并由lifecycle封出首row，避免“首row fulfillment必须先存在才能继续”的死锁。一旦Timeline nonempty，active recipe的
partial/unfulfilled/Invalid不得fallback到raw-only、旧recipe或旧head cache。

`NthPrevious`不要求Store为旧row合成新的current-head fulfillment key：先exact解析current Timeline head + active recipe的
fulfilled RowView，再沿该view的`PreviousRowResultId`链走n步；每一步都复验same RecipeDigest和exact Timeline
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
}
```

边界、创建时目标长度和实际长度都是已经发生的值事实。以后修改 BuildInterval不能改变旧 descriptor。
正文始终按 Start/End 从 raw读取，不保存在 descriptor。同一Timeline row chain允许相邻rows采用不同
`PartitionPolicyDigestAtCreation`，但不得跨`TimelineId`或脱离selected `TimelineHeadRef` chain。
`RowId` 继续由原 `RowIdDomain` 与原 identity body v1 确定性导出，验证 ID 与 body 一致。descriptor 外层 wire v2
不再包含第二个 digest；外层格式升级不改变 ID preimage。Grid 的构建、selection、fulfillment 统一使用原 HistoryRowId。

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

Timeline明确分成四种write transaction：immutable policy put不改head；partition-policy CAS只切active policy；row append原子插row并
推进head；selected-path reconcile只回指共同ancestor/empty。后三者都比较whole expected `TimelineHeadRef`；append保留active
policy，policy CAS与reconcile都不追加row，reconcile也不切policy。即使next digest与active policy相同，成功policy CAS仍推进
generation。fork产生另一条显式head/path，不覆盖旧rows。
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
  Target { Carrier, BlockKey, SemanticHeading }
  CapabilityFingerprint
  DeclarativeSpec
  MaxContentBytes
  DefinitionDigest
}
```

`LogicalColumnId` 表示长期概念身份；`DefinitionDigest` 表示一次确切实现版本。Developer prompt A/B使用不同
definition revisions，不覆盖同一版本。

新Definition写schema v2，`SemanticHeading`作为provider-facing单行语义信封进入canonical body与digest；
`(Carrier, BlockKey)`仍是唯一routing identity与排序依据。读取schema v1时保留原canonical bytes/digest，并从其
carrier与block key确定性生成legacy英文heading，不做隐式升级重写。

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

Completion runtime的唯一public binding key是exact
`(FamilyDefinitionDigest, Capability.RuntimeProtocolId, Capability.SemanticModelId?)`；nullable semantic model是key的真实成员，
不是default/fallback。Host只提供deferred resolver与provider-neutral invoker；route object reference拥有跨batch lane affinity与cap，
Manager仍独占whole-batch budget、row barrier和artifact settlement。Runtime不得读取Control/Store/Timeline coordinator，也不得把
provider、model connection、cache hint、usage、call-log或lane identity写入Family/Definition/Cell。Provider input只允许
V1 schema marker、有序previous `logicalColumnId/content`、visible History，以及本work的Topic/literal UserPromptTemplate/Target；
reasoning、inline think与未commit Grid metadata不进入prompt。
冻结的Target投影仍只有carrier与block key；`SemanticHeading`只用于main-agent request的pre-Prepared渲染，
其exact结果随后由Prepared v5持久化，旧Prepared snapshot不会被新renderer重渲染。
Runtime scheduling使用不持lane的leader pre-admission与真实provider-call start/terminal barrier；followers只能在本batch所有leaders已started
或形成terminal decision后释放。fatal一旦被观察只drain已started work，不再dispatch任何未started follower；admission wait与dispatch
lane wait是分离的bounded operational evidence，二者都不进入semantic identity。

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
- Grid reset后，control plane中的definition与recipe完整值仍足以重建同一语义recipe；新 Store 为 cell/view 分配新的普通 ID，正文也可能
  不同，但不会把overlay误恢复成full rebuild。

control plane写入recipe时必须验证base已存在、属于同一`TimelineId`且只向较早已写入recipe引用，从而保证recipe
graph无环；`RecomputedColumns`必须按BuildTarget order投影并进入digest。`BootstrapThroughRowId`在非空Timeline上必须是
注册时exact `TimelineHeadRef` chain的ancestor，`null`只表示该chain尚无sealed row；off-chain row不得派生spec。

recipe到RowBuildSpec的派生是唯一规范，不是Manager policy：

- full-grid recipe从Row 0起每个row都令Assignments exact等于全部target columns，ReusedCells为空；
- overlay recipe从Row 0到bootstrap row闭区间令Assignments exact等于`RecomputedColumns`，其余target columns从base
  recipe的exact same-row view复用；
- overlay在bootstrap之后的新rows令Assignments exact等于全部target columns，ReusedCells为空；
- 已存在相同 CellSlot winner可以让assignment零remote call完成，但不能把assignment改写成任意reuse。

Manager 为每个待构建 row 派生非持久 `RowBuildSpec`，其中的 recipe、history row、target、前驱与
assignments 共同约束执行。`RowBuildSpec.Create` 不再接收单独 `PriorInput`。

Evaluate assignment 使用当前 Slot；Reuse assignment 引用已存 base cell。两者 disjoint、union exact 覆盖
OutputBuildTarget，每列与 definition 均须匹配。Runtime 独立检查 frozen spec、实际前驱 RowResult 及有序 cells；
Store 查询仅返回该 spec 缺失的 Slots。executor outcome 必须与冻结的 missing-work 集合精确对应，不能按另一批次的
ordinal 接受结果。bootstrap 后仍对全部 target columns 求值。

### 5.7 CellSlot 与前驱来源

```text
CellSlot = (RecipeDigest, HistoryRowId, LogicalColumnId)
```

这是普通结构坐标，不编码为 hash。不可变 recipe 固定规则/列，Timeline 行固定历史与上一行，同 recipe/上一行
的 RowResult 唯一且必须先发布，因此 Slot 已确定求值输入。规则或前驱不符应拒绝，不能用另一 key 绕过校验。
没有独立 EvaluationKey、PriorInputReference 或 content/projection digest；前驱有无由行关系表达。

Cell 不另存 producer prior：沿 `cell.Slot.HistoryRowId` 找 Timeline 前驱，再按 cell 的源 recipe 与该历史行
查已提交 RowResult。本行只提交部分 cell 时也能推导，因为前驱早已发布。没有历史前驱才是 FirstRow；
有历史前驱却缺来源 RowResult 是缺失；零列前驱仍是有 ID 的真实 RowResult。

模型仍读取上一行完整有序正文及本行 HistorySegment；Slot 与普通 ID 不进入 provider prompt。
HistorySegmentContent 按 raw materialize 并复验 descriptor，Family/Definition 仍确定渲染协议与静态规则。

### 5.8 RecapCellArtifact

`RecapCellArtifact` 保留实体名；`.Id` 为 Store 分配的普通随机 128-bit `CellId`，`.Slot` 记录源构建位置，
并保存 definition、Outcome 与 Content 等业务字段；历史行来自 Slot。没有 ContentDigest 或 CellDigest。

同 Slot 并发得到不同模型正文，第一个 commit 者获胜；`PutCell(spec, draft)` 的 `Inserted` 与
`AlreadyFilled` 都返回实际持久 `Winner`。提交结果不明时按 Slot 重新观察，不能按未出现的候选 ID 判未提交。

`KeepUnchanged` 仍是一次实际求值后的新 Slot 结果，保存前行对应列正文；不是缺失或跳过调用。
Overlay Reuse 则直接引用 base cell 的 ID 与源 Slot，不给它重写 candidate 来源。

### 5.9 RecapRowView

`RecapRowView` 保留实体名；`.Id` 为 Store 分配的普通随机 `RowResultId`，`.PreviousRowResultId` 引用已存前驱，
有序 members 使用 `CellId`。row assignment、target 与成员必须完整匹配；首行前驱为 null，其他行精确匹配
Timeline 指定的上一行及同 recipe/target/scope。

`PutRowView(spec, stored cells)` 原子发布 header 与 members，成功返回实际持久 `Winner`。
同 assignment、成员与前驱返回已有 row；真实业务差异才 Conflict，候选随机 ID 不参与业务相等。

三个谓词/证明分开：

- `MembershipComplete`：exact definitions/columns 都有选定 cell；Getter 据此决定可读。
- `PriorSourceAligned`：由每个 cell 的源 Slot 推导前驱 RowResult，与当前 row 前驱比较。它不承诺正文等价；
  合法 Overlay 可以 NotSatisfied，不能因此拒绝正文或重建。缺来源或预算耗尽为 Incomplete。
- `FullRebuildChain`：recipe 无 base、完整重算，且前驱链属于同 recipe 并满足来源对齐。不能把 Overlay 冒充 full。

诊断统计独立 `ExaminedRows`、`ExaminedCells`、`ExaminedMembers` 与实际 `ExaminedContentUtf8Bytes`。
这些单位不等于旧整对象 canonical bytes；不为计量重建已删除的序列化对象。

## 6. Core workflows

### 6.1 Normal row fill

1. Timeline冻结新 row descriptor。
2. Manager读取上一 row的complete view和本 row HistorySegment。
3. 按active GridBuildRecipe为本row产生RowBuildSpec并查询缺失assignments。
4. 在首个remote call前解析全部待执行definition/runtime bindings。
5. 同 row cells并行执行并分别commit。
6. exact完整后创建RecapRowView。
7. composition root由selected `SessionJournalReadView`打开owner-bound Getter，调用`Resolve(completionBoundary, nthPrevious)`取得
   exact fulfilled candidate与sealed-row anchor；SessionJournal core再独立fold该anchor之后尚未封段的raw tail。

同一个`Send`可在pre-observation、ObservationAccepted和每个ToolResultObserved后的安全未Prepared边界多次调用这一
lifecycle，但只有`PreObservation`允许reconcile/seal；ObservationAccepted与ToolResultObserved只做readiness并把当前事件保留在
SessionJournal raw tail。Timeline只在replay-safe boundary封row；Manager operation必须幂等，不能假设“一次Send只调用一次”。

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

- 同 Slot 已提交结果零调用复用；不同 recipe 的同输入/同正文不再隐式共享，包括首行。
- Overlay 的显式 Reuse 读取 exact same-row base cell，保留其 ID 与源 Slot。
- 新 Slot 即使预计正文不变，也须由实际 Maintainer 调用返回 `KeepUnchanged`。
- 首版不做自动依赖推断或以内容等价跳过重算。

## 7. Consistency and failure rules

1. raw events + selected Parent lineage是History正文事实authority。
2. Timeline ledger是既有row边界、长度和predecessor决策的authority；它不保存History正文，也不随cell reset丢失。
3. MaintainerControlPlane是definition、GridBuildRecipe与active recipe CAS的唯一逻辑authority；具体composition只选
   一个物理carrier。执行进度由missing query恢复，不获得独立durable campaign lifecycle。
4. cell、row view 及查询状态是可整体 Reset/重建的派生结果；正常运行不局部删除已提交 winner。
5. cell只依赖当前row descriptor和同一TimelineHead predecessor chain上的上一row view，不允许同row依赖。
6. row view只引用exact definition revision对应的cells，不混用“碰巧同LogicalColumnId”的其他版本。
7. remote call期间不持有Store transaction/lock；成功结果才短事务commit。
8. crash前没有committed cell等同于Missing；允许重复remote call，不持久化复杂Attempt/Settlement状态机。
9. 同一 `CellSlot`的并发结果使用atomic put-if-absent决胜；系统不宣称远端调用exactly once。
10. unknown schema、关系无效、wrong row/column/version、off-lineage raw proof均fail closed。
11. partial candidate永远不会被Getter误报成live complete view。
12. SQL columns 与 row members 是唯一持久表示；按实际类型、关系、唯一性、FK 和预算验证。损坏时 typed Invalid，
    不保留整对象 canonical 副本，也不在线猜测/补表。
13. committed artifact损坏使whole Grid Store invalid；不得为绕过Slot UNIQUE删除单cell再补写。
14. `Prepared`/`Started` request已经冻结exact context与completion recipe；恢复这两相时不得读取Timeline、Grid、
    ControlPlane或DerivedRecap active/current route config。Prepared仍按frozen completion identity从Host registry exact bind；
    `Started`默认Refuse在client creation前零derived write，显式restart只从Prepared frozen bytes产生新attempt。
15. `DerivedContext.NthPrevious=n`沿exact selected Timeline predecessor chain选择第n个sealed row，再要求同一active
    recipe的exact fulfillment；missing/damaged/off-lineage不得跳过邻居或按全局ordinal猜测。

## 8. Persistence backend decision

RecapGrid 使用单一 SQLite Store；DDL owner 为
[`SchemaV4.sql`](../../../../prototypes/SessionJournal.RecapGrid/Store/SchemaV4.sql)。当前规则见
[Store v4 说明](../../current/contracts/recap-grid-store-sqlite-v4.md)。旧 v2 逻辑 schema 的批准与测试指纹是历史证据，
不自动认证 v4。

### 8.1 单一持久表示

- cell：普通 CellId、源 Slot、正文/结果字段；Slot 三字段均非 null，并有 UNIQUE。
- row：普通 RowResultId、唯一 assignment、前驱 ID 与必要状态。
- members：row、ordinal、column/definition 与已存 CellId；有序唯一成员与 FK。
- fulfillment：exact Timeline head/recipe/ThroughRowId → 已存 RowResult，保留 scope 与前沿验证。

SQL columns 与成员关系直接物化对象；没有 `cell.canonical`、`row.canonical`、`fulfilled.key_canonical`。
不再需要 nullable prior 唯一键、FirstRow sentinel 索引或额外 producer prior 字段。导出为临时诊断投影，
不作为第二份持久权威或生产导入格式。

### 8.2 事务与恢复

Completion、History materialization 与 prompt 构造都在事务外；成功结果短事务提交。
同 Slot first-winner、row assignment UNIQUE、成员/前驱 FK、metadata counters、bounded retry 与原子发布保留。
不确定 cell commit 按 Slot、row commit 按 assignment 重新观察实际记录；本地 retry 不重新调用 provider。

Timeline 与 Control 是独立 authorities；不做跨库事务或 `ATTACH`。Prepared 已冻结正文不依赖当前 Store。
Reset 更换 StoreInstanceId；新 CellId/RowResultId 不从内容生成，陌生 ID 返回 Missing 即可，不增加跨 Store ID 服务。

### 8.3 Operator 与 schema 切换

物理槽位仍为 `derived/recap-grid/v1/grid.sqlite`，SQLite schema 为 v4。普通打开旧 schema 返回
UnsupportedSchema，不自动清库、迁移或调用模型。显式离线 Reset 复用已有文件 witness、lease 与原子替换机制，
不解码旧 cell，不保留旧 schema reader。

`recap-grid inspect/export/verify/reset` 与现有 Timeline/Cadence/Control/build/progress/materialize 命令继续由
CLI owner 提供。inspect/verify/export read-only/no-create；导出正文须显式选择，分页与数量/正文上限保留。
不要手工编辑 live 数据库，或为绕过 Slot first-winner 局部删除 cell。

旧 Recap 不转换；全部重构代码完成后才统一重建。最终清库前须在旧 Store 仍可读时正常收敛所有仍支持执行分支的
pending promotion（工具先查 Store proof 才查 receipt）与 Recipes 非空 registration（删除字段改变 command）。
随后停服备份，在完整隔离副本逐库执行明确的 Timeline schema 2→3 升级，保留所有行/head/path，最后 Reset、新库构建。
没有完成这个前提就暂缓对应数据集；不清 Timeline、Control、Journal 或冻结请求。

Store export cursor wire v2 明确拒绝 v1；旧 fulfilled cursor 与 RowId 同为 64 hex 也不能混用。
Timeline 独立维护 API 为 `HistoryTimelineMaintenance.UpgradeSchemaV2(repositoryPath, refId, timelineId)`；
普通 reader 仅支持 schema 3，旧 row 解码只在该离线入口。目录仍为 `derived/history-timeline/v2`。
Control writer v4，旧 v2/v3 在 codec 验证后投影到单一 bootstrap RowId，纯读/receipt replay/export/backup 保留原 Head/bytes；
下一真实 mutation 才升级。Recipe 正文、Cadence、catalog/runtime 不变，空 registration bundle 继续拒绝。

### 8.4 Backend invariants

- raw History 不进入 RecapGrid 正文存储；规则图保留在 Control。
- SQL 类型、scope、成员、前驱、UNIQUE/FK 与资源预算验证实际业务关系。
- 整体 Reset 可丢弃旧 Recap，不要求新正文相同；正常重开保留首个已存结果。
- cell、row、fulfilled 各自有明确事务与 crash/reopen 边界。
- Store 发布与 Control active CAS 是独立边界，active recipe 尚未 fulfilled 时不伪装可读。

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
7. 同 Slot 重开零新增调用；不同 recipe 即使输入或正文相同也各自求值。Overlay 显式复用 base cell；
   新 Slot 的 KeepUnchanged 仍产生新 cell，并保留实际调用事实。
8. crash before cell commit留下Missing；retry允许第二次调用；crash after commit不重复生成healthy cell。
   两个worker并发完成同一 CellSlot时，put-if-absent只接受一个cell，另一方读取AlreadyFilled。
9. rewind使row descriptor off-lineage时Getter fail closed；不会选择相同ordinal的另一branch row。
10. 删除RecapGrid数据库后，durable Timeline ledger、raw History与MaintainerControlPlane仍在，可按exact active
    GridBuildRecipe完整rebuild cells/views。recipe已经active但fulfilled view cache尚未更新或已丢失时，Getter fail
    closed，恢复不会把overlay与full rebuild混淆，也不需要跨库repair。
11. inspect/export能在不加载Completion provider和secret时列row、column、missing、view 与来源关系。
12. 大grid fixture验证按row/column查询不依赖无界目录扫描或全表内存materialization。
13. committed cell 关系无效或 SQLite integrity failure只允许whole-Grid reset/rebuild，不出现targeted repair状态。
14. Agent请求未知FamilyDefinition、越权scope或超预算创建column时，control plane零变化；合法control event在Grid reset后仍在。
15. 悬疑分析fixture中，`XSuspicion` overlay回填不改旧`CulpritHypothesis` cells；激活后的新rows允许后者读取前一row的
    X疑点而更新。full-grid recipe则从Row 0重算全部columns，证明新专题发现可沿wavefront逐row传播，同时不存在同row循环。
16. `NthPrevious=1`严格选择exact selected Timeline chain上的前一sealed row及同一active recipe fulfillment；目标slot
    missing/damaged时fail closed，不跳到更早healthy row或同ordinal sibling branch。
17. Prepared后删除Grid、改变active recipe并使Timeline/Control不可用，request仍按frozen bytes byte-identical恢复；Started
    Refuse零client/零derived write，explicit restart只产生新的provider attempt。
18. no-active时无论Timeline empty/nonempty都raw-only且零Store open；empty Timeline + active同样raw-only且零Store open以避免首row
    lifecycle死锁；nonempty Timeline + active missing fulfillment只能返回`Unfulfilled`，不得raw fallback。

## 11. Open decisions before implementation

1. candidate/旧cell retention与GC规则。

Timeline durable decision已由WP-01C关闭：唯一production backend是独立SQLite ledger（`DELETE` +
`synchronous=EXTRA`），per-Ref canonical locator、verified backup/restore与explicit abandon各自有closed typed library action；它与
可reset的Grid数据库保持独立lifecycle。两路independent review与final serial validation均GO，现已成为WP-02的complete handoff；
这不表示actual cyber activation。C3D hard cut后的Timeline Schema V2已由commit `7a9c0b3b`完成，仍只在Linux上启用durable lease/fsync；
V1 root inert且没有normal read、fallback或migration。

Control carrier decision已由WP-02关闭：V1只使用
`<repo>/control/recap-grid/v1/refs/<ref>/timelines/<timeline>/control.json` bounded canonical whole-state carrier与双lock；不写raw
SessionJournal、不读取old operator config、不放在可reset的`derived` root。public factory内部跟随exact Timeline locator，backup/export/temp
不参与normal discovery；restore/reinitialize只在strict-readable current+exact expected head+exclusive lease下安装new instance并令
generation等于current generation + 1。两条independent review与final serial validation已GO，现为WP-03的complete handoff；
这仍不表示production cutover。

## 12. Implementation boundary

本文通过只表示Shape/Rule锁定及SQLite目标选择，不表示旧系统迁移方案或production implementation已经批准。
施工计划已经拆为WP-00至WP-08；WP-00 baseline/walking skeleton、WP-01A Timeline contracts/partition、WP-01B raw
integration、WP-01C durable ledger、WP-02 content-addressed ControlPlane、WP-03 SQLite Grid Store与WP-04
build-engine/Manager与WP-05 pure-read Getter/ContextComposer均已完成；WP-05 final evidence为Getter 21/21、Getter public
surface 2/2、Control composability 3/3、SessionJournal phase2/recovery 3/3、Walking 16/16、solution build 0 warning / 0 error、
docs 15/0、diff clean与两路independent closure GO（P0=0，P1=0），现为WP-06的complete handoff。WP-04 final evidence为
Manager 57/57、Manager public 1/1、
Walking 15/15、HistoryTimeline public 3/3、solution build 0 warning / 0 error、docs 15/0、diff clean与两路
independent GO（P0=0，P1=0）。
WP-06 final evidence为Runtime 56/56、Completion 471/471、Runtime public surface 2/2、Walking 18/18、solution build
0 warning / 0 error、docs 15/0、diff clean与两路independent closure GO（P0=0，P1=0）；exact route、V1 renderer/parser、
leader/follower scheduler与operational evidence成为WP-07A的complete handoff。WP-07A当时把exact deferred
route落到独立Hosting owner，把pure-read progress落到Manager，并新增临时candidate operator子树；focused CLI 5/5
已在closure tail扩为8/8，覆盖online/offline cap与raw drift、Ref隔离、真实Runtime build、pure-read promotion、strict materialization、
strict bounded connections零mutation与Timeline/Control/Grid maintenance的raw/四域byte isolation。Hosting operational evidence只在首次
真实work materialize并按field/event/retained-total bytes限界；最终Hosting 16/16、Hosting public 1/1、Manager 60/60、Completion registry
lifetime 6/6、CLI candidate 8/8、Walking 20/20、Timeline cursor 2/2、stable/old CLI targeted 7/7、solution build 0 warning / 0 error、
docs 15/0、diff clean与两路independent closure GO（P0=0，P1=0）。WP-07B完成Galatea/CLI disposable online vertical；
Agent-facing Control与Tool continuation由WP-07C complete handoff承接。
WP-07B final evidence为Hosting 19/19、Online 21/21、Galatea actual candidate 7/7、CLI candidate 10/10、raw audit
19/19、public surfaces 3/3、Walking 22/22、solution build 0 warning / 0 error、包漏洞扫描零命中、docs 15/0与diff clean；
两路independent closure均GO（P0=0，P1=0）。
WP-07C已加入Control V2 terminal receipts、strict AgentControl、显式built-in provision与
ToolResult/ToolContinuation exact frozen binding。receipt保存Control-owned canonical command/result identity与原instance/generation，
不保存whole next head；replay只报告current head及head-advanced/instance-replaced证据。当前tail focused证据记录在WP-07C implementation
record；其中Family/Definition/overlay登记是明确fixture precondition，真实CLI Host随后经Runtime missing build，并在
正式`run-online-turn`内由main provider发出AgentControl promotion ToolCall；同一Hostpure-read检查exact proof、提交receipt、保留
ToolResult raw-tail，且紧随其后的completion读取active contribution。Galatea继续覆盖同一frozen recovery/authority等价边界。
最终证据为Control 45/45、AgentControl 20/20、Completion 482/482、CLI candidate 12/12、Walking 23/23、solution build
0 warning / 0 error、vulnerable package scan零命中、docs 15/0、diff clean与两路independent closure GO（P0=0，P1=0）。
WP-08现已formalize这些callers、删除candidate nesting和old owners；final source evidence与外部`NotRun` gates见
[`WP-08 completion record`](derived-recap-grid-wp08-atomic-cutover.md#implementation-completion-record2026-08-12)。
Post-cutover cadence A0-A2随后交付独立repo-owned Cadence authority、reserve-aware online/offline Timeline seal与
reserve-aware Getter/context selection；target deployment policy为B=60,000、R=24,000。单次seal/build-read/anchor
共用262,144 raw-event operation cap；它是typed bounded work，不是长期容量方案。C2 rolling built-in、C3 incremental
Manager、C4 rollover/retention与C5 cyber activation仍由
[`cadence/capacity audit`](derived-recap-grid-cadence-capacity-and-activation-audit.md)保持Open。
WP-01C final evidence为Timeline 156/156、raw 19/19、walking 13/13、
public surface 2/2、solution build 0 warning / 0 error、docs 15/0、diff clean与两路independent GO；commit evidence由containing commit提供。
其后WP-02 final evidence为Abstractions 15/15、Control 26/26、Control public surface 2/2、Walking/architecture 13/13，
并重跑Timeline 156/156与Timeline public surface 2/2；solution build 0 warning / 0 error、vulnerable package scan零命中、
docs 15/0、diff clean与两路independent GO。
其后WP-03 final evidence为Store 40/40、store-only CLI 1/1、Store public surface 2/2、Walking 14/14、
Abstractions 15/15、solution build 0 warning / 0 error、vulnerable package scan零命中、docs 15/0、diff clean与两路independent GO。
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
