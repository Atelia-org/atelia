# DerivedRecap Grid WP-03：SQLite RecapGridStore

状态：Complete；A0-A5、两路independent review与final serial gate已完成；WP-04 Ready；current production尚未切换

只需加载：目标设计、总计划、WP-02 handoff、本文和 WP-04 摘要。

## Intent

实现Derived artifact的唯一live store：SQLite保存immutable Cell/RowView canonical bytes、validated locators与可重建
fulfillment refs。没有remote call、Manager policy、targeted repair或文件blob第二真源。

## Internal slices

- **A0 schema/native/query**：dependency/version、checked-in strict schema、PRAGMA与query plan；
- **A1 lifecycle + Cell**：factory/owned handle、first-winner Cell与sticky Invalid；
- **A2 missing + RowView**：exact prerequisite、header/member atomic commit；
- **A3 fulfillment**：formal five-field key与idempotent ref；
- **A4 maintenance/reset/crash/CLI**：read-only actions、exact witness、child FailFast与stable command group；
- **A5 public/architecture/docs**：assembly-external surface、package graph、solution与handoff。

A0对Directory+JSON只做了结构性反证：它会引入第二套transaction/orphan/index协议；没有虚构“同fixture实跑”的对比证据。
SQLite gate已经成立，因此production代码、dependency与tests中不保留Directory loser或backend selector。

## Plan lock（2026-08-11）

- 唯一backend为Store assembly自有SQLite；canonical slot是
  `<repo>/derived/recap-grid/v1/{grid.sqlite,lifetime.lock}`，V1 Linux-only；
- direct project只引用`SessionJournal.RecapGrid.Abstractions`；direct package固定
  `Microsoft.Data.Sqlite 10.0.10`与security override `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`；
- 选定rollback journal `DELETE` + `synchronous=EXTRA`、page 4096、private cache、pooling false、FK ON、
  `trusted_schema=OFF`、temp MEMORY、busy timeout 0与code-owned `max_page_count`；不再保留WAL候选代码或配置；
- normal handle持shared lifetime lease，mutation只重试已经物化的短local transaction；reset持exclusive lifetime，使用exact
  physical witness与同目录empty DB atomic replace，crash只允许old或empty-valid；
- schema以WP-02 formal canonical types为authority：Fulfilled key没有Target字段，RowView包含TimelineId/HistoryRowId，first-row
  predecessor使用injective tagged BLOB；target文档中的旧示例仅作历史Shape说明；
- CLI只新增独立顶层`recap-grid`组，不改旧`recap`命令或production composition。

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
- `recap-grid inspect/export/verify/reset --prepare/reset`；
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
- fulfilled five-field composite key/FK绑定 exact recipe/row/view；
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
9. inspect/export/verify `Mode=ReadOnly`、no-create、code-owned 128-item/2MiB page与128-error bounds、默认隐藏Cell正文，provider factory零调用；V1不暴露`--limit/--max-errors`；
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

## Implementation record（final，2026-08-11）

> 本节保留2026-08-11 candidate的历史数值；2026-08-16 `CF-D-04` 因outer JSON composed-size反例把
> current export page cap从4 MiB hard-cut到2 MiB。上方current crash/concurrency matrix已按新cap更新。

- 新增唯一production owner `SessionJournal.RecapGrid.Store`，direct project只引用Abstractions，direct pins为
  `Microsoft.Data.Sqlite 10.0.10`与`SQLitePCLRaw.bundle_e_sqlite3 2.1.12`；checked-in `SchemaV1.sql`、strict
  schema/metadata/count caps、DELETE+EXTRA、private/no-pool、FK/trusted-schema/temp/page/max-page gates均由Store持有；
- canonical layout固定为`<repo>/derived/recap-grid/v1/{grid.sqlite,lifetime.lock}`。normal只跟随exact slots，不扫描temp、orphan、
  old recap roots或其他durability domains；V1 durable flock/fsync明确Linux-only；
- public factory交付`Create/Open/OpenReader`与owned disposable Reader/Writer；三种write transaction均有first-winner/idempotence、
  prerequisite、Busy/Limit/Disposed/Invalid及`CommitIndeterminate(Intended, Observed?)` closed results；
- provider command会在执行前重装busy handler，故writer control改由native SQLite handle明确安装`busy_timeout=0`并执行
  `BEGIN IMMEDIATE/COMMIT/ROLLBACK`；`max_page_count`只在取得writer后设置/复验，local retry不跨业务物化或远程调用；
- maintenance交付read-only/no-create `Inspect/Export/Verify`和closed-store exact physical witness `PrepareReset/Reset`；Export page
  按Cell/View PK与Fulfilled五字段composite PK逐表keyset，使用可逆typed cursor；每个物理row复用strict validator，Fulfilled诊断携带
  `ViewDigest`。page受128 item/4MiB约束且默认不返回canonical正文；reset只替换Grid DB，发布后不清理moved source，crash只允许old或empty-valid；
- CLI新增顶层`recap-grid inspect|export|verify|reset`，其中`reset --prepare`先输出exact physical `length/sha256`；throwing provider
  factory fixture已证明absent/no-create与prepare -> wrong -> exact reset全流程零provider creation；
  old `recap`命令与current production composition没有切换；
- child harness覆盖Cell、RowView、Fulfilled三种transaction的before BEGIN、after statements-before COMMIT、after COMMIT，以及
  reset before/after publish；reset fixture建立真实SessionJournal、HistoryTimeline与Control stores，并逐文件复验Grid root外inventory/bytes exact；
- repository path从existing ancestors到repo root及Grid descendants逐段拒绝symlink/reparse；四个Create/Inspect/PrepareReset/Reset入口
  在symlink repo root上fail closed且external target零mutation；
- 三种write在native COMMIT开始后只把可确认ROLLBACK的BUSY降回普通retry；其他IO/FULL/hook failure关闭旧connection再返回
  `CommitIndeterminate(Intended, Observed?)`，native-return与settled-flag窗口都有三类回归；
- final serial evidence：Store full 40/40（含Cell/View/Fulfilled三页Export、首续页query-plan、assignment collision、8类corruption、
  commit/reset child crash）、CLI store-only E2E 1/1、public surface 2/2、Walking 14/14、Abstractions 15/15、
  solution build 0 warning / 0 error、vulnerable package scan零命中、docs 15/0与diff check clean。CLI full为67/68，唯一
  `CompletionTargetIdentityFactoryTests` fingerprint失败与冻结基线一致，且对应production/test files与HEAD exact无diff；新CLI fixture独立1/1。
- 两条independent review经首轮发现并闭合symlink root、indexed Export/Verify、reset witness、exact RowView spec、
  native COMMIT settlement、真实raw/Timeline/Control bytes与architecture gates后，最终均给出GO（P0=0，P1=0）。
  containing commit是本记录的commit evidence；这不表示current production已cutover。

## Handoff to WP-04

交付 deterministic store fixture、failure injection seam、CLI evidence与无provider的 Cell/RowView APIs。
