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
/// Internal logic treats <see cref="DocCommentId"/> as the canonical identifier; ancillary fields are derived solely
/// for presentation and query interoperability. Display-oriented values remain in the record for UI/transport layers,
/// while future refactors will transition structural relationships to DocCommentId-derived segment arrays.
/// </summary>
/// <param name="DocCommentId">Documentation comment id (e.g., "N:Foo.Bar" / "T:Foo.Bar.Baz").</param>
/// <param name="FullDisplayName">Fully-qualified name without the <c>global::</c> prefix (external display style).</param>
/// <param name="DisplayName">FQN 的最后一段，保持声明形态，包含泛型元数（如 `List`1`）。</param>
/// <param name="Kind">Symbol kind (Type/Namespace/...); current index materializes Type and Namespace.</param>
/// <param name="Assembly">Containing assembly for types; standardized as empty string for namespaces (UI normalizes to null).</param>
/// <param name="NamespaceSegments">Namespace chain derived from <paramref name="DocCommentId"/> (empty for root).</param>
/// <param name="TypeSegments">Type/nested-type chain derived from <paramref name="DocCommentId"/> (empty for namespaces).</param>
public sealed record SymbolEntry(
    string DocCommentId,
    string Assembly,
    SymbolKinds Kind,
    string[] NamespaceSegments,
    string[] TypeSegments,
    string FullDisplayName,
    string DisplayName
) {
    /// <summary>
    /// Project this entry to a user-facing <see cref="SearchHit"/> with standardized fields:
    /// - <see cref="SearchHit.Assembly"/> becomes <c>null</c> when empty/whitespace.
    /// - <see cref="SearchHit.Namespace"/> becomes <c>null</c> when empty.
    /// - <see cref="SearchHit.Name"/> uses <see cref="FullDisplayName"/>.
    /// </summary>
    /// <param name="MatchFlags">How the query matched this entry.</param>
    /// <param name="score">Secondary sort score (lower is better) within the same &lt;paramref name="MatchFlags"/&gt;.</param>
    /// <returns>A consistent, transport-friendly hit.</returns>
    public SearchHit ToHit(MatchFlags MatchFlags, int score) => new(
        Name: FullDisplayName,
        Kind: Kind,
    Namespace: NamespaceSegments is { Length: > 0 } nsSegments ? string.Join('.', nsSegments) : null,
        Assembly: string.IsNullOrEmpty(Assembly) ? null : Assembly,
        SymbolId: new SymbolId(DocCommentId),
        MatchFlags: MatchFlags,
        IsAmbiguous: false,
        Score: score
    );
}
