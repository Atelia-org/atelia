# CodeCortex V2 Indexing API

This document describes the public, text-oriented indexing API that powers search and outline in CodeCortexV2.

## Goals
- Immutable, thread-safe snapshots for lock-free reads
- Clear separation of concerns: Roslyn-bound synchronizer vs. pure data+query index
- Transport-friendly outputs: simple DTOs and strings

## Main Types

### SymbolEntry
An immutable record representing a single symbol (namespace or type) within a snapshot.
- SymbolId: documentation comment id, e.g., `N:Foo.Bar`, `T:Foo.Bar.Baz`
- Fqn: fully-qualified name with `global::` prefix (Roslyn display style)
- FqnNoGlobal: FQN without `global::` (external display style)
- FqnBase: FQN with generic arity trimmed per segment, e.g., `Ns.List`1.Item` → `Ns.List.Item`
- Simple: simple name (last segment), e.g., `Baz`
- Kind: symbol kind; current snapshots materialize Type and Namespace
- Assembly: containing assembly for types; empty string for namespaces (UI normalizes to null)
- GenericBase: simple name stripped of generic arity/arguments, e.g., `List`1` → `List`
- ParentNamespace: parent namespace without `global::`; empty for root

Projection to user-facing results is done via `ToHit(matchKind, score)`, which standardizes Assembly/Namespace nullability and uses `FqnNoGlobal` as Name.

### SymbolsDelta
A minimal change-set applied to a `SymbolIndex`:
- TypeAdds: upserts for type entries
- TypeRemovals: type doc-ids to remove
- NamespaceAdds: upserts for namespaces
- NamespaceRemovals: namespace doc-ids to remove

`SymbolIndex.WithDelta(delta)` applies removals first, then upserts, returning a new immutable snapshot.

### SymbolIndex
A pure, Roslyn-free, immutable index with query logic only.
- Construction: start from `SymbolIndex.Empty` and apply deltas
- Query: `Search(query, limit, offset, kinds)` with layered strategies (Id → Exact → Prefix → Contains → Suffix → Wildcard → GenericBase → Fuzzy)
- Ordering: by match-kind, then score (asc), then name (ordinal)
- Pagination: via `limit` and `offset`; returns total and `nextOffset`

### IndexSynchronizer (IIndexProvider)
Bridges Roslyn workspace events to immutable snapshots.
- `CreateAsync(workspace, ct)`: builds initial snapshot and subscribes to changes
- Debounced batch processing; single-writer semantics
- Maintains document↔type maps to handle partial types and removals
- Produces `SymbolsDelta` and publishes new snapshots atomically through `Current`

## Usage Patterns

- CLI/Service layer obtains an `IIndexProvider` (typically `IndexSynchronizer`) and calls:
  - `provider.Current.Search(...)` for search
  - A separate outline provider resolves the doc-id (types/namespaces) when a unique result is selected

- No component other than the synchronizer should depend on Roslyn in the indexing layer.

## Behavior Notes
- Namespaces have `Assembly == null` in the user-facing projection
- Generic matching: queries like `List<T>` and `List` prefer the same GenericBase bucket
- Suffix search on simple names may mark results as `IsAmbiguous` when multiple matches exist
- Fuzzy search is only used when other strategies produce no result; threshold depends on length

## Versioning & Compatibility
- `BuildAsync(Solution, ...)` has been removed from `SymbolIndex`; use `IndexSynchronizer`
- Additional kinds (methods/properties) can be indexed by extending `SymbolsDelta` and `SymbolEntry`

## Testing Guidance
- Incremental flows: DocumentAdded/Changed/Removed; partial types; namespace cascade removals
- Storms of workspace events: verify debounce and batch application
- Search correctness: layered matching order and stable ordering across pages
