# RecapGrid Store SQLite v4

状态：2026-09-14 Timeline 单一行身份切片实施中，集成验证待补；设计与验收入口为 [Timeline 计划](../../../Galatea/timeline-row-identity-simplification-plan.md)。

本说明记录当前 Store 模型与 operator 边界。旧 [v2 合同](recap-grid-store-sqlite-v2.md)与 [v3 说明](recap-grid-store-sqlite-v3.md)保留其历史证据，
不作为 v4 的 schema/API 合同；旧 Store 数据不转换。DDL 的唯一 owner 是
[`SchemaV4.sql`](../../../../prototypes/SessionJournal.RecapGrid/Store/SchemaV4.sql)，读写与维护由
[`SqliteRecapGridStore`](../../../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs)和
[`StoreMaintenance`](../../../../prototypes/SessionJournal.RecapGrid/Store/StoreMaintenance.cs)实现。

## 构建位置与结果

`CellSlot(RecipeDigest, HistoryRowId, LogicalColumnId)` 是普通结构坐标。不可变 recipe、历史行和已发布前驱
确定其输入，因此不再持久保存独立 EvaluationKey、ContentDigest 或 PriorInputProjectionDigest。
`CellId` 与 `RowResultId` 是 Store 分配的随机 128-bit ID，SQL 使用 32 位十六进制文本；它们不从内容派生。

实体仍为 `RecapCellArtifact` 与 `RecapRowView`，分别用 `.Id/.Slot` 和 `.Id/.PreviousRowResultId` 表达结果与来源。
row members 引用 `CellId`。Evaluate 使用当前 Slot，Overlay Reuse 引用 same-row base cell，保留原 ID 与源 Slot。
不同 recipe 不再自动共享同输入或同正文缓存，包括首行。

Store 的 `PutCell(spec, draft)` 与 `PutRowView(spec, stored cells)` 由已验证 spec 驱动；所有成功结果返回
实际持久 `Winner`。同 Slot 不同正文返回首个已存 cell；同 row assignment、成员与前驱返回已存 row。
真实业务差异才冲突，随机候选 ID 不参与相等比较。提交结果不明时按 Slot/row assignment 观察已有记录。

## SQL 是唯一持久表示

物理槽位继续为 `derived/recap-grid/v1/grid.sqlite`；SQLite `user_version` 与 metadata schema 为 `4`，
目录中的 `v1` 不是数据库 schema 版本。

| 表 | 责任 |
|---|---|
| `store_metadata` | StoreInstanceId、schema 与事务内维护的数量计数 |
| `cell_artifact` | 普通 ID、源 Slot、规则/历史关系与正文/Outcome；Slot 三字段非 null 且 UNIQUE |
| `row_view` | 普通 ID、唯一 row assignment、前驱与必要状态；前驱 FK 保留同 scope/recipe/target |
| `row_view_member` | row 的有序列、definition 与 CellId；成员列唯一、引用已存 cell |
| `fulfilled_view_ref` | exact Timeline head/recipe/ThroughRowId 到已存 RowResult 的引用；FK 精确绑定结果、scope、recipe 与历史行 |

历史行只有原 `HistoryRowId`；row 不保存 `row_descriptor_digest`，fulfilled 的 through 列为
`through_history_row_id`。`FulfilledViewKey` 使用 `(RefId, TimelineId, TimelineHeadGeneration, ThroughRowId, RecipeDigest)`；
`RowBuildSpec/RowViewCoordinate/RecapRowView` 及 Getter/Manager 不再携带第二个行身份。

SQL 列和成员关系直接物化对象。删除 cell/row 的整对象 canonical BLOB、fulfilled key canonical 副本及重复对账。
读回与 verify 校验类型、业务关系、唯一性、FK、计数与资源上限；没有另一份隐藏 JSON 权威。
没有 nullable prior 唯一键、FirstRow 特殊索引或另存的 producer prior：cell 来源前驱由源 Slot 与 Timeline 推导。

Completion 与历史/前驱渲染在事务外；cell、row+members、fulfilled 各自短事务提交。正常重开仍 missing-only，
不提供局部删除 winner、改写已发布前驱或跨 Store 拼表。

## 查询、导出与诊断

inspect/verify/export 为 read-only/no-create，保留分页、记录数与正文预算。导出只是当次 SQL 数据的 JSON 投影，
不作为生产导入格式；`RecapGridStoreExportItem` 使用 `JsonUtf8Bytes/Json/FulfilledRowResultId`，CLI 使用
`jsonBase64/fulfilledRowResultId`。cursor wire 为 v2；旧 v1 一律拒绝，不把旧 64 位 descriptor digest 静默解释为 RowId。
cell/row cursor 仍使用 32 位结果 ID，fulfilled cursor 使用确切 `ThroughRowId`。

Getter selection 使用 `SelectedRowResultId/CurrentRowResultId`，CLI selection 输出 `rowResultId`。
`PriorSourceAligned` 比较由 cell 源 Slot 推导的前驱 RowResult 与当前 row 前驱；它不承诺旧的正文等价。
合法 Overlay 可能 NotSatisfied，正文仍可读；缺来源或预算耗尽是 Incomplete。
预算分别统计 `ExaminedRows/ExaminedCells/ExaminedMembers/ExaminedContentUtf8Bytes`；最后一项只度量实际正文 UTF-8 bytes。
Runtime telemetry 使用 Slot、既有身份的 `StoreInstanceId/StoreSchemaVersion` 值与必要前驱 ID，
不保留旧 digest 字段名，也不新增 Runtime → Store 模块依赖。

Store 从 SQL 物化 Abstractions 所使用的 Timeline 坐标，因此 RG0001 允许它构造
`TimelineId/HistoryRowId` 两种不可变值；该例外不开放 Timeline 的读取、维护或写入 API。

## 旧 schema 与最终 Reset

普通 Open/OpenReader/Inspect/Export/Verify 遇旧 schema 返回 UnsupportedSchema；Create 不改写已有旧库。
没有旧 reader、自动迁移、自动 Reset 或自动模型重建。显式离线 Reset 不要求解码旧 cell，仍遵循已有
physical witness、独占 lease、原子替换与 commit-indeterminate 合同；成功后更换 StoreInstanceId。
旧 handle 不可认领新实例，陌生结果 ID 返回 Missing 即可。

真实处置等全部计划重构完成后统一进行：先在旧状态可读时正常收敛所有仍支持执行分支的 pending promotion 与 Recipes 非空 registration，
再停服备份、在隔离副本升级所需 Timeline、Reset Store 与 LLM 重建。promotion 先查 Store proof 后查 receipt；仅保留 Control 文件不能保证未结束的
promotion 可在空 Store 上重放。Journal/Prepared、Timeline/Cadence 和 Control 规则/回执全部保留。
旧冻结请求使用已保存的 ContextSnapshot，恢复时不读取新旧 Recap；新请求才使用新建摘要。
