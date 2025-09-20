namespace CodeCortexV2.Abstractions;

/// <summary>
/// Immutable change-set for <see cref="SymbolIndex"/> snapshots.
/// Produced by the synchronizer from Roslyn changes and applied via <c>SymbolIndex.WithDelta</c>.
/// </summary>
/// <param name="TypeAdds">New or updated type entries to upsert.</param>
/// <param name="TypeRemovals">Type doc-ids (e.g., <c>T:Ns.Type</c>) to remove.</param>
/// <param name="NamespaceAdds">New namespaces to upsert (debounced and normalized).</param>
/// <param name="NamespaceRemovals">Namespace doc-ids (e.g., <c>N:Ns.Sub</c>) to remove.</param>
/// <remarks>
/// Contract and invariants (expected to be ensured by the producer, e.g., IndexSynchronizer):
/// - Normalization & closure completeness:
///   - <paramref name="NamespaceAdds"/> MUST include any missing ancestors required for the added entries (i.e., the full namespace chain).
///   - <paramref name="NamespaceRemovals"/> MUST include namespaces that become empty due to this batch, including conservative cascading removals
///     per the chosen policy (e.g., considering other documents' remaining types).
/// - Idempotency & self-consistency:
///   - Within the same delta, do not include contradictory operations on the same id (e.g., add and remove simultaneously).
///   - Applying this delta repeatedly yields the same resulting index state.
/// - Rename representation:
///   - A rename is represented as <c>remove(oldId)</c> + <c>add(newEntry)</c>. Namespace changes due to rename are reflected in the corresponding
///     namespace adds/removals per the closure rules.
/// - Debounce/coalescing resilience:
///   - When multiple workspace events are coalesced, the resulting delta remains self-consistent and closed.
///
/// Consumers (e.g., ISymbolIndex.WithDelta) should treat the delta as authoritative and avoid re-inference via full-index scans.
/// Optional defensive validations may be employed in debug/testing configurations with diagnostic logging.
/// </remarks>
public sealed record SymbolsDelta(
    IReadOnlyList<SymbolEntry> TypeAdds,
    IReadOnlyList<string> TypeRemovals,
    IReadOnlyList<SymbolEntry> NamespaceAdds,
    IReadOnlyList<string> NamespaceRemovals
);
