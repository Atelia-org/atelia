using CodeCortexV2.Abstractions;
using CodeCortexV2.Formatting;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.Providers;

/// <summary>
/// Namespace outline provider that returns a "pseudo" TypeOutline:
/// - First member is the namespace itself (metadata + xml doc rendered as markdown)
/// - Then direct children only: child namespaces and direct public/protected types (no type members)
/// This allows reusing the existing MarkdownLayout.RenderTypeOutline pipeline.
/// </summary>
public sealed class NamespaceOutlineProvider
{
    private readonly Func<SymbolId, ISymbol?> _resolve;

    public NamespaceOutlineProvider(Func<SymbolId, ISymbol?> symbolResolver)
    {
        _resolve = symbolResolver;
    }

    public Task<TypeOutline> GetNamespaceAsTypeOutlineAsync(SymbolId namespaceId, OutlineOptions? options, CancellationToken ct)
    {
        options ??= new OutlineOptions();
        var sym = _resolve(namespaceId) as INamespaceSymbol;
        if (sym is null)
        {
            throw new InvalidOperationException($"Namespace symbol not found: {namespaceId}");
        }

        // Title = minimal namespace name
        var nsName = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // Build synthetic parent member (namespace as a member)
        var nsBlocks = MarkdownLayout.BuildMemberBlocks(sym);
        var nsMeta = BuildNamespaceMetadataSection(sym);
        nsBlocks.Insert(0, nsMeta);
        var nsSummaryMd = MarkdownLayout.RenderBlocksToMarkdown(nsBlocks, string.Empty, RenderMode.Final, baseHeadingLevel: 3);
        var parent = new MemberOutline(
            Kind: "namespace",
            Name: nsName,
            Signature: SignatureFormatter.RenderSignature(sym),
            Summary: nsSummaryMd,
            DeclaredIn: new CodeCortexV2.Abstractions.Location(string.Empty, 0, 0)
        );

        var members = new List<MemberOutline> { parent };

        // 1) Direct child namespaces (alphabetical)
        foreach (var sub in sym.GetNamespaceMembers().OrderBy(n => n.Name))
        {
            ct.ThrowIfCancellationRequested();
            var blocks = MarkdownLayout.BuildMemberBlocks(sub);
            var sum = MarkdownLayout.RenderBlocksToMarkdown(blocks, string.Empty, RenderMode.Final, baseHeadingLevel: 3);
            members.Add(new MemberOutline(
                Kind: "namespace",
                Name: sub.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Signature: SignatureFormatter.RenderSignature(sub),
                Summary: sum,
                DeclaredIn: new CodeCortexV2.Abstractions.Location(string.Empty, 0, 0)
            ));
        }

        // 2) Direct public/protected types (alphabetical)
        foreach (var t in sym.GetTypeMembers()
                     .Where(t => IsPublicApi(t))
                     .OrderBy(t => t.Name))
        {
            ct.ThrowIfCancellationRequested();
            // Only type-level metadata + xml doc (no members)
            var typeBlocks = XmlDocFormatter.BuildMemberBlocks(t);
            var typeMeta = BuildTypeMetadataSection(t);
            typeBlocks.Insert(0, typeMeta);
            var typeSummaryMd = MarkdownLayout.RenderBlocksToMarkdown(typeBlocks, string.Empty, RenderMode.Final, baseHeadingLevel: 3);
            members.Add(new MemberOutline(
                Kind: TypeKindKeyword(t.TypeKind),
                Name: t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Signature: SignatureFormatter.RenderSignature(t),
                Summary: typeSummaryMd,
                DeclaredIn: ToLocation(t)
            ));
        }

        // Optional truncation
        if (options.MaxItems is int max && max > 0 && members.Count > max)
        {
            members = members.Take(max).ToList();
        }

        var dto = new TypeOutline(nsName, null, members);
        return Task.FromResult(dto);
    }

    private static bool IsPublicApi(INamedTypeSymbol s)
        => s.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;

    private static string TypeKindKeyword(TypeKind k) => k switch
    {
        TypeKind.Class => "class",
        TypeKind.Struct => "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => k.ToString().ToLowerInvariant()
    };

    private static CodeCortexV2.Abstractions.Location ToLocation(ISymbol s)
    {
        var file = s.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath ?? string.Empty;
        return new CodeCortexV2.Abstractions.Location(file, 0, 0);
    }

    private static SectionBlock BuildNamespaceMetadataSection(INamespaceSymbol sym)
    {
        var fqn = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        var docId = DocumentationCommentId.CreateDeclarationId(sym) ?? ("N:" + fqn);

        var inner = new List<Block>();
        if (!string.IsNullOrEmpty(fqn))
        {
            inner.Add(new SectionBlock("FQN", new SequenceBlock(new List<Block> { new ParagraphBlock(fqn) })));
        }
        if (!string.IsNullOrEmpty(docId))
        {
            inner.Add(new SectionBlock("DocId", new SequenceBlock(new List<Block> { new ParagraphBlock(docId) })));
        }

        return new SectionBlock("Namespace Metadata", new SequenceBlock(inner));
    }

    private static SectionBlock BuildTypeMetadataSection(INamedTypeSymbol sym)
    {
        var fqn = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        var docId = Microsoft.CodeAnalysis.DocumentationCommentId.CreateDeclarationId(sym) ?? fqn;
        var asm = sym.ContainingAssembly?.Name ?? string.Empty;
        var file = sym.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;

        var inner = new List<Block>();
        if (!string.IsNullOrEmpty(fqn))
        {
            inner.Add(new SectionBlock("FQN", new SequenceBlock(new List<Block> { new ParagraphBlock(fqn) })));
        }
        if (!string.IsNullOrEmpty(docId))
        {
            inner.Add(new SectionBlock("DocId", new SequenceBlock(new List<Block> { new ParagraphBlock(docId) })));
        }
        if (!string.IsNullOrEmpty(asm))
        {
            inner.Add(new SectionBlock("Assembly", new SequenceBlock(new List<Block> { new ParagraphBlock(asm) })));
        }
        if (!string.IsNullOrEmpty(file))
        {
            inner.Add(new SectionBlock("File", new SequenceBlock(new List<Block> { new ParagraphBlock(System.IO.Path.GetFileName(file)) })));
        }

        return new SectionBlock("Type Metadata", new SequenceBlock(inner));
    }
}

