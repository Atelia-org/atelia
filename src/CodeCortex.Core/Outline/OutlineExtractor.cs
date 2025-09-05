using System.Text;
using CodeCortex.Core.Hashing;
using CodeCortex.Core.Ids;
using CodeCortex.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Net;

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
        // Primary path: use Roslyn-provided XML (fast path for well-formed docs)
        var xml = symbol.GetDocumentationCommentXml() ?? string.Empty;
        var first = ExtractFirstNonEmptyLine(xml);
        // If Roslyn reports badly formed doc or we couldn't get a line, fall back to syntax trivia extraction
        if (string.IsNullOrEmpty(first) || IsBadlyFormedXmlMarker(first)) {
            var fallback = TryExtractFirstLineFromTrivia(symbol);
            if (!string.IsNullOrEmpty(fallback)) {
                first = fallback;
            }
        }
        if (string.IsNullOrEmpty(first)) {
            return string.Empty;
        }
        // Decode common entities for nicer display (e.g., &lt;T&gt; -> <T>)
        first = WebUtility.HtmlDecode(first).Trim();
        return first.Length > 160 ? first[..160] : first;
    }

    private static string ExtractFirstNonEmptyLine(string xml) {
        if (string.IsNullOrWhiteSpace(xml)) {
            return string.Empty;
        }

        using var sr = new System.IO.StringReader(xml);
        string? line;
        while ((line = sr.ReadLine()) != null) {
            var t = line.Trim();
            if (t.Length == 0) {
                continue;
            }

            return t;
        }
        return string.Empty;
    }

    private static bool IsBadlyFormedXmlMarker(string line)
        => line.StartsWith("<!-- Badly formed XML comment ignored", StringComparison.Ordinal);

    private static string TryExtractFirstLineFromTrivia(INamedTypeSymbol symbol) {
        var decl = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (decl == null) {
            return string.Empty;
        }

        var node = decl.GetSyntax();
        // Look for the first structured documentation trivia before the declaration
        var trivia = node.GetLeadingTrivia()
            .FirstOrDefault(t => t.HasStructure && t.GetStructure() is DocumentationCommentTriviaSyntax);
        if (trivia.Equals(default(SyntaxTrivia))) {
            return string.Empty;
        }

        var text = trivia.GetStructure()!.ToFullString();
        // Strip XML tags to keep only human text, then take first non-empty line
        var stripped = StripXmlTags(text);
        using var sr = new System.IO.StringReader(stripped);
        string? line;
        while ((line = sr.ReadLine()) != null) {
            var t = line.Trim();
            if (t.Length == 0) {
                continue;
            }

            return t;
        }
        return string.Empty;
    }

    private static string StripXmlTags(string s) {
        if (string.IsNullOrEmpty(s)) {
            return string.Empty;
        }

        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        for (int i = 0; i < s.Length; i++) {
            var ch = s[i];
            if (ch == '<') {
                inTag = true;
                continue;
            }
            if (ch == '>') {
                inTag = false;
                continue;
            }
            if (!inTag) {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
