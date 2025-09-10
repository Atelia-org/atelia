using CodeCortexV2.Abstractions;
using CodeCortexV2.Formatting;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.Providers;

public sealed class TypeOutlineProvider : ITypeOutlineProvider {
    private readonly Func<SymbolId, ISymbol?> _resolve;

    public TypeOutlineProvider(Func<SymbolId, ISymbol?> symbolResolver) {
        _resolve = symbolResolver;
    }

    public Task<SymbolOutline> GetTypeOutlineAsync(SymbolId typeId, OutlineOptions? options, CancellationToken ct) {
        options ??= new OutlineOptions();
        var sym = _resolve(typeId) as INamedTypeSymbol;
        if (sym is null) {
            throw new InvalidOperationException($"Type symbol not found: {typeId}");
        }
        var outline = SymbolOutlineBuilder.BuildForType(sym, includeMembers: true, ct);
        return Task.FromResult(outline);
    }
}

