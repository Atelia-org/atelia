# RecapGrid current concepts

状态：WP-08 formal source cutover Complete；raw selected `RefId` Parent lineage仍是唯一历史事实源。

1. HistoryTimeline、Cadence、Control 与 RecapGrid Store 是独立 companion authorities；都不能替代 raw history。
2. Timeline row 绑定 exact raw range、partition policy、descriptor digest 与 previous-row chain。
3. Control 保存完整 canonical Family/Definition/Recipe graph、active recipe 与 terminal operation receipts；
   mutation 比较 whole `ControlHeadRef`。
4. Full recipe 对目标列全部求值；Overlay bootstrap 对 recomputed columns 求值并对其余列复用 same-row
   base cells；bootstrap 后走 normal full-row evaluation。
5. Manager 以一个 frozen Timeline/Control/Store authority 做 row-major base-to-candidate wavefront；
   missing-only restart 不重发已有 exact cells。
   前驱输入仅传 `PreviousView` 与 `PreviousCells`；Runtime 校验成员顺序与身份后，调用
   `PriorInputProjectionDigest.FromCells` 对照 `Spec.PriorInput` 的独立 expected digest。
   不再传递完整 projection 对象；digest 的旧 body/domain、缓存键和持久格式保持。
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
    匹配与首次 instance/generation 保留。Control writer 为 v3，v2 经源格式验证后投影到同一当前模型；
    读取、重放与 export/backup 保留原 Head/bytes，正常持久 mutation 才写 v3。
    AgentControl 输出为 schemaVersion 2 与 operationKey，已有 Journal 工具结果原文保持。
12. candidate build 与 promotion分离；promotion必须 fresh re-prove head-through fulfillment，并以
    `MaximumNewCalls = 0` 保证不在 promotion阶段启动 recap provider。
13. old `derived/recap` v4-v8 与 rebuild/v1 都是 inert legacy slots；只有显式 manifest-confirmed
    legacy-root archive/delete会触碰它们。

Owning code 与 tests见[架构与代码地图](../architecture-and-code-map.md)。
Control 格式、跨提交窗口与回退边界见[回执简化设计](../../../Galatea/control-receipt-simplification-plan.md)。
