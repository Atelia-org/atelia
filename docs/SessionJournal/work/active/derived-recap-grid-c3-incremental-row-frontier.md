# RecapGrid C3：逐 row 增量 frontier 重构

状态：C3A、C3B Complete；C3D source implementation candidate等待最终closure；C3C pending。

上游设计：

- [DerivedRecap Grid target design](derived-recap-grid-target-design.md)
- [Cadence、capacity 与 activation audit](derived-recap-grid-cadence-capacity-and-activation-audit.md)

本文冻结 C3 的最小主干。它不以调大 `MaximumSelectedRows`、增加 retry 或引入第二个 mutable campaign
checkpoint 作为修复；它把已经成功发布的每个 RowView 本身变成 durable progression assignment。

## 1. 要修的不是一个常量

历史WP04 Manager 在 dispatch 前冻结并 materialize 从 root 到 through 的完整 selected path，再把
`MaximumSelectedRows` 同时当作 authority discovery 与本次 work budget。Galatea production 传入 4,096，
所以第 4,097 行不是“本次少做一点”，而是每次 retry 都在同一位置 `BudgetExceeded`，永远不能继续。

这违反 RecapGrid 的主语义：

```text
progress unit = one (RecipeDigest, HistoryRowId) RowView

one row step:
  freeze exact Timeline/Control/Store authority
  derive this row's exact EvaluationKeys
  reuse already committed Cells
  dispatch only missing Cells for all target Maintainers
  publish the immutable RowView assignment
```

Cell 是否过时由 EvaluationKey/definition/prior/history hashes 决定，不需要另造 mutable stale flag。一次操作可以
做一行或多行，但停止后已发布的行必须成为下一次的准确起点；预算只能控制本次推进量，不能决定整个 ledger 是否还能工作。

## 2. 冻结的设计裁决

### 2.1 Immutable per-row assignment 是唯一进度 authority

Store schema V2 为每个 row/recipe 保存唯一 assignment：

```text
coordinate = (RefId, TimelineId, RecipeDigest, HistoryRowId)

value commits:
  RowDescriptorDigest
  TargetDigest
  ViewDigest
  PreviousHistoryRowId?
  PreviousViewDigest?
  BootstrapCompleted
```

`RecapRowView` canonical V2 本身提交上述 recurrence；SQLite exact unique index只是同一事务内的 locator，
不是第二份 mutable truth。禁止增加可被 stale writer 覆盖的 `CurrentProgressHead`。

Store publish 必须验证：

- root 的 previous row/view 同时为空；non-root 同时非空；
- predecessor assignment 与当前 assignment 的 Ref/Timeline/Recipe/Target exact 相同；
- predecessor row/view exact 等于 `PreviousHistoryRowId`/`PreviousViewDigest`；
- Store验证 `RowBuildSpec` 提交的 Ref/Timeline/Row/descriptor/previous row coordinate；C3B Manager负责从
  frozen selected descriptor witness唯一派生该spec并在publish前后复验Timeline authority，Store不得反向引用Timeline owner；
- 同 coordinate 同值为 idempotent，不同值使 Store sticky Invalid；
- RowView、members 与 coordinate index在同一SQLite transaction发布。

`FulfilledViewRef`继续只表示 exact whole-head fulfillment/proof，不承担 partial frontier 定位。

### 2.2 Manager 只发现未建 suffix

Manager 对 frozen through row 先执行 exact `ReadViewAt(coordinate)`：

1. 命中且完整健康：该 recipe 已到 through；只需按需补 Fulfilled mapping；
2. 未命中：沿 current selected predecessor path 向后分页，遇到第一份 exact、chain-valid assignment即为anchor；
3. 到 root 仍未命中：anchor 是 FirstRow；
4. 把收集到的最小未建 suffix反转为 root-to-through；
5. 每次 `BuildNext` 只处理 suffix 中第一个 dependency-ready `(recipe,row)`。

Discovery 不接受低于当前 durable lineage 的固定 row-count terminal。分页、每页bytes、canonical artifact bytes、
cancellation 与 SQLite/I/O错误仍有界；但不能因为累计行数达到4,096或65,536而永久拒绝。

同一次 CLI/offline operation应复用冻结的 suffix；进程重启后重新发现是允许的性能代价。若未来需要 O(1)
跨operation定位，可以增加由immutable assignments重建并逐次复验的hint，但它不能成为authority。

### 2.3 Overlay 是两条独立 progression chain

Base与candidate各按自己的 `RecipeDigest`推进。Candidate bootstrap row要求same-row base assignment先存在；缺失时
`BuildNext`先返回/执行base dependency。`BootstrapCompleted`随candidate assignment递推，避免只命中中途anchor后又依赖
root-to-head index才能恢复phase：

```text
full recipe: BootstrapCompleted = true
overlay root before bootstrap: false
overlay current: previous.BootstrapCompleted || current.RowId == BootstrapRowId
```

bootstrap之后的normal candidate row不再要求same-row base；recipe/definition变更产生新的digest namespace，旧assignments
保持immutable且不会被误判为current。

### 2.4 Operation budget 是外层调度，不是寿命上限

正式原语按一条row定义。外层可以有：

- `MaximumRecipeRowSteps`：这次最多发布多少个recipe-row assignments；
- `MaximumNewCalls`：这次最多启动多少次真实Completion；
- cancellation/deadline：停止启动新工作，drain已启动调用；
- page size / page bytes / artifact bytes：限制单次物化与wire对象。

达到外层预算时返回typed continuation/progress；下一次从已发布assignment继续。删除把“最多读多少历史row”同时当作
work budget的 `MaximumSelectedRows`。

### 2.5 Online 调度顺序

Online lifecycle采用Grid-first的稳态顺序：

1. 若current Timeline head存在未完成Grid debt，先做一个recipe-row step，不append Timeline；
2. 无debt时，Cadence/Timeline最多seal一条eligible row；
3. seal成功后立即为active recipe做一个row step；
4. terminal probe仍有debt时返回continuation/backpressure，绝不放行main-agent provider request；
5. 无debt且Getter exact ready后才构造主Agent context。

CLI/offline catch-up可以在一个冻结operation中循环多步；Galatea前台不应无界循环历史backlog。

## 3. 永久容量上限裁决

累计 `MaximumTimelineRows`、`MaximumRowCount`、`MaximumCellCount`、`MaximumRowViewCount`、
`MaximumFulfilledViewCount` 不是operation budget；达到后 retry也无法前进，因此不符合长期rolling系统。

C3/C4交界采取以下规则：

- 删除Timeline与Grid Store按累计对象数量以及database总bytes设置的code-owned固定寿命cap；SQLite `INTEGER`
  计数继续用于diagnostic与一致性验证，database增长只受SQLite格式/寻址、filesystem与实际可用磁盘约束；
- 保留单artifact bytes、单页items/bytes、单次raw audit与provider payload上限；backup/verify/export必须stream/page，
  不能因为整个database超过一个预设总bytes数而失效；
- 计数与ordinal使用checked `long`/SQLite 64-bit范围，溢出、disk full、SQLite full、I/O错误typed fail closed；
- verification/export始终分页，不把全库materialize到内存；
- 不以“未来再做rollover”掩盖当前固定65,536终点。rollover/GC仍可作为磁盘治理优化，但不能是继续运行的前置条件。

## 4. 工作包与提交边界

### C3A — Store V2 progression assignment

- `RowBuildSpec`/`RecapRowView`加入Ref、previous row与phase；canonical/hash升级V2；
- SQLite schema V2与exact `ReadViewAt`；atomic recurrence/index/settlement；
- 删除Store累计cell/view/member/fulfilled count与database总bytes拒绝，只保留单artifact/page/operation bounds；
- pre-release不做V1兼容读取/在线migration；旧derived Store由operator reset/rebuild。

### C3B — Manager row frontier

- `FreezeOperation`改为head-to-anchor minimal suffix discovery；
- 新增/收窄one-row `BuildNext` primitive；
- overlay dependency closure与bootstrap phase来自assignments；
- `BuildAsync`/`InspectBuildProgress`复用同一frozen suffix；
- 删除whole-root hot-path index与`MaximumSelectedRows`。

实现证据：Manager从through沿selected predecessor读取V2 assignment，命中每条recipe的exact healthy anchor后只反转
minimal suffix；overlay bootstrap以独立base/candidate anchors恢复，same-row base requirement覆盖全部未建bootstrap rows。
`InspectBuildProgress`纯读且不capture raw，首个缺失durable assignment即Frontier；Build只在actual missing Cell work时
lazy capture raw，view-only/zero-call fulfillment仍执行轻量raw-head与Timeline/Control final fences。Manager full 73 tests及
Public/Walking、CLI/Online/AgentControl/Galatea focused覆盖restart step=1、branch/rewind sibling exclusion、nested overlay、
damaged anchor、budget/cancel/elapsed与zero-write paths。

### C3C — Online/CLI/Host orchestration

- Grid-first；Timeline seal与Grid fill各至多一步的foreground pass；
- CLI显式bounded catch-up循环并报告remaining/anchor/examined/committed；
- promotion只读current-head assignment并以`MaximumNewCalls=0`重证，不暗中rebuild；
- readiness/progress纯读，不构造provider、不写Store。

### C3D — Timeline V2 lifetime capacity（source candidate）

- hard cut `derived/history-timeline/v2` / Schema V2；V1 bytes inert且无fallback/migration；
- 删除immutable trie path-copy，改为mutable current selected path + whole-head count/root commitment与O(log N) Merkle
  append/truncate；normal read/page/reconcile验证local assignment、inclusion proof与whole head，sticky guard捕获越权middle mutation；
- 移除Timeline累计policy/row/node count、database/restore总bytes固定cap及相应int narrowing；保留single artifact/page/raw audit/
  operation limits；
- `HistoryRecentReserveAnchor`只返回exact stop/count authority，Getter二次分页验证，不持whole path；
- V2实测public 4,097-row CLI sync约18.2秒，真实Manager zero-call vertical通过；65,537 durable rows约341.40 MiB、append
  约107秒，并通过reopen、path首尾、rewind/reselect、verify及backup/restore。4,097/8,194中间点约21.35/42.72 MiB与
  7.2/13.7秒，证明节点总量O(N)且没有旧逐rowO(n²)放大；这些是fixture evidence，不是未来filesystem容量承诺。

每个工作包都按 fresh review → implementation → independent review → focused/affected validation → commit闭合；
不得把全部变更留成一次不可审阅的大diff。

## 5. 必须通过的验收

- 4,096/4,097行：public Timeline与Manager vertical均继续，不再whole-root BudgetExceeded；
- 65,536/65,537累计对象：Timeline与Grid不因固定count拒绝；Timeline V2还须验证reopen/path、rewind/reselect、
  verify与backup/restore，而不只检查一次append；
- partial row：已提交Cells在restart后复用，只补missing cells，RowView最后原子发布；
- restart：reopen后从最近assignment继续，完成后再次reopen为zero provider calls；
- branch/rewind：共同祖先复用，sibling RowId不串用，回到旧branch可复用旧immutable assignment；
- recipe/definition change：进入新digest chain，旧chain不覆盖；
- overlay：base/candidate frontier独立，bootstrap same-row dependency exact，phase可从中途anchor恢复；
- Store reset：空assignment后可从root重建；不触碰raw/Timeline/Control；
- Fulfilled丢失：head assignment健康时只补mapping，zero provider；
- stale/Busy/CommitIndeterminate：已发布immutable artifacts可reconcile，未通过final fences不得产proof；
- provider failure/cancellation：已完成siblings/Cells保留，未完成row不发布假assignment；
- Getter/Materialize：只接受exact whole authority与healthy head assignment，不从missing/corrupt fallback raw。

## 6. 明确不在本包偷做的事项

- Galatea `world-understanding`/`autobiography` prompts、真实Opus调用与actual cyber激活；
- 修改Cadence的`B=60,000`/`R=24,000`语义；
- 把provider usage/cache/route写入durable Evaluation identity；
- 为了兼容未发布的Store V1保留双读、fallback或auto-migration；
- 引入新的scheduler、backend selector或mutable campaign owner。

这些边界让C3只修“长期逐row推进主干”，而不再次把部署、prompt和持久化重构混成一个大工作包。
