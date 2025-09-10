using System.Linq;
using System.Collections.Generic;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Formatting;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.Providers;

/// <summary>
/// Builder that produces a renderer-agnostic SymbolOutline tree from Roslyn symbols.
/// - Metadata is kept as a separate structured object (not mixed into Members).
/// - XmlDoc is represented as Block trees (no markdown at this stage).
/// - Namespace overview includes direct child namespaces and public/protected types (no deep expansion by default).
/// </summary>
public static class SymbolOutlineBuilder {
    public static SymbolOutline BuildForType(INamedTypeSymbol type, bool includeMembers = true, CancellationToken ct = default) {
        var name = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var signature = SignatureFormatter.RenderSignature(type);
        var xmlBlocks = XmlDocFormatter.BuildMemberBlocks(type);
        var metadata = BuildMetadata(type);

        var members = new List<SymbolOutline>();
        if (includeMembers) {
            foreach (var m in type.GetMembers().Where(IsPublicApiMember).OrderBy(m => m.Name)) {
                ct.ThrowIfCancellationRequested();
                if (m is IMethodSymbol ms && (ms.MethodKind == MethodKind.PropertyGet || ms.MethodKind == MethodKind.PropertySet || ms.MethodKind == MethodKind.EventAdd || ms.MethodKind == MethodKind.EventRemove)) {
                    continue;
                }
                members.Add(BuildForMember(m));
            }
        }

        return new SymbolOutline(CodeCortexV2.Abstractions.SymbolKind.Type, name, signature, xmlBlocks, members, metadata);
    }

    public static SymbolOutline BuildForNamespace(INamespaceSymbol ns, bool includeChildren = true, CancellationToken ct = default) {
        var name = ns.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var signature = SignatureFormatter.RenderSignature(ns);
        var xmlBlocks = XmlDocFormatter.BuildMemberBlocks(ns);
        var metadata = BuildMetadata(ns);

        var members = new List<SymbolOutline>();
        if (includeChildren) {
            foreach (var sub in ns.GetNamespaceMembers().OrderBy(n => n.Name)) {
                ct.ThrowIfCancellationRequested();
                var child = new SymbolOutline(
                    CodeCortexV2.Abstractions.SymbolKind.Namespace,
                    sub.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    SignatureFormatter.RenderSignature(sub),
                    XmlDocFormatter.BuildMemberBlocks(sub),
                    Members: new List<SymbolOutline>(),
                    Metadata: BuildMetadata(sub)
                );
                members.Add(child);
            }

            foreach (var t in ns.GetTypeMembers().Where(IsPublicApiType).OrderBy(t => t.Name)) {
                ct.ThrowIfCancellationRequested();
                // Overview only: do not include type members here
                var typeNode = BuildForType(t, includeMembers: false, ct);
                members.Add(typeNode);
            }
        }

        return new SymbolOutline(CodeCortexV2.Abstractions.SymbolKind.Namespace, name, signature, xmlBlocks, members, metadata);
    }

    public static SymbolOutline BuildForMember(ISymbol member) {
        var kind = member switch {
            INamespaceSymbol => CodeCortexV2.Abstractions.SymbolKind.Namespace,
            INamedTypeSymbol => CodeCortexV2.Abstractions.SymbolKind.Type,
            IMethodSymbol => CodeCortexV2.Abstractions.SymbolKind.Method,
            IPropertySymbol => CodeCortexV2.Abstractions.SymbolKind.Property,
            IFieldSymbol => CodeCortexV2.Abstractions.SymbolKind.Field,
            IEventSymbol => CodeCortexV2.Abstractions.SymbolKind.Event,
            _ => CodeCortexV2.Abstractions.SymbolKind.Unknown
        };
        var name = member.Name;
        var signature = SignatureFormatter.RenderSignature(member);
        var xmlBlocks = XmlDocFormatter.BuildMemberBlocks(member);
        var metadata = BuildMetadata(member);
        return new SymbolOutline(kind, name, signature, xmlBlocks, Members: new List<SymbolOutline>(), metadata);
    }

    private static bool IsPublicApiMember(ISymbol s) =>
        s.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
        && !s.IsImplicitlyDeclared;

    private static bool IsPublicApiType(INamedTypeSymbol s) =>
        s.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;

    private static SymbolMetadata BuildMetadata(ISymbol s) {
        var fqn = s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        var docId = DocumentationCommentId.CreateDeclarationId(s) ?? fqn;
        var asm = s.ContainingAssembly?.Name;
        var file = s.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;
        return new SymbolMetadata(fqn, docId, asm, file);
    }
}

