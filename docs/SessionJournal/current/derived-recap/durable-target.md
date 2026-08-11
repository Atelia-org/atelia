# RecapGrid durable target

状态：WP-08 formal source cutover Complete；actual cyber activation仍为外部`NotRun`。

## Canonical durable layout

```text
derived/history-timeline/v1/refs/<ref>/
  locator.json
  timelines/<timeline>.sqlite

control/recap-grid/v1/refs/<ref>/timelines/<timeline>/
  control.json
  lifetime.lock
  writer.lock

derived/recap-grid/v1/
  grid.sqlite
  lifetime.lock
```

HistoryTimeline 使用 per-Ref locator 和 durable SQLite ledger，保存 policies、rows、whole head 以及
selected-path persistent indexes。Control 的 single canonical `control.json` 保存完整 state graph、active
recipe、operation receipts 与 whole head。Grid SQLite保存 canonical cells、row views 与 fulfilled records。

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
- old legacy recap slots不参与 normal open、selection、recovery或fallback。它们由
  `recap-grid legacy-root inspect|archive|delete` 以 bounded manifest与fresh confirmation单独治理。

## Rebuildability boundary

raw SessionJournal 仍是历史事实源。Timeline可以从 selected lineage重新构造；Control 的 operator
definitions/recipes与Grid artifacts是repo-owned companion state，必须通过各自public maintenance surface
检查、备份或重建。任一层损坏都 fail closed，不允许从mtime/latest/orphan扫描猜测authority。
