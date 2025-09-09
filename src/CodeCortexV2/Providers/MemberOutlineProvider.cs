using CodeCortexV2.Abstractions;
using CodeCortexV2.Formatting;
using Microsoft.CodeAnalysis;
using System.Text;

namespace CodeCortexV2.Providers;

public sealed class MemberOutlineProvider : IMemberOutlineProvider {
    private readonly Func<SymbolId, ISymbol?> _resolve;

    public MemberOutlineProvider(Func<SymbolId, ISymbol?> symbolResolver) {
        _resolve = symbolResolver;
    }

    public Task<MemberOutline> GetMemberOutlineAsync(SymbolId memberId, OutlineOptions? options, CancellationToken ct) {
        options ??= new OutlineOptions();
        var sym = _resolve(memberId);
        if (sym is null) {
            throw new InvalidOperationException($"Symbol not found: {memberId}");
        }

        var kind = sym.Kind.ToString();
        var name = sym.Name;
        var signature = SignatureFormatter.RenderSignature(sym);
        var summary = XmlDocFormatter.BuildDetailedMemberMarkdown(sym);
        var loc = ToLocation(sym);

        var dto = new MemberOutline(kind, name, signature, summary, loc);
        return Task.FromResult(dto);
    }

    private static string FormatSignature(ISymbol s) {
        return SignatureFormatter.RenderSignature(s);
    }

    private static string TypeKindKeyword(TypeKind k) => k switch {
        TypeKind.Class => "class",
        TypeKind.Struct => "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => k.ToString().ToLowerInvariant()
    };

    private static bool IsPublicApiMember(ISymbol s) =>
        s.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
        && !s.IsImplicitlyDeclared;

    private static string RenderSummaryMarkdown(ISymbol s) {
        var lines = XmlDocFormatter.GetSummaryLines(s);
        if (lines.Count == 0) {
            return string.Empty;
        }

        var sb = new StringBuilder();
        MarkdownRenderer.RenderLinesWithStructure(sb, lines, indent: string.Empty, bulletizePlain: false, startIndex: 0, insertBlankBeforeTable: true);
        return sb.ToString().TrimEnd();
    }

    private static CodeCortexV2.Abstractions.Location ToLocation(ISymbol s) {
        var roslynLoc = s.Locations.FirstOrDefault(l => l.IsInSource) ?? s.Locations.FirstOrDefault();
        if (roslynLoc is null || !roslynLoc.IsInSource) {
            return new CodeCortexV2.Abstractions.Location(string.Empty, 0, 0);
        }
        var span = roslynLoc.SourceSpan;
        return new CodeCortexV2.Abstractions.Location(roslynLoc.SourceTree!.FilePath, span.Start, span.Length);
    }
}

