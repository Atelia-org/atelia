# DerivedRecap current concepts

状态：v8 direct-cut current shape；raw selected `RefId` Parent lineage仍是唯一事实源。

1. Recap是可删除、可重建的derived artifact，不回写raw event history。
2. 一个epoch对应一个shared history slab、一个frozen ordered projection和一个complete roster。
3. `Previous`只允许Empty或structured prior pack；没有flattened PriorContext、per-block old payload或
   live previous publication读取。
4. `NoBuild`表示整轮零调用；Build后每个member恰好一次，结果Updated或KeepUnchanged。
5. final由manifest hash、ordinal、canonical block definition绑定；Keep相同正文也推进execution identity。
6. publication只有complete roster；统一以root AdmissionBoundary表示coverage。
7. normal online只用bounded raw authority；FullRebuildRequired与MoreWorkPending语义不同。
8. explicit rebuild spool是可删除execution aid，不保存event body、recap、prompt或epoch decision。
9. frozen recovery先于active config；新的topology不匹配要求full rebuild。
10. 当前kernel串行。family/prefix-cache aware parallel dispatch属于后续R4。

Owning code见[架构与代码地图](../architecture-and-code-map.md)和Store/Planner README。
