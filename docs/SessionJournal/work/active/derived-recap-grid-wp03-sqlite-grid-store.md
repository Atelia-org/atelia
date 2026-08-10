# DerivedRecap Grid WP-03：SQLite RecapGridStore

状态：Ready；WP-02 complete handoff已通过independent review与final serial gates，尚未开工

只需加载：目标设计、总计划、WP-02 handoff、本文和 WP-04 摘要。

## Intent

实现Derived artifact的唯一live store：SQLite保存immutable Cell/RowView canonical bytes、validated locators与可重建
fulfillment refs。没有remote call、Manager policy、targeted repair或文件blob第二真源。

## Internal slices

- **A0 disconnected spike**：dependency/version、checked-in schema、queries、crash/contention、CLI；
- **A1 production store**：strict repository、whole-store Invalid/reset、public narrow API。

A0失败时按target design重新打开Directory+JSON决策；不得把失败spike包装成可配置第二backend。

## In scope

- `Microsoft.Data.Sqlite` dependency与runtime version gate；
- checked-in strict V1 create/open schema（unknown/incompatible直接reset，不提供migration runner）；
- `TryReadCell(EvaluationKey)`、atomic put-if-absent；
- `FindMissingAssignments(RowBuildSpec)`；
- `PutRowView(RowBuildSpec, canonicalView)`并验证exact winners/reused cells；
- `ReadView(viewDigest)`与atomic RowView header+exact members commit；
- opaque exact `FulfilledViewKey` read/commit；Store不读取Timeline/Control，组合解析属于WP-05；
- direct reference只允许`SessionJournal.RecapGrid.Abstractions`；WP-02的Control carrier、Reader、admission与locks均不得进入Store；
- restart读取只用`RecapRowView.DecodeCanonical(bytes)`与`FulfilledViewKey.DecodeCanonical(bytes)`恢复typed manifest/key；RowBuildSpec与
  selected cells只用于commit时contextual membership validation，不得作为额外durable preimage或逼Store重写codec；
- canonical bytes重算与locator/member/FK exact validation；
- operation-specific typed results：Cell `Inserted | AlreadyFilled(winner) | Invalid | Busy`；RowView/Fulfillment同key不同view为
  `Invalid`而非general Conflict；RowView/Fulfillment使用`Inserted | AlreadyPresent | Invalid | Busy`；
- `recap-grid inspect/export/verify/reset`；
- bounded query/materialization与query-plan evidence。

## Durable rules

- canonical artifact bytes是semantic authority；SQL columns/member rows是同transaction写入的denormalized indexes；
- locator/canonical/FK任一不一致使whole Store `Invalid`；不在线补表；
- committed Cell/RowView不删除、不覆写；首版无GC；
- fulfillment ref可重建，但same exact key/different view是Invalid，不last-write-wins；
- `reset`只在Store closed后执行；无page salvage、quarantine或Published repair；
- Completion/History materialization全部发生在transaction外。

## Out of scope

- Timeline tables或`ATTACH`跨库join/transaction；
- MaintainerControlPlane data；
- scheduler、Completion、Galatea；
- JSON live sidecar或external content blobs；
- retention/GC/VACUUM optimization；
- remote-call retry。

## Write scope

- new `SessionJournal.RecapGrid.Store` owner/tests/crash harness；
- narrow `recap-grid` CLI inspect/export/verify/reset composition；
- solution/dependency registration；
- 不修改 old DerivedRecap Store behavior。

依赖与layout在A0锁定：直接pin `Microsoft.Data.Sqlite`版本并做Linux native load smoke；runtime version/source/compile-options
必须可观察，安全minimum gate只在A0选定的journal mode确有需要时启用。schema包含application/schema
metadata、`STRICT` tables、non-null FirstRowSentinel与count/byte limits。journal mode、synchronous/foreign-key/busy PRAGMAs、
pooling与backup/reset按每连接实测后只保留一个配置；只有选择WAL才启用WAL安全版本gate。repository本地API可以诚实同步，
不得用`Task.Run`伪装SQLite async。
若无法机械证明all connections/pool已关闭，`reset`必须要求停止Host并通过exclusive-access preflight；不得用删除单一DB
pathname冒充同时处理WAL/SHM或在失败后升级破坏力度。

## Required schema/query gate

至少证明：

- unique `EvaluationKeyDigest`；
- Cell digest + column + definition exact association；
- unique `(Recipe, RowDescriptor, Target, PreviousViewKey)` RowView；
- fulfilled composite key/FK绑定 exact recipe/row/target/view；
- first-row sentinel规避SQLite NULL unique语义；
- primary reads/missing/completeness不做unbounded full scan；
- large fixture `EXPLAIN QUERY PLAN`使用预期index。

## Crash and concurrency matrix

1. before/after Cell commit；
2. RowView header/member transaction；
3. fulfilled ref commit；
4. closed-store reset：DB/WAL/SHM作为同一unit，crash后只允许old或empty-valid；
5. two connections same EvaluationKey：一个winner、另一AlreadyFilled；
6. same fulfillment key same view idempotent；different view Invalid；
7. bounded `SQLITE_BUSY`只重试local commit，不触发业务回调；
8. canonical BLOB、locator、member ordinal/set、FK orphan、unknown schema、truncated/page/integrity统一Invalid；本实例随后拒绝writes；
9. inspect/export/verify `Mode=ReadOnly`、no-create、bounded `--limit/--max-errors`、默认隐藏Cell正文，provider factory零调用；
10. runtime SQLite version、schema与connection PRAGMAs可诊断；
11. 三种commit原语各覆盖before BEGIN、after statements-before COMMIT、after COMMIT-before return；
12. 大fixture分页/流式，`EXPLAIN QUERY PLAN`为SEARCH/index而非脆弱墙钟gate；
13. reset错误确认或无法证明exclusive close时fail closed，raw/Timeline/Control filesystem snapshot exact不变。

## No-Go

- writer queue成为correctness必要条件却未进入contract；
- async API被误当真正非阻塞而占用关键线程；
- WAL/version/backup在未测量前写死；
- targeted row/cell delete绕过unique reservation；
- dual SQLite+file durability。

## Done when

- A0 spike形成Go/No-Go evidence；Go后A1 public surface最小化；
- child-process crash、contention、query-plan、CLI gates green；
- builds/docs/diff green；
- reviewer确认Store不含Manager/Completion policy。

## Handoff to WP-04

交付 deterministic store fixture、failure injection seam、CLI evidence与无provider的 Cell/RowView APIs。
