using System;
namespace CodeCortexV2.Abstractions;

/// <summary>
/// Immutable change-set for <see cref="SymbolIndex"/> snapshots.
/// Produced by the synchronizer from Roslyn changes and applied via <c>SymbolIndex.WithDelta</c>.
/// </summary>
/// <param name="TypeAdds">New or updated type entries to upsert.</param>
/// <param name="TypeRemovals">Types to remove, identified by DocId + Assembly (assembly is REQUIRED).</param>
/// <param name="NamespaceAdds">DEPRECATED – Implementations MAY ignore. Historically used to materialize namespace chains.</param>
/// <param name="NamespaceRemovals">DEPRECATED – Implementations MAY ignore. Historically used for cascading empty-namespace removals.</param>
/// <remarks>
/// Updated contract (leaf-only delta preferred):
/// - Producers SHOULD populate only <see cref="TypeAdds"/> and <see cref="TypeRemovals"/>. Namespace-related fields are deprecated and SHOULD be empty.
/// - Ordering invariant (CRITICAL for correct processing):
///   * <see cref="TypeAdds"/> MUST be sorted by ascending <see cref="SymbolEntry.DocCommentId"/> length (outer types before nested types).
///   * <see cref="TypeRemovals"/> MUST be sorted by descending <see cref="TypeKey.DocCommentId"/> length (nested types before outer types).
///   * Doc ids MUST begin with <c>"T:"</c> and Assembly MUST be provided.
///   * Rationale: This ordering ensures parent types are always processed before their nested children during adds,
///     preventing the need for placeholder nodes and maintaining index consistency. Violations will trigger fail-fast exceptions.
/// - Consumers (e.g., <c>ISymbolIndex.WithDelta</c>) SHOULD handle, internally and locally, namespace chain materialization
///   and cascading empty-namespace removal during application of the leaf delta.
/// - Idempotency & self-consistency:
///   - Within the same delta, avoid contradictory operations on the same id (e.g., add and remove simultaneously).
///   - Applying this delta repeatedly yields the same resulting index state.
/// - Rename representation:
///   - Represented as <c>remove(oldId)</c> + <c>add(newEntry)</c>.
/// - Debounce/coalescing resilience:
///   - When multiple workspace events are coalesced, the resulting delta remains self-consistent.
/// - Validation: Consumers are encouraged to treat ordering or doc-id violations as fatal (assert or throw) rather than silently
///   repairing inconsistent deltas.
///
/// Back-compat: Implementations MAY still honor NamespaceAdds/NamespaceRemovals if provided, but new code SHOULD NOT rely on them.
/// </remarks>
public sealed record SymbolsDelta(
    IReadOnlyList<SymbolEntry> TypeAdds,
    IReadOnlyList<TypeKey> TypeRemovals
);
