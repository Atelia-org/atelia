using CodeCortexV2.Abstractions;
using CodeCortexV2.Formatting;
using Microsoft.CodeAnalysis;
using System.Text;

namespace CodeCortexV2.Providers;

public sealed class MemberOutlineProvider : IMemberOutlineProvider
{
    private readonly Func<SymbolId, ISymbol?> _resolve;

    public MemberOutlineProvider(Func<SymbolId, ISymbol?> symbolResolver)
    {
        _resolve = symbolResolver;
    }

    public Task<MemberOutline> GetMemberOutlineAsync(SymbolId memberId, OutlineOptions? options, CancellationToken ct)
    {
        options ??= new OutlineOptions();
        var sym = _resolve(memberId);
        if (sym is null)
        {
            throw new InvalidOperationException($"Symbol not found: {memberId}");
        }

        var kind = sym.Kind.ToString();
        var name = sym.Name;
        var signature = FormatSignature(sym);
        var summary = RenderSummaryMarkdown(sym);
        var loc = ToLocation(sym);

        var dto = new MemberOutline(kind, name, signature, summary, loc);
        return Task.FromResult(dto);
    }

    private static string FormatSignature(ISymbol s)
    {
        if (s is IPropertySymbol ps)
        {
            var acc = new StringBuilder();
            acc.Append("{ ");
            if (ps.GetMethod != null && IsPublicApiMember(ps.GetMethod)) acc.Append("get; ");
            if (ps.SetMethod != null && IsPublicApiMember(ps.SetMethod)) acc.Append("set; ");
            acc.Append("}");
            var typeStr = ps.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var namePart = ps.IsIndexer
                ? $"this[{string.Join(", ", ps.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + p.Name))}]"
                : ps.Name;
            return $"{typeStr} {namePart} {acc}";
        }
        if (s is INamedTypeSymbol nt)
        {
            if (nt.TypeKind == TypeKind.Delegate)
            {
                var invoke = nt.DelegateInvokeMethod;
                var ret = invoke?.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "void";
                var nameDisplay = nt.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                var parms = invoke == null ? string.Empty : string.Join(", ", invoke.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + p.Name));
                return $"delegate {ret} {nameDisplay}({parms})";
            }
            var display = nt.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            return $"{TypeKindKeyword(nt.TypeKind)} {display}";
        }
        return s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    private static string TypeKindKeyword(TypeKind k) => k switch
    {
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

    private static string RenderSummaryMarkdown(ISymbol s)
    {
        var lines = XmlDocFormatter.GetSummaryLines(s);
        if (lines.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        MarkdownRenderer.RenderLinesWithStructure(sb, lines, indent: string.Empty, bulletizePlain: false, startIndex: 0, insertBlankBeforeTable: true);
        return sb.ToString().TrimEnd();
    }

    private static CodeCortexV2.Abstractions.Location ToLocation(ISymbol s)
    {
        var roslynLoc = s.Locations.FirstOrDefault(l => l.IsInSource) ?? s.Locations.FirstOrDefault();
        if (roslynLoc is null || !roslynLoc.IsInSource)
        {
            return new CodeCortexV2.Abstractions.Location(string.Empty, 0, 0);
        }
        var span = roslynLoc.SourceSpan;
        return new CodeCortexV2.Abstractions.Location(roslynLoc.SourceTree!.FilePath, span.Start, span.Length);
    }
}

