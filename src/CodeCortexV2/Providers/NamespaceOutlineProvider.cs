using CodeCortexV2.Abstractions;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.Providers;

/// <summary>
/// Namespace outline provider returning unified SymbolOutline.
/// - Includes: direct child namespaces and public/protected types (no deep recursion for types).
/// </summary>
public sealed class NamespaceOutlineProvider : INamespaceOutlineProvider {
    private readonly Func<SymbolId, ISymbol?> _resolve;

    public NamespaceOutlineProvider(Func<SymbolId, ISymbol?> symbolResolver) {
        _resolve = symbolResolver;
    }

    public Task<SymbolOutline> GetNamespaceOutlineAsync(SymbolId namespaceId, OutlineOptions? options, CancellationToken ct) {
        options ??= new OutlineOptions();
        var sym = _resolve(namespaceId) as INamespaceSymbol;
        if (sym is null) { throw new InvalidOperationException($"Namespace symbol not found: {namespaceId}"); }
        var outline = SymbolOutlineBuilder.BuildForNamespace(sym, includeChildren: true, ct);
        // Optional truncation
        if (options.MaxItems is int max && max > 0 && outline.Members.Count > max) {
            outline = outline with { Members = outline.Members.Take(max).ToList() };
        }
        return Task.FromResult(outline);
    }
}

