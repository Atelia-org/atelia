using CodeCortexV2.Abstractions;
using CodeCortexV2.Formatting;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.Providers;

public sealed class MemberOutlineProvider : IMemberOutlineProvider {
    private readonly Func<SymbolId, ISymbol?> _resolve;

    public MemberOutlineProvider(Func<SymbolId, ISymbol?> symbolResolver) {
        _resolve = symbolResolver;
    }

    public Task<SymbolOutline> GetMemberOutlineAsync(SymbolId memberId, OutlineOptions? options, CancellationToken ct) {
        options ??= new OutlineOptions();
        var sym = _resolve(memberId);
        if (sym is null) { throw new InvalidOperationException($"Symbol not found: {memberId}"); }
        var outline = SymbolOutlineBuilder.BuildForMember(sym);
        return Task.FromResult(outline);
    }
}

