using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeCortexV2.Formatting;
// Layer: Extractor
// - Responsibility: obtain XDocument for symbol XML docs.
// - Sources order: ISymbol.GetDocumentationCommentXml() → leading trivia fallback (///) with prefix stripping.
// - Do NOT: decode HTML entities, normalize cref/langword, build Block tree, or do any layout/rendering.
// - Errors: parse failures are swallowed; return null to trigger summary-only fallback in the facade.


internal static partial class XmlDocFormatter {
    // Extractor: get full XML doc as XDocument (from symbol XML or trivia fallback)
    private static XDocument? TryGetXDocument(ISymbol symbol) {
        var xml = symbol.GetDocumentationCommentXml();
        if (!string.IsNullOrWhiteSpace(xml)) {
            try { return XDocument.Parse(xml!, LoadOptions.PreserveWhitespace); } catch { }
        }
        // trivia fallback
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences) {
            var node = syntaxRef.GetSyntax();
            var leading = node.GetLeadingTrivia();
            foreach (var tr in leading) {
                if (tr.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || tr.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) {
                    var xmlWithPrefixes = tr.ToFullString();
                    var cleaned = StripDocCommentPrefixes(xmlWithPrefixes);
                    try { return XDocument.Parse(cleaned, LoadOptions.PreserveWhitespace); } catch { }
                }
            }
        }
        return null;
    }

    private static string StripDocCommentPrefixes(string xml) {
        if (string.IsNullOrEmpty(xml)) { return string.Empty; }
        var sb = new StringBuilder(xml.Length);
        var lines = xml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines) {
            var t = line.TrimStart();
            if (t.StartsWith("///")) {
                var idx = line.IndexOf("///");
                var rest = line[(idx + 3)..];
                if (rest.StartsWith(" ")) {
                    rest = rest.Substring(1);
                }

                sb.AppendLine(rest);
            }
            else {
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }
}

