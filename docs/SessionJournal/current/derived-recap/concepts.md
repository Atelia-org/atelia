# RecapGrid current concepts

状态：WP-08 formal source cutover Complete；Store v3 切片已完成；当前 [Timeline 单一行身份](../../../Galatea/timeline-row-identity-simplification-plan.md)已实现并通过 2,001 项本地测试，尚未部署。raw selected `RefId` Parent lineage仍是唯一历史事实源。

1. HistoryTimeline、Cadence、Control 与 RecapGrid Store 是独立 companion authorities；都不能替代 raw history。
2. Timeline row 绑定 exact raw range、partition policy、唯一的 HistoryRowId 与 previous-row chain。
3. Control 保存完整 canonical Family/Definition/Recipe graph、active recipe 与 terminal operation receipts；
   mutation 比较 whole `ControlHeadRef`。
4. Full recipe 对目标列全部求值；Overlay bootstrap 对 recomputed columns 求值并对其余列复用 same-row
   base cells；bootstrap 后走 normal full-row evaluation。
5. Manager 以一个 frozen Timeline/Control/Store authority 做 row-major base-to-candidate wavefront；
   missing-only restart 按 `CellSlot(RecipeDigest, HistoryRowId, LogicalColumnId)` 读取首个已存结果。
   前驱输入传 `PreviousView` 与 `PreviousCells`；Runtime 对照 frozen spec 独立校验历史、规则、前驱 ID、
   成员与顺序。没有独立 EvaluationKey 或正文/投影 digest；不同 recipe 不再隐式共享求值缓存。
6. Runtime 的 route key 是 exact `(FamilyDigest, RuntimeProtocolId, SemanticModelId?)`；null 也是 exact key，
   没有 wildcard 或 default fallback。
7. Cadence是per-Ref repo-owned R/expected Timeline policy authority。Timeline仍按first-safe B分区，所有writer
   只在证明candidate后保留至少R时seal；目标policy为B=60,000、R=24,000。
8. Getter先验证current/crossed fulfillment的View/Cells健康，再选择latest R-eligible fulfilled anchor，
   然后应用`NthPrevious`；旧sibling或latest scan均不可回退。健康ledger尚无R-eligible predecessor时返回
   `ReserveBootstrapRawOnly`，缺artifact/corruption仍fail closed。
9. empty Timeline 或 no-active recipe 是ordinary raw-only；non-empty active且缺current fulfillment是
   `Unfulfilled`，不能降级为reserve bootstrap。
10. Online 在合法 lifecycle boundary先经Cadence reconcile/seal Timeline，再做pure-read readiness；只有
   `Unfulfilled` 才惰性打开 Manager/Store/provider。
11. AgentControl 的 terminal receipt提供 operation replay/settlement，不承诺外部工具 effect exactly-once。
    回执使用既有 `OperationKey`，不再计算或保存独立 `ResultIdentity`；command/runtime/sequence
    匹配与首次 instance/generation 保留。Control writer 为 v4，v2/v3 经源格式验证后投影到同一当前模型；
    读取、重放与 export/backup 保留原 Head/bytes，正常持久 mutation 才写 v4。
    AgentControl 输出为 schemaVersion 2 与 operationKey，已有 Journal 工具结果原文保持。
12. candidate build 与 promotion分离；promotion必须 fresh re-prove head-through fulfillment，并以
    `MaximumNewCalls = 0` 保证不在 promotion阶段启动 recap provider。
13. old `derived/recap` v4-v8 与 rebuild/v1 都是 inert legacy slots；只有显式 manifest-confirmed
    legacy-root archive/delete会触碰它们。
14. Store 为 Cell/Row 分配普通随机 `CellId`/`RowResultId`；SQL 列与成员关系是唯一持久表示。
    同 Slot 的并发正文沿用 first-winner；同 row assignment、成员和前驱返回已存 row。
    Overlay Reuse 保留 base cell 的 ID 与源 Slot，不复制为 candidate cell。
15. Getter 的 `PriorSourceAligned` 比较来源前驱 RowResult，不承诺正文等价。合法 Overlay 可以
    `NotSatisfied` 而仍可读；来源缺失或预算耗尽为 `Incomplete`。诊断计量为独立行/cell/member 数及
    实际正文 UTF-8 bytes，不能把它与旧 canonical 对象字节数直接比较。
16. Store v4 使用既有 `derived/recap-grid/v1/grid.sqlite` 槽位，普通打开旧 schema 返回 unsupported。
    旧 Store 不迁移；全部重构完成后，收敛 pending promotion 与 Recipes 非空 registration、停服备份，再显式 Reset 与统一 LLM 重建。
    Timeline 全部行保留并通过显式离线操作升级为 schema 3；Cadence、Control 规则/回执与 Journal/Prepared 保留。
    本切片不执行真实数据升级、清库或模型重建。
17. Timeline descriptor 外层 wire v2 删除 DescriptorDigest；RowId 仍使用原 body/domain v1。Store fulfillment
    使用 ThroughRowId、cursor wire v2 拒绝旧 v1。普通 Timeline reader 不兼容旧 schema；离线 API 为
    `HistoryTimelineMaintenance.UpgradeSchemaV2(repositoryPath, refId, timelineId)`，作用于明确物理 scope，包括非 active Timeline。

Owning code 与 tests见[架构与代码地图](../architecture-and-code-map.md)。
Control 格式、跨提交窗口与回退边界见[回执简化设计](../../../Galatea/control-receipt-simplification-plan.md)。
