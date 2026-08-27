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

## Phase ordering

- Fresh/NewRequest：创建per-turn Online，执行formal composite lifecycle，再以无Agent Control tool的runtime开始completion。
- Prepared：先按frozen identity绑定completion与tool profile；绑定本身lazy且不打开Timeline/Control/Store。
- Started：启动时strict config/connections已冻结；Refuse早于本次current connection
  selection/client、route与derived state。
- ToolContinuation：先exact frozen tool profile/operation/sequence，再绑定无工具的current completion，最后打开Online。
- ToolResult NewRequest：保留ToolResult raw tail，以无工具runtime进入readiness；不在该phase重新seal。

Lifecycle audit authority来自同一mutable `SessionJournalEngine`在Prepare动态作用域内签发的owner-bound
snapshot。Online可以用同一snapshot的独立cursors先offline reconcile、再offline build；cursor释放自身
enumerator/lease，不销毁共享snapshot。任何raw head变化均返回typed authority mismatch。

## Readiness and build

1. Online在PreObservation允许的boundary执行 Timeline reconcile和bounded seal；必要时完成一次bounded
   offline audit。
2. Getter以同一repository/Ref/Timeline/Control/Store authority执行pure-read Resolve。
3. empty Timeline或no-active直接返回raw-only，且不打开Store、Manager或provider。
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
  以`GalateaFirstTurnBootstrapPolicy`创建并验证Cadence、empty Timeline与empty Control，使首轮进入formal raw-only；existing
  repository与maintenance path均不补写。
- Galatea progress使用formal `RecapGridReadiness` DTO，来源是Getter Resolve；仅Unfulfilled时调用
  Manager InspectProgress，provider/build/write为零。

## Boundaries

- built-in assets、Store、recipe与activation必须由operator显式provision/register/compose/activate；Galatea的missing-session
  bootstrap只auto-create首轮structural三域，不是full Grid provisioning，也不读取route或dispatch provider。
- `recap_grid_control`的receipt支持幂等replay与indeterminate settlement，但不把uncertain external effects
  描述成exactly-once。
- old v4-v8/rebuild legacy roots inert；只有formal legacy-root operator可以archive/delete。
- real-provider HTTP/caching/economic canary已在C2D独立人工环境完成；deterministic tests仍不能替代下一次provider/config revision的
  fresh canary。exact evidence见[C2 rolling maintainers](../../work/active/derived-recap-grid-c2-galatea-rolling-maintainers.md)。
