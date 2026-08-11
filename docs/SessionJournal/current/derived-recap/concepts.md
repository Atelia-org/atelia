# RecapGrid current concepts

状态：WP-08 formal source cutover Complete；raw selected `RefId` Parent lineage仍是唯一历史事实源。

1. HistoryTimeline、Control 与 RecapGrid Store 是独立 companion authorities；都不能替代 raw history。
2. Timeline row 绑定 exact raw range、partition policy、descriptor digest 与 previous-row chain。
3. Control 保存完整 canonical Family/Definition/Recipe graph、active recipe 与 terminal operation receipts；
   mutation 比较 whole `ControlHeadRef`。
4. Full recipe 对目标列全部求值；Overlay bootstrap 对 recomputed columns 求值并对其余列复用 same-row
   base cells；bootstrap 后走 normal full-row evaluation。
5. Manager 以一个 frozen Timeline/Control/Store authority 做 row-major base-to-candidate wavefront；
   missing-only restart 不重发已有 exact cells。
6. Runtime 的 route key 是 exact `(FamilyDigest, RuntimeProtocolId, SemanticModelId?)`；null 也是 exact key，
   没有 wildcard 或 default fallback。
7. Getter 只沿 exact `PreviousViewDigest + PreviousRowId` chain 选择 current active fulfillment；旧
   fulfillment、sibling 或 latest scan 均不可回退。
8. empty Timeline 或 no-active recipe 是 raw-only；non-empty active 且缺 current fulfillment才是
   `Unfulfilled`。
9. Online 在合法 lifecycle boundary 先 reconcile/seal Timeline，再做 pure-read readiness；只有
   `Unfulfilled` 才惰性打开 Manager/Store/provider。
10. AgentControl 的 terminal receipt提供 operation replay/settlement，不承诺外部工具 effect exactly-once。
11. candidate build 与 promotion分离；promotion必须 fresh re-prove head-through fulfillment，并以
    `MaximumNewCalls = 0` 保证不在 promotion阶段启动 recap provider。
12. old `derived/recap` v4-v8 与 rebuild/v1 都是 inert legacy slots；只有显式 manifest-confirmed
    legacy-root archive/delete会触碰它们。

Owning code 与 tests见[架构与代码地图](../architecture-and-code-map.md)。
