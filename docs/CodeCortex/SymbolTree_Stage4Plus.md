# SymbolTree Stage4+ Backlog

_Date: 2025-10-02_

This note captures the medium-term ideas that were factored out of the single-node refactor once the Phase 3 cleanup completed. The items below are **not** part of the current sprint; they can be revisited when we iterate on scalability or resiliency again.

## Context
- Single-node semantics are now the only runtime path (see `SymbolTreeSingleNode.md`).
- `SymbolsDeltaContract.Normalize` enforces the producer contract; `SymbolTree.WithDelta.md` is the authoritative behavior spec.
- All new deltas will eventually converge old snapshots into the single-node topology, so the backlog can assume that invariant.

## Backlog Clusters

### P1 – Localized performance / determinism improvements
- **Parent child slot index**: cache `(parentId, name, kind) -> childIds` for touched parents inside a delta to avoid repeated sibling scans.
- **DocId lookup map**: maintain a temporary map of `DocCommentId -> nodeIds` while applying a delta to accelerate add/remove matching.
- **Deterministic alias ordering**: keep alias buckets naturally sorted (already done) and extend determinism to any future auxiliary indexes.
- **NodeBuilder lazy overlays**: switch `List<Node>` cloning to a copy-on-write layout (immutable array + sparse override dictionary).

### P2 – Long running maintenance
- **Tombstone compaction**: define thresholds (percentage + absolute count) to trigger subtree rebuild or freelist trimming during low-traffic periods.
- **Large delta fallback**: when delta size crosses a safety threshold, fall back to a targeted rebuild path or request a full rebuild from the synchronizer.
- **Alias bucket rehydration**: batch refresh and dedupe alias buckets after compaction to keep freelist reuse healthy.

### P3 – Diagnostics and tooling
- **Freelist telemetry promotion**: elevate `SymbolTree.SingleNode.Freelist` into structured metrics once thresholds are calibrated.
- **Delta contract guardrails**: optionally move `SymbolsDeltaContract.Normalize` to the producer pipeline so violations surface earlier.
- **Long-run fuzzing**: extend the random-delta vs `FromEntries` parity harness to run in CI/nightly with higher coverage.

## When to Revisit
- Observed perf regressions related to large solutions or unusual delta bursts.
- Increased freelist churn or alias bucket growth flagged by load testing.
- New product requirements (e.g., cross-solution indexing) that mandate stronger guarantees.

## Tracking
- Link this note from `SymbolTreeSingleNode.md` (Closure checklist).
- Update the backlog whenever a Stage4+ task is promoted into an active milestone.
