namespace CodeCortexV2.Abstractions;

/// <summary>
/// Immutable change-set for <see cref="SymbolIndex"/> snapshots.
/// Produced by the synchronizer from Roslyn changes and applied via <c>SymbolIndex.WithDelta</c>.
/// </summary>
/// <param name="TypeAdds">New or updated type entries to upsert.</param>
/// <param name="TypeRemovals">Type doc-ids (e.g., <c>T:Ns.Type</c>) to remove.</param>
/// <param name="NamespaceAdds">New namespaces to upsert (debounced and normalized).</param>
/// <param name="NamespaceRemovals">Namespace doc-ids (e.g., <c>N:Ns.Sub</c>) to remove.</param>
public sealed record SymbolsDelta(
    IReadOnlyList<SymbolEntry> TypeAdds,
    IReadOnlyList<string> TypeRemovals,
    IReadOnlyList<SymbolEntry> NamespaceAdds,
    IReadOnlyList<string> NamespaceRemovals
);
