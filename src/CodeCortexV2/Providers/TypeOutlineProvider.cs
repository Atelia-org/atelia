using CodeCortexV2.Abstractions;
using CodeCortexV2.Formatting;
using Microsoft.CodeAnalysis;
using System.Text;

namespace CodeCortexV2.Providers;

public sealed class TypeOutlineProvider : ITypeOutlineProvider {
    private readonly Func<SymbolId, ISymbol?> _resolve;

    public TypeOutlineProvider(Func<SymbolId, ISymbol?> symbolResolver) {
        _resolve = symbolResolver;
    }

    public Task<TypeOutline> GetTypeOutlineAsync(SymbolId typeId, OutlineOptions? options, CancellationToken ct) {
        options ??= new OutlineOptions();
        var sym = _resolve(typeId) as INamedTypeSymbol;
        if (sym is null) {
            throw new InvalidOperationException($"Type symbol not found: {typeId}");
        }

        var name = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var summary = string.Empty; // metadata will be embedded as a SectionBlock under the synthetic type member
        var members = new List<MemberOutline>();

        // Insert synthetic 'type as member' for unified markdown layout
        var typeBlocks = XmlDocFormatter.BuildMemberBlocks(sym);
        // Prepend type metadata as a SectionBlock for a fully uniform block tree
        var typeMeta = BuildTypeMetadataSection(sym);
        typeBlocks.Insert(0, typeMeta);
        var typeSummaryMd = MarkdownLayout.RenderBlocksToMarkdown(typeBlocks, string.Empty, baseHeadingLevel: 3, maxAtxLevel: 4);
        var typeMember = new MemberOutline(
            Kind: TypeKindKeyword(sym.TypeKind),
            Name: name,
            Signature: FormatSignature(sym),
            Summary: typeSummaryMd,
            DeclaredIn: ToLocation(sym)
        );
        members.Add(typeMember);

        foreach (var m in sym.GetMembers().Where(IsPublicApiMember).OrderBy(m => m.Name)) {
            if (m is IMethodSymbol ms && (ms.MethodKind == MethodKind.PropertyGet || ms.MethodKind == MethodKind.PropertySet || ms.MethodKind == MethodKind.EventAdd || ms.MethodKind == MethodKind.EventRemove)) {
                continue;
            }
            var mo = BuildMemberOutline(m);
            members.Add(mo);
        }

        var dto = new TypeOutline(name, summary, members);
        return Task.FromResult(dto);
    }

    private static MemberOutline BuildMemberOutline(ISymbol m) {
        var kind = m.Kind.ToString();
        var name = m.Name;
        var signature = SignatureFormatter.RenderSignature(m);
        var summary = XmlDocFormatter.BuildDetailedMemberMarkdown(m);
        var loc = ToLocation(m);
        return new MemberOutline(kind, name, signature, summary, loc);
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

    private static string BuildTypeHeaderAndSummary(INamedTypeSymbol sym) {
        var sb = new StringBuilder();
        var fqn = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        var docId = Microsoft.CodeAnalysis.DocumentationCommentId.CreateDeclarationId(sym) ?? fqn;
        var asm = sym.ContainingAssembly?.Name;
        var file = sym.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;
        if (!string.IsNullOrEmpty(fqn)) {
            sb.AppendLine($"FQN: {fqn}");
        }

        if (!string.IsNullOrEmpty(docId)) {
            sb.AppendLine($"DocId: {docId}");
        }

        if (!string.IsNullOrEmpty(asm)) {
            sb.AppendLine($"Assembly: {asm}");
        }

        if (!string.IsNullOrEmpty(file)) {
            sb.AppendLine($"File: {System.IO.Path.GetFileName(file)}");
        }
        // Keep metadata header only; type XML doc will appear under the synthetic 'type member'
        return sb.ToString().TrimEnd();
    }

    private static SectionBlock BuildTypeMetadataSection(INamedTypeSymbol sym) {
        var fqn = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        var docId = Microsoft.CodeAnalysis.DocumentationCommentId.CreateDeclarationId(sym) ?? fqn;
        var asm = sym.ContainingAssembly?.Name ?? string.Empty;
        var file = sym.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;

        var inner = new List<Block>();
        if (!string.IsNullOrEmpty(fqn)) {
            inner.Add(new SectionBlock("FQN", new SequenceBlock(new List<Block> { new ParagraphBlock(fqn) })));
        }
        if (!string.IsNullOrEmpty(docId)) {
            inner.Add(new SectionBlock("DocId", new SequenceBlock(new List<Block> { new ParagraphBlock(docId) })));
        }
        if (!string.IsNullOrEmpty(asm)) {
            inner.Add(new SectionBlock("Assembly", new SequenceBlock(new List<Block> { new ParagraphBlock(asm) })));
        }
        if (!string.IsNullOrEmpty(file)) {
            inner.Add(new SectionBlock("File", new SequenceBlock(new List<Block> { new ParagraphBlock(System.IO.Path.GetFileName(file)) })));
        }

        return new SectionBlock("Type Metadata", new SequenceBlock(inner));
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

