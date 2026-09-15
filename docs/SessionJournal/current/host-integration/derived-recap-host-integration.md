# SessionJournal RecapGrid host integration

状态：WP-08 formal source cutover Complete；C2D real-provider与本机actual cyber activation Complete；current code/tests与actual
repo authority仍须分别核验。

## Single-owner composition

```text
SessionJournalEngine
  + RecapGridCompletionHost
      + strict connection/profile catalog
      + exact route manifest
      + one CompletionConnectionRegistry
      + lazy RecapGrid Runtime
  + RecapGridOnline
      + HistoryTimeline mutator/build reader
      + Getter candidate source
      + lazy Manager
```

CLI和Galatea共享正式 RecapGrid products，但各自负责application phase gate与operator/UI mapping。
Galatea Program只构造一个`RecapGridCompletionHost`；`GalateaHostService`是其唯一dispose owner。main-agent与
recap runtime借用同一connection registry中的clients，shutdown顺序是Online/request handles → Runtime
drain → registry distinct-client cleanup。

Route manifest V2只把exact key绑定到connection id、并发与timeout。Completion request与connection有意不提供
caller-selected output cap；具体adapter在省略表示不限量/模型最大值时省略provider字段，否则只发送所选模型的
provider-reported maximum。route切换因此不会遗留provider-incompatible token override。

CLI `recap-grid build` 直接使用 V3 catalog 与人工 route `ParseJson`，借用单个 registry 给 RecapGrid host；
其他 CLI 命令的 V2 connection 文件合同保持。CLI 默认 factory 延迟读取 Codex subscription 环境到实际 client 创建，
Galatea 保留原启动验证。无 missing work 不读取 subscription 环境/认证或创建 client，但普通 catalog 的
`baseAddressEnv/apiKeyEnv` 配置解析仍提前执行，不宣称整个环境无依赖。

Runtime start/settled 与 Manager 行提交/已有行 callback 提供独立操作进度。CLI 在 stderr 输出 `[recap-build]`
事件和 30 秒 waiting 提示，stdout 保留单份业务 JSON；模型完成不能代替 Store 提交证明。
观察者异常不得改变结果。派生结果字段按实际类型保留，超量 telemetry evidence 可显式省略而不覆盖业务 status/result。
具体命令与各事件语义见 [CLI 指南](../../../../prototypes/SessionJournal.Cli/README.md#构建与即时诊断)。

## Phase ordering

- Fresh/NewRequest：创建per-turn Online，执行formal composite lifecycle，再以无Agent Control tool的runtime开始completion。
- Prepared：先按frozen identity绑定completion与tool profile；绑定本身lazy且不打开Timeline/Control/Store。
- Started：启动时strict config/connections已冻结；Refuse早于本次current connection
  selection/client、route与derived state。
- ToolContinuation：先exact frozen tool profile/operation/sequence，再绑定无工具的current completion，最后打开Online。
- ToolResult NewRequest：保留ToolResult raw tail，以无工具runtime进入readiness；不在该phase重新seal。

上述 Prepared/Started 包含 [v9 语义计划](../contracts/completion-request-prepared-v9.md) 与历史 v7/v8 exact
两类。v9 先验证持久计划，Refuse 不要求 projector；允许发送后才用当前 host projector 表达已选内容。
旧 v7/v8 exact 恢复不调用新 projector。换风格不重新打开 Getter/Manager 选择 cell，不改变 uncertain 权限。
host 的主请求 runtime 和真正发出辅助请求的 runtime 都需能处理 `SessionInputObservationMessage`；核心
query、audit 与 setup 比较只读 typed 内容，不要求接入 Galatea 或 md-json。

Lifecycle audit authority来自同一mutable `SessionJournalEngine`在Prepare动态作用域内签发的owner-bound
snapshot。Online可以用同一snapshot的独立cursors先offline reconcile、再offline build；cursor释放自身
enumerator/lease，不销毁共享snapshot。任何raw head变化均返回typed authority mismatch。

## Readiness and build

1. Online在PreObservation允许的boundary执行 Timeline reconcile和bounded seal；必要时完成一次bounded
   offline audit。
2. Getter以同一repository/Ref/Timeline/Control/Store authority执行pure-read Resolve。
3. empty Timeline或no-active直接返回raw-only，且不打开Manager或provider；new-session bootstrap 已有 Store 与 active empty-Timeline recipe 仍不改变这个首轮上下文结论。
4. non-empty active且current fulfillment缺失时才lazy创建Manager，先InspectProgress，再按budget Build
   `LiveActive`。
5. candidate build不自动activate；promotion在fresh head-through fulfillment上以zero-new-call重证后执行
   operation-aware Control CAS。

## Formal callers

- CLI：稳定`recap-grid`命令树和唯一顶层`run-online-turn`；read-only/create/maintenance命令在provider
  factory前终止。Fresh/NewRequest不注入`recap_grid_control`，Control mutation只走显式operator命令；
  historical frozen recovery仍exact bind tool profile。所有branch mutation要求exact Ref confirmation。
- Galatea：strict RecapGrid config包含deferred route manifest、bounded profile catalog与exact bootstrap profile；
  historical profiles保留用于frozen recovery，禁止fallback；fresh/NewRequest不注入`recap_grid_control`。
  `create-if-missing`只在unpublished same-parent session candidate中，
  以`GalateaFirstTurnBootstrapPolicy`创建并验证Cadence、empty Timeline、Control、Store、按该 user names 展开的 V6 asset、
  empty-Timeline full recipe 与 active recipe；它不读取 route、不创建 Completion client 或 dispatch provider，因此首轮 context
  仍是formal raw-only。existing repository与maintenance path均不补写。
- Galatea在session attach之后提供两条彼此独立的纯读观察链：`RecentTurnsResponseV1.recapGridReadiness`来自Getter Resolve，
  仅Unfulfilled时调用Manager InspectProgress；`GET /api/v1/recap-cadence-progress`则从exact Cadence policy、
  selected Timeline head row与同一captured raw head测量recent suffix的PlanningUnit/HistoryLoad进度。
  后者不进入recent/SSE grammar；两条service inspection区段的provider/build/write均为零。

HTTP cadence route先复用既有`GetSessionAsync`。`create-if-missing`用户第一次直接GET missing repository时，
该session attach会先执行既有 SessionJournal/Cadence/Timeline/Control/Store/asset/recipe structural bootstrap；因此整个route不承诺
zero-write。attach/bootstrap完成后，cadence service inspector才在per-session `TurnLock`内纯读，且不创建
Completion client、Online或Manager；busy在任何Engine/Timeline/Cadence read前返回typed 503。
DTO把HistoryLoad编码为canonical decimal string，显式给出B（recap interval）、R（minimum recent reserve）、
threshold和remaining。未选first replay-safe boundary时`B+R`只是ideal threshold；选中boundary后，
`measured boundary load + R`才是包含overshoot的effective threshold。tracked browser在初始recent、terminal
current确认与Undo/reconciliation之后best-effort刷新；fresh/resume 202在accepted body读取前、turn-busy、
active turn与rewind pending都会保留上一稳定边界并标stale，且仅在
progress与exact RecapGrid readiness观察到相同raw head时显示exact。HistoryLoad不是provider token数或完整
context-window load。

## Boundaries

- 对既有 path，built-in assets、Store、recipe与activation只可由operator显式provision/register/compose/build/promote；
  Galatea的missing-session bootstrap则在private staging内provider-free地创建该 user 的完整 empty-Timeline Grid（含 active recipe），
  不读取route或dispatch provider。它不是既有 repository 的repair入口。
- `recap_grid_control`的receipt支持幂等replay与indeterminate settlement，但不把uncertain external effects
  描述成exactly-once。
- old v4-v8/rebuild legacy roots inert；只有formal legacy-root operator可以archive/delete。
- real-provider HTTP/caching/economic canary已在C2D独立人工环境完成；deterministic tests仍不能替代下一次provider/config revision的
  fresh canary。exact evidence见[C2 rolling maintainers](../../work/active/derived-recap-grid-c2-galatea-rolling-maintainers.md)。
