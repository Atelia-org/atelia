# CodeCortex V2 Indexing API

This document describes the public, text-oriented indexing API that powers search and outline in CodeCortexV2.

## Goals
- Immutable, thread-safe snapshots for lock-free reads
- Clear separation of concerns: Roslyn-bound synchronizer vs. pure data+query index
- Transport-friendly outputs: simple DTOs and strings

## Main Types

### SymbolEntry
An immutable record representing a single symbol (namespace or type) within a snapshot. Internally the index treats the
documentation comment id as the sole canonical key; every other field is derived from it for transport or UI purposes.
- **DocCommentId** (`SymbolId`): documentation comment id, e.g., `N:Foo.Bar`, `T:Foo.Bar.Baz`
- **FullDisplayName**: fully-qualified display string without the `global::` prefix (external/UI style)
- **DisplayName**: final segment in display form (preserving generic arity, e.g., ``List`1``)
- **Kind**: symbol kind; current snapshots materialize Type and Namespace
- **Assembly**: containing assembly for types; empty string for namespaces (UI normalizes to null)
- **ParentNamespace** (legacy): namespace string without `global::`; pending replacement by `NamespaceSegments` and
  `TypeSegments` arrays derived from the doc-id

Projection to user-facing results is done via `ToHit(matchKind, score)`, which standardizes Assembly/Namespace nullability
and uses `FullDisplayName` as the search/display name. Upcoming refactors will replace the legacy namespace string with
DocCommentId-derived segment arrays to keep structural logic fully DocId-based.

### SymbolsDelta
A minimal, leaf-oriented change-set applied to a `SymbolIndex`:
- **TypeAdds** – upserts for concrete type entries. Producers MUST supply DocCommentIds starting with `"T:"`, include the owning assembly, and order the collection by `DocCommentId.Length` ascending so outer types arrive before nested types.
- **TypeRemovals** – removals for concrete type keys (`DocCommentId + Assembly`). The list MUST be ordered by `DocCommentId.Length` descending so inner-most types are pruned before their parents.

Namespace-related fields are deprecated and SHOULD be omitted (empty collections). The helper `SymbolsDeltaContract.Normalize` enforces the ordering/validation contract when producers cannot emit canonical deltas directly.

`SymbolIndex.WithDelta(delta)` applies removals first, then upserts, materializing any missing namespace chain and cascading empty namespace cleanup internally. The operation is idempotent: feeding the same delta repeatedly yields the same snapshot.

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
