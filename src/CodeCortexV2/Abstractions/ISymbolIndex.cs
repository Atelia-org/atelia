
namespace CodeCortexV2.Abstractions;
public interface ISymbolIndex {
    SearchResults Search(string query, int limit, int offset, SymbolKinds kinds);
    /// <summary>
    /// Apply a <see cref="SymbolsDelta"/> to produce a new immutable snapshot.
    /// Updated contract (leaf-only deltas; namespace operations deprecated):
    /// - Producers SHOULD emit leaf-only deltas: <c>TypeAdds</c> and <c>TypeRemovals</c>. Namespace-related fields in
    ///   <see cref="SymbolsDelta"/> are DEPRECATED and MAY be ignored by implementations.
    /// - Implementations of <c>WithDelta</c> SHOULD handle, internally and locally (impacted subtrees only):
    ///   - Namespace chain materialization for added types.
    ///   - Cascading deletion of empty namespaces after type removals.
    /// - Idempotency: applying the same delta multiple times yields the same resulting index state.
    /// - Locality: time/memory complexity should be proportional to the size of the delta, not the whole index.
    /// - Defensive checks: implementations MAY include lightweight validations with diagnostics, but MUST NOT rely on
    ///   full-index traversal in production.
    ///
    /// Producer notes (e.g., <c>IndexSynchronizer</c>):
    /// - Deduplicate/conflict-resolve within a batch (e.g., avoid simultaneous add+remove for the same id); a rename is
    ///   represented as remove(oldId) + add(newEntry).
    /// - NamespaceAdds/NamespaceRemovals are deprecated; producers SHOULD leave them empty/null.
    /// </summary>
    ISymbolIndex WithDelta(SymbolsDelta delta);
}
