# RecapGrid Store SQLite v5

状态：2026-09-17 已在合成、停止的 repository fixture 上实现并验证；尚未迁移真实实例。

本说明记录当前 Store 的持久语义与离线 V4→V5 operator 合同。旧
[v4 合同](recap-grid-store-sqlite-v4.md)保留历史事实，不是当前写入合同。DDL 的唯一
owner 是
[`SchemaV5.sql`](../../../../prototypes/SessionJournal.RecapGrid/Store/SchemaV5.sql)，
读写、完整验证和分页导出由
[`SqliteRecapGridStore`](../../../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs)
实现；升级与恢复入口分别是
[`StoreUpgrade`](../../../../prototypes/SessionJournal.RecapGrid/Store/StoreUpgrade.cs)
和
[`StoreRestore`](../../../../prototypes/SessionJournal.RecapGrid/Store/StoreRestore.cs)。

## RowWork 是行选择权威

V5 在 `(RefId, TimelineId, RootRecipeDigest, HistoryRowId)` 下持久化唯一
`RowWork`。其 canonical 内容与 SQL 投影同时冻结：

- 实际 `ProducerTarget`，包括有序 logical column 与 definition；
- exact `PreviousHistoryRowId` / `PreviousRowResultId`；
- 每列的 `Evaluate` 或 `Reuse` assignment；Reuse 精确记录来源 `CellId`；
- 由完整 canonical work 计算的 `WorkId`。

新行在任何 provider dispatch 前先提交 RowWork；并发提议遵循 first-winner，既有
work 优先，不能用当前默认 policy 覆盖。新 cell 以 `WorkId + LogicalColumnId` 定位，
新 row 保存同一 `WorkId`，二者发布时都重新验证所引用的持久 work。SQL 仍保留旧
recipe/history 唯一约束，用来保护升级保留下来的对象关系；它不是绕过 WorkId 的新写入
入口。

Full、Overlay 与逐行 producer 是不同事实。root 仍是 adopted recipe；RowWork 的
`ProducerTarget` 才是该行实际 producer。exact prior 可以跨 producer policy，但必须处于
相同 Ref/Timeline/root 的直接历史邻接。Overlay 的非重算列只复用 assignment 冻结的
exact `CellId`；验证还要求该 cell 来自同 history row 的有效 base work，不能按当前列名
重新选择。

物理槽位仍是 `derived/recap-grid/v1/grid.sqlite`；目录名中的 `v1` 不是 SQLite schema
版本。`store_metadata.schema_version` 与 SQLite `user_version` 均为 `5`。新增
`row_work`、`row_work_member`，`cell_artifact` 与 `row_view` 新增 `work_id`；旧
CellId、RowResultId、正文、Outcome、producer definition、fulfilled 引用和
StoreInstanceId 在升级中原样保留。

## verify、export 与 cursor

`recap-grid verify` 是 read-only/no-create 的 full verifier。除 SQLite integrity、foreign
key、metadata counters 与原有 cell/row/fulfilled 关系外，它逐项解码 RowWork canonical，
核对物理 key/member、actual producer、exact prior、Reuse source，以及每个 V5 cell/row
对 WorkId 的归属。任一关系不闭合即 `unhealthy`。

`recap-grid export` 仍只是有界诊断投影，不是导入格式。顺序为 `cell`、`row-view`、
`fulfilled`、`row-work`；默认只报告 kind/key/实际 JSON byte 数，只有
`--include-content` 才返回 `jsonBase64`。row-work JSON 包含 root、actual producer、exact
prior 与 ordered assignments；cell slot 和 row JSON 都包含 `workId`。export 在每个阶段
执行与 verify 相同的相关 RowWork 关系检查，损坏数据不会被正常投影。

cursor wire 仍是 canonical base64url V2。原 cell、row-view、fulfilled 三种 V2 cursor
保持可读，新增 typed row-work cursor；旧 V1 继续拒绝。单页最多 128 items / 2 MiB，
`Incomplete` 时必须使用返回的 exact `NextCursor` 继续。

极端多列时，cell phase 为逐 cell 闭合验证，可能重复解码同一 RowWork；这是当前诊断
性能限制，不改变输出和验证语义。

## 显式 V4→V5 升级

普通 Open/OpenReader 遇 V4 仍返回 `UnsupportedSchema`；Getter、Host 与启动流程都不会
隐式迁移。正式入口是：

```text
recap-grid upgrade-store-v5 --input <stopped-repository-copy>
recap-grid upgrade-store-v5 --input <stopped-repository-copy> --apply
```

默认是 dry-run。它在 Store 同目录构造并严格验证临时 V5 后删除临时文件；正常返回时
active authority bytes 不变，但“纯预览”不等于完全没有临时文件 I/O。异常进程终止可能
留下 `.grid.upgrade-v5.*` 临时文件，后续命令会要求 operator 先 inspect 并人工处理，
不会猜测或自动清理。

apply 仅用于停止的 repository 或获准隔离副本，并取得 Store exclusive lifetime lease。
它先创建 exact-name V4 backup，file fsync 后 directory fsync，再 strict-verify backup；
V5 replacement 只从该 durable snapshot 构造。replace 前再次核对 backup 与 active 的
identity、logical counters 以及 physical witness（length + SHA-256），任一漂移都拒绝
替换。replace 后 directory fsync 并 strict-verify V5。重复对健康 V5 执行返回
`AlreadyCurrent`。

V4 complete row 可直接重建 RowWork。V4 partial cells 则由 CLI 对 exact
`(RefId, TimelineId)` 的 Control registration 与 selected Timeline 只读求证，再把唯一
证明交给 Store；Store 自己不选择 scope、target 或 prior。active/inactive candidate、Full、
Overlay 和多 Ref 都按实际证据迁移；unprovable/ambiguous/unavailable 或 orphan partial
明确拒绝。整个过程不构造 completion client，也不调用 LLM。

升级只替换 Store 文件；raw Journal、HistoryTimeline、Cadence 与 Control 均只读且不迁写。

## 双 witness 恢复 V4 backup

恢复是显式两步协议：

```text
recap-grid prepare-restore-store-v4 --input <stopped-repository-copy> \
  --backup <absolute-exact-v4-backup>

recap-grid restore-store-v4 --input <stopped-repository-copy> \
  --backup <absolute-exact-v4-backup> \
  --confirm-active-length <n> --confirm-active-sha256 <hex> \
  --confirm-backup-length <n> --confirm-backup-sha256 <hex>
```

prepare 在 exclusive lease 内 strict-verify 当前 V5 与 exact V4 backup，返回两侧 evidence。
restore 要求逐字确认 active 和 backup 的 physical witness，并在 replacement 前再次验证两侧；
任一侧改变分别返回 `ActiveChanged` / `BackupChanged`。恢复使用同目录 durable temporary、
atomic replace、directory fsync 与最终 strict V4 verify。成功恢复或重新观察到相同 V4 返回
`Restored` / `AlreadyRestored`。

恢复成功后 active 是 V4，普通 reader 因而仍拒绝打开。系统不会自动向前迁移；operator
检查结果后必须显式再次运行 `upgrade-store-v5`。restore 在 replace 前清理 temporary 是
best-effort；当前若删除失败，返回值的 P2 可诊断性有限，因此仍须检查同目录 residue。

升级或恢复在 replace 后但 durable settlement 尚不能确认时返回 `CommitIndeterminate`，
并携带 intended/backup evidence、best-effort observed active 与唯一 next action。此结果不
授权自动 retry 或自动 restore；先重新 inspect/verify 当前 active 与 backup。冷进程 crash
测试覆盖 full/partial 的每个 durable phase，并验证重开只得到合法 V4 或 V5 状态。

## 证据入口

- V4 full/partial/Overlay/multi-ref 与外部 authority 不变：
  [`ProgramRecapGridV4UpgradeTests`](../../../../tests/SessionJournal.Cli.Tests/ProgramRecapGridV4UpgradeTests.cs)。
- CLI 双 witness restore 与 `AlreadyRestored`：
  [`ProgramRecapGridV4RestoreTests`](../../../../tests/SessionJournal.Cli.Tests/ProgramRecapGridV4RestoreTests.cs)。
- apply witness、`AlreadyCurrent`、Overlay shared cell 与失败边界：
  [`StoreAuthorityRegressionTests`](../../../../tests/SessionJournal.RecapGrid.Store.Tests/StoreAuthorityRegressionTests.cs)。
- RowWork verify/export/cursor 与 4,097 actual work rows：
  [`StoreRowWorkMaintenanceTests`](../../../../tests/SessionJournal.RecapGrid.Store.Tests/StoreRowWorkMaintenanceTests.cs)。
- upgrade/restore 的真实子进程 fail-fast 矩阵：
  [`StoreUpgradeCrashRecoveryTests`](../../../../tests/SessionJournal.RecapGrid.Store.Tests/StoreUpgradeCrashRecoveryTests.cs)、
  [`StoreRestoreCrashRecoveryTests`](../../../../tests/SessionJournal.RecapGrid.Store.Tests/StoreRestoreCrashRecoveryTests.cs)。

这些证据均来自合成 repository；不表示真实 `.atelia/galatea` 已升级、服务已部署或包已发布。
