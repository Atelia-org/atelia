namespace CodeCortexV2.Abstractions;

/// <summary>
/// Track by precise type key: DocCommentId + Assembly, to support precise removals
/// </summary>
/// <param name="DocCommentId">Type doc-id (e.g., T:Ns.Type or T:Ns.Outer+Inner`1)</param>
/// <param name="Assembly">Containing assembly name (REQUIRED)</param>
public readonly record struct TypeKey(
    string DocCommentId,
    string Assembly
);

/// <summary>
/// Immutable snapshot entry describing a single symbol (namespace or type).
/// All string fields are pre-normalized for fast search without extra allocations.
/// </summary>
/// <param name="DocCommentId">Documentation comment id (e.g., "N:Foo.Bar" / "T:Foo.Bar.Baz").</param>
/// <param name="FqnNoGlobal">Fully-qualified name without the <c>global::</c> prefix (external display style).</param>
/// <param name="FqnLeaf">FQN 的最后一段，保持声明形态，包含泛型元数（如 `List`1`）。</param>
/// <param name="Kind">Symbol kind (Type/Namespace/...); current index materializes Type and Namespace.</param>
/// <param name="Assembly">Containing assembly for types; standardized as empty string for namespaces (UI normalizes to null).</param>
/// <param name="ParentNamespaceNoGlobal">The parent namespace in external form (without <c>global::</c>), or empty string at the root.</param>
public sealed record SymbolEntry(
    string DocCommentId,
    string Assembly,
    SymbolKinds Kind,
    string ParentNamespaceNoGlobal,
    string FqnNoGlobal,
    string FqnLeaf
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
        Namespace: string.IsNullOrEmpty(ParentNamespaceNoGlobal) ? null : ParentNamespaceNoGlobal,
        Assembly: string.IsNullOrEmpty(Assembly) ? null : Assembly,
        SymbolId: new SymbolId(DocCommentId),
        MatchFlags: MatchFlags,
        IsAmbiguous: false,
        Score: score
    );
}
