# SymbolTree "Single-Node" Refactor – Phase 3 Cleanup

_Date: 2025-10-01_

## Implementation Highlights
- Single-node semantics are now the only code path: the legacy placeholder implementation and its environment toggle have been removed.
- Builder no longer tracks the reuse pool required by the old algorithm; freelist reuse is handled uniformly and stats are still reported via `DeltaStats`.
- Removal flow always triggers `TidyTypeSiblings`/`CollapseEmptyTypeAncestors`, ensuring leftover structural shells from historical snapshots are reclaimed immediately after a delta.
- New regression test `Prototype_ShouldKeepAssembliesSeparatedForSameDocId` verifies that multiple assemblies sharing the same DocCommentId remain isolated and that targeted removals do not affect sibling assemblies.
- Alias bucket maintenance is now deterministic: inserts preserve ascending `NodeId` ordering, and the new regression test `AliasBuckets_ShouldRemainSortedByNodeId` guards against future regressions.
- Added regression `SingleNode_LegacySnapshot_ConvergesAfterDelta` to模拟历史快照（含结构占位节点）并确认一次 delta 后即可收敛。
- Added stress regression `SingleNode_ExtremeNestingAndAssemblies_RemainsConsistent` 覆盖多层嵌套 + 泛型 + 多程序集组合场景。
- Added regression `SingleNode_LegacySnapshot_ConvergesAfterDelta` to simulate历史快照（含结构占位节点）并确认一次 delta 后即可收敛为纯单节点拓扑。

## Documentation & Instrumentation Update (2025-10-01)
- Documented the leaf-only `SymbolsDelta` contract and ordering requirements in `docs/CodeCortex/Indexing-API.md` and refreshed `docs/CodeCortex/SymbolTree.WithDelta.md` to reference `SymbolsDeltaContract.Normalize`.
- Added `DebugUtil` instrumentation (`SymbolTree.SingleNode.Freelist`) to report per-delta reuse/freed counts alongside the existing `WithDelta` summary, enabling quick checks on freelist health during load tests.

## Test Coverage
- Suite：`SymbolTreeSingleNodePrototypeTests`
  - 验证单节点/legacy 快照在代表性的嵌套、泛型、跨程序集场景下维持查询等价。
  - 回归覆盖历史占位快照收敛、极端嵌套/多程序集组合、别名排序确定性。
- Regression command（2025-10-02）: `dotnet test tests/CodeCortex.Tests/CodeCortex.Tests.csproj --filter FullyQualifiedName~SymbolTree` → 42 通过 / 0 失败，用时 2.1s。
- Regression command: `dotnet test tests/CodeCortex.Tests/CodeCortex.Tests.csproj` (167 tests，全部通过，包含别名排序与单节点回归)。

## Status Snapshot（2025-10-01）
- Phase 3 清理完成：legacy 占位节点路径与环境开关已移除，`WithDelta`/`Builder` 全面运行单节点拓扑。
- Phase 4 先行项落地：别名桶按 `NodeId` 升序保持确定性，并配合 `SymbolTree.SingleNode.Freelist` 日志监控节点复用状况。

## In-Flight Work
1. 资料收尾：整理本页与 `SymbolTree.WithDelta.md` 的最终版，保留必要的调试/运维说明，其余历史阶段计划归档。

## Next Steps
- 验证并同步 `SymbolTree.WithDelta.md` / `Indexing-API.md` 文本，确保最终指导与实现一致。
- 归档资料：如需补充说明，追加至 `SymbolTree_Stage4Plus.md`，保持当前文档聚焦现状。

## Notes & Monitoring
- With the fallback removed, any snapshot that flows through `WithDelta` converges to pure single-node form after one update window.
- Monitor both `SymbolTree.WithDelta` and `SymbolTree.SingleNode.Freelist` channels on representative solutions to tune thresholds for future metric promotion.
- Socialize the updated delta contract with synchronizer producers and decide whether `SymbolsDeltaContract.Normalize` should be enforced centrally or opt-in per pipeline.

## Closure Checklist（草案）
- [x] 验证：`FromEntries` + `WithDelta` 均只产生单节点（DocId+Assembly 唯一），覆盖历史占位快照（`SingleNode_LegacySnapshot_ConvergesAfterDelta`）。
- [x] 测试：`SymbolTree` 相关测试集全部通过，并附带 2025-10-02 运行记录。
- [x] 文档：`SymbolTreeSingleNode.md`、`SymbolTree.WithDelta.md` 与 `Indexing-API.md` 已同步到最终版本。
- [x] 归档：Stage4+ 长期优化思路已单独记录或删减（见 `SymbolTree_Stage4Plus.md`）。

## Documentation Map
- `docs/CodeCortex/SymbolTreeSingleNode.md`（本文）：单节点重构的状态快照与执行摘要。
- `docs/CodeCortex/SymbolTree.WithDelta.md`：`WithDelta` 行为规范、契约细节与实现提示。
- `docs/CodeCortex/SymbolTree_Stage4Plus.md`：Stage4+ 长期优化想法与触发条件。
