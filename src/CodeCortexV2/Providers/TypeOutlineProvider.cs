using CodeCortexV2.Abstractions;
using CodeCortexV2.Formatting;
using Microsoft.CodeAnalysis;
using System.Text;

namespace CodeCortexV2.Providers;

public sealed class TypeOutlineProvider : ITypeOutlineProvider
{
    private readonly Func<SymbolId, ISymbol?> _resolve;

    public TypeOutlineProvider(Func<SymbolId, ISymbol?> symbolResolver)
    {
        _resolve = symbolResolver;
    }

    public Task<TypeOutline> GetTypeOutlineAsync(SymbolId typeId, OutlineOptions? options, CancellationToken ct)
    {
        options ??= new OutlineOptions();
        var sym = _resolve(typeId) as INamedTypeSymbol;
        if (sym is null)
        {
            throw new InvalidOperationException($"Type symbol not found: {typeId}");
        }

        var name = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var summary = RenderSummaryMarkdown(sym);
        var members = new List<MemberOutline>();

        foreach (var m in sym.GetMembers().Where(IsPublicApiMember).OrderBy(m => m.Name))
        {
            if (m is IMethodSymbol ms && (ms.MethodKind == MethodKind.PropertyGet || ms.MethodKind == MethodKind.PropertySet || ms.MethodKind == MethodKind.EventAdd || ms.MethodKind == MethodKind.EventRemove))
            {
                continue;
            }
            var mo = BuildMemberOutline(m);
            members.Add(mo);
        }

        var dto = new TypeOutline(name, summary, members);
        return Task.FromResult(dto);
    }

    private static MemberOutline BuildMemberOutline(ISymbol m)
    {
        var kind = m.Kind.ToString();
        var name = m.Name;
        var signature = FormatSignature(m);
        var summary = RenderSummaryMarkdown(m);
        var loc = ToLocation(m);
        return new MemberOutline(kind, name, signature, summary, loc);
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

