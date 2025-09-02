using System.Text;
using CodeCortex.Core.Hashing;
using CodeCortex.Core.Ids;
using CodeCortex.Core.Models;
using Microsoft.CodeAnalysis;

namespace CodeCortex.Core.Outline;

/// <summary>
/// Phase1 outline extractor producing markdown summary per design spec (reduced fields).
/// </summary>
public sealed class OutlineExtractor : IOutlineExtractor {
    /// <inheritdoc />
    public string BuildOutline(INamedTypeSymbol symbol, TypeHashes hashes, OutlineOptions options) {
        var id = TypeIdGenerator.GetId(symbol);
        var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var files = string.Join(",", symbol.DeclaringSyntaxReferences.Select(r => Path.GetFileName(r.SyntaxTree.FilePath)).Distinct());
        var asm = symbol.ContainingAssembly?.Name ?? "";
        var sb = new StringBuilder();
        sb.AppendLine($"# {fqn} {id}");
        sb.AppendLine($"Kind: {symbol.TypeKind.ToString().ToLower()} | Files: {files} | Assembly: {asm} | StructureHash: {hashes.Structure}");
        sb.AppendLine($"PublicImplHash: {hashes.PublicImpl} | InternalImplHash: {hashes.InternalImpl} | ImplHash: {hashes.Impl}");
        sb.AppendLine($"XmlDocHash: {hashes.XmlDoc}");
        if (options.IncludeXmlDocFirstLine) {
            var first = FirstXmlLine(symbol);
            if (!string.IsNullOrEmpty(first)) {
                sb.AppendLine($"XMLDOC: {first}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Public API:");
        foreach (var m in symbol.GetMembers().Where(IsPublicApiMember).OrderBy(m => m.Name)) {
            string line;
            if (m is IPropertySymbol ps) {
                var accessors = new StringBuilder();
                accessors.Append(ps.Name).Append(" { ");
                if (ps.GetMethod != null && IsPublicApiMember(ps.GetMethod)) {
                    accessors.Append("get; ");
                }

                if (ps.SetMethod != null && IsPublicApiMember(ps.SetMethod)) {
                    accessors.Append("set; ");
                }

                line = accessors.Append("}").ToString();
            } else {
                line = m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            }
            sb.AppendLine("  + " + line);
        }
        return sb.ToString();
    }

    private static bool IsPublicApiMember(ISymbol s) => s.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal && !s.IsImplicitlyDeclared;

    private static string FirstXmlLine(INamedTypeSymbol symbol) {
        var xml = symbol.GetDocumentationCommentXml() ?? string.Empty;
        using var sr = new System.IO.StringReader(xml);
        string? line;
        while ((line = sr.ReadLine()) != null) {
            var t = line.Trim();
            if (t.Length == 0) {
                continue;
            }

            return t.Length > 160 ? t[..160] : t;
        }
        return string.Empty;
    }
}
