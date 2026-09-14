# RecapGrid durable target

状态：WP-08 与 C2D 的历史交付完成；当前 Timeline 单一行身份已实现并通过 2,001 项本地测试，尚未部署，未操作真实实例。

## Canonical durable layout

```text
derived/history-timeline/v2/refs/<ref>/
  locator.json
  timelines/<timeline>.sqlite

control/recap-grid/v1/refs/<ref>/timelines/<timeline>/
  control.json
  lifetime.lock
  writer.lock

control/recap-grid/v1/refs/<ref>/cadence/
  cadence.json
  cadence.lock

derived/recap-grid/v1/
  grid.sqlite
  lifetime.lock
```

HistoryTimeline 使用 per-Ref locator 和 durable SQLite schema 3 ledger，保存 policies、immutable rows、whole head 以及
mutable selected path；selected path的count/root commitment进入whole head，append以O(log N)更新complete-subtree Merkle
accumulator，reconcile以prefix commitment截断，不再复制immutable trie snapshot。Control 的 single canonical `control.json` 保存完整 state graph、active
recipe、operation receipts 与 whole head。Cadence保存per-Ref R、exact expected Timeline partition fields、
generation/domain digest；它不属于SessionJournal RuntimeConfig。Grid SQLite schema 4 保存 SQL 列与有序成员，cells/rows 使用普通结果 ID，fulfilled 只引用 ThroughRowId。

Control 目录仍为 `v1`，文件内容 writer 为 v4；旧 v2/v3 按源格式读取并保留原 Head/bytes，
正常持久 mutation 才升级。bootstrap 只保留 RowId；Recipe 正文与键不变。当前升级边界见[Timeline 单一行身份计划](../../../Galatea/timeline-row-identity-simplification-plan.md)。

## Durable rules

- 所有 canonical codecs 都是 strict versioned wire：拒绝 unknown/duplicate/reordered/non-canonical fields、
  invalid UTF-16/UTF-8、trailing bytes 与越界输入。
- Timeline、Control 和 Store 各自拥有 identity/head。跨域操作必须冻结并在发布前后重验 exact whole
  authority；相同 ID 不能替代同一 owner handle 或 repository binding。
- SQLite backends 使用短 writer transaction、durable journal settings、strict schema/meta validation 与
  bounded verification。normal open不做全表扫描；maintenance verify才做 bounded keyset full verification。
- atomic publish 后发生 I/O/fync异常时返回 typed indeterminate settlement，不能虚构 zero mutation。
- backup/restore/reinitialize/reset 都要求 fresh exact witness 与 exclusive lifetime；successful replacement
  生成可区分的新 identity 或 generation，旧 head不会发生 ABA。
- Grid Store reset只触碰 `derived/recap-grid/v1`，不会删除 Control 或 Timeline。
- Timeline 当前目录名仍为 `v2`，descriptor 外层 wire 为 v2、SQL 为 schema 3；RowId 的 body/domain v1 不变。
  普通 reader 遇 schema 2 返回 unsupported。`UpgradeSchemaV2(repositoryPath, refId, timelineId)` 是独立离线操作，
  对明确物理 Timeline 取得 Ref 独占锁，分页转换所有行，包括非当前路径行；原 head/generation、policy、path/Merkle、
  guard 和 locator 保持。临时目标 verify 后原子替换并冷验，健康新格式重复执行仅验证。
- 所有重构结束后才统一切换真实数据：先正常收敛相关 pending promotion 和 Recipes 非空 registration，停服备份，
  在完整隔离副本逐库升级所有仍需使用的 Timeline，再 Reset Store 并最后 LLM 重建。Cadence 与 Journal 原字节保留，
  Control 通过旧 codec 读取，下一真实 mutation 才写 v4。不同 Timeline 的逐库发布不是跨库原子事务。
- 旧 `derived/history-timeline/v1` 目录仍是历史 inert slot；本切片的升级入口处理当前 v2 目录中的 schema 2，
  不重新 provision 四个 durability domains，不重新切分历史。旧备份需用匹配旧代码先恢复到完整隔离副本，
  再调用同一窄升级；新 Restore 不直接接受旧 schema，旧 backup manifest 不改写。
- old legacy recap slots不参与 normal open、selection、recovery或fallback。它们由
  `recap-grid legacy-root inspect|archive|delete` 以 bounded manifest与fresh confirmation单独治理。

## Rebuildability boundary

raw SessionJournal 仍是历史事实源。本次保留 Timeline 的全部既有分区与非当前路径行，不用 selected lineage 重切代替格式升级。Control 的 operator
definitions/recipes与Grid artifacts是repo-owned companion state，必须通过各自public maintenance surface
检查、备份或重建。任一层损坏都 fail closed，不允许从mtime/latest/orphan扫描猜测authority。
