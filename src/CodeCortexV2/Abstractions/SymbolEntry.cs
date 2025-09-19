namespace CodeCortexV2.Abstractions;

/// <summary>
/// Immutable snapshot entry describing a single symbol (namespace or type).
/// All string fields are pre-normalized for fast search without extra allocations.
/// </summary>
/// <param name="SymbolId">Documentation comment id (e.g., "N:Foo.Bar" / "T:Foo.Bar.Baz").</param>
/// <param name="Fqn">Fully-qualified name including the <c>global::</c> prefix (Roslyn display style).</param>
/// <param name="FqnNoGlobal">Fully-qualified name without the <c>global::</c> prefix (external display style).</param>
/// <param name="FqnBase">FQN with generic arity trimmed from each segment (e.g., <c>Ns.List`1.Item</c> → <c>Ns.List.Item</c>).</param>
/// <param name="Simple">Simple name of the symbol (e.g., <c>Baz</c> or namespace segment name).</param>
/// <param name="Kind">Symbol kind (Type/Namespace/...); current index materializes Type and Namespace.</param>
/// <param name="Assembly">Containing assembly for types; standardized as empty string for namespaces (UI normalizes to null).</param>
/// <param name="GenericBase">Simple name without generic arity/arguments (e.g., <c>List`1</c> → <c>List</c>).</param>
/// <param name="ParentNamespace">The parent namespace in external form (without <c>global::</c>), or empty string at the root.</param>
public sealed record SymbolEntry(
    string SymbolId,
    string Fqn,
    string FqnNoGlobal,
    string FqnBase,
    string Simple,
    SymbolKinds Kind,
    string Assembly,
    string GenericBase,
    string ParentNamespace
) {
    /// <summary>
    /// Project this entry to a user-facing <see cref="SearchHit"/> with standardized fields:
    /// - <see cref="SearchHit.Assembly"/> becomes <c>null</c> when empty/whitespace.
    /// - <see cref="SearchHit.Namespace"/> becomes <c>null</c> when empty.
    /// - <see cref="SearchHit.Name"/> uses <see cref="FqnNoGlobal"/>.
    /// </summary>
    /// <param name="MatchFlags">How the query matched this entry.</param>
    /// <param name="score">Secondary sort score (lower is better) within the same &lt;paramref name="MatchFlags"/&gt;.</param>
    /// <returns>A consistent, transport-friendly hit.</returns>
    public SearchHit ToHit(MatchFlags MatchFlags, int score) => new(
        Name: FqnNoGlobal,
        Kind: Kind,
        Namespace: string.IsNullOrEmpty(ParentNamespace) ? null : ParentNamespace,
        Assembly: string.IsNullOrEmpty(Assembly) ? null : Assembly,
        SymbolId: new SymbolId(SymbolId),
        MatchFlags: MatchFlags,
        IsAmbiguous: false,
        Score: score
    );
}
