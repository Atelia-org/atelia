using System.Text;
using CodeCortex.Core.Hashing;
using CodeCortex.Core.Ids;
using CodeCortex.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Net;
using System.Xml.Linq;

namespace CodeCortex.Core.Outline;

/// <summary>
/// Phase1 outline extractor producing markdown summary per design spec (reduced fields).
/// </summary>
public sealed partial class OutlineExtractor : IOutlineExtractor {
    /// &lt;inheritdoc /&gt;
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
            var lines = XmlDocLinesExtractor.GetSummaryLines(symbol);
            if (lines.Count > 0) {
                sb.AppendLine("XMLDOC:");
                MarkdownRenderer.RenderLinesWithStructure(sb, lines, "  ", bulletizePlain: false, startIndex: 0, insertBlankBeforeTable: true);
            }
        }
        sb.AppendLine();
        sb.AppendLine("Public API:");
        foreach (var m in symbol.GetMembers().Where(IsPublicApiMember).OrderBy(m => m.Name)) {
            // Skip accessor/event access methods; properties/events will be shown as single logical items
            if (m is IMethodSymbol ms && (ms.MethodKind == MethodKind.PropertyGet || ms.MethodKind == MethodKind.PropertySet || ms.MethodKind == MethodKind.EventAdd || ms.MethodKind == MethodKind.EventRemove)) { continue; }
            string line;
            if (m is IPropertySymbol ps) {
                var acc = new StringBuilder();
                acc.Append("{ ");
                if (ps.GetMethod != null && IsPublicApiMember(ps.GetMethod)) {
                    acc.Append("get; ");
                }

                if (ps.SetMethod != null && IsPublicApiMember(ps.SetMethod)) {
                    acc.Append("set; ");
                }

                acc.Append("}");
                string namePart;
                if (ps.IsIndexer) {
                    var parms = string.Join(", ", ps.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + p.Name));
                    namePart = $"this[{parms}]";
                }
                else {
                    namePart = ps.Name;
                }
                var typeStr = ps.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                line = $"{typeStr} {namePart} {acc}";
            }
            else if (m is INamedTypeSymbol nt && (nt.TypeKind == TypeKind.Class || nt.TypeKind == TypeKind.Struct || nt.TypeKind == TypeKind.Interface || nt.TypeKind == TypeKind.Enum || nt.TypeKind == TypeKind.Delegate)) {
                if (nt.TypeKind == TypeKind.Delegate) {
                    var invoke = nt.DelegateInvokeMethod;
                    var ret = invoke?.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "void";
                    var nameDisplay = nt.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    var parms = invoke == null ? string.Empty : string.Join(", ", invoke.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + p.Name));
                    line = $"delegate {ret} {nameDisplay}({parms})";
                }
                else {
                    var display = nt.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    line = $"{TypeKindKeyword(nt.TypeKind)} {display}";
                }
            }
            else {
                line = m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            }
            sb.AppendLine("  + " + line); // 1级缩进，2个空格
            var mlines = XmlDocLinesExtractor.GetSummaryLines(m);
            MarkdownRenderer.RenderLinesWithStructure(sb, mlines, "    ", bulletizePlain: true, startIndex: 0, insertBlankBeforeTable: true);
            // Params / Returns / Exceptions sections
            AppendPredefinedSections(sb, m);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static bool IsPublicApiMember(ISymbol s) => s.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal && !s.IsImplicitlyDeclared;

    private static List<string> GetSummaryLines(ISymbol symbol) {
        return XmlDocLinesExtractor.GetSummaryLines(symbol);
    }








    private static void AppendPredefinedSections(StringBuilder sb, ISymbol m) {
        var xml = m.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) { return; }
        XDocument doc;
        try { doc = XDocument.Parse(xml!, LoadOptions.PreserveWhitespace); } catch { return; }

        // Params
        var paramEls = doc.Descendants("param").ToList();
        if (paramEls.Count > 0) {
            sb.AppendLine("      Params:");
            foreach (var p in paramEls) {
                var name = p.Attribute("name")?.Value ?? string.Empty;
                var lines = new List<string>();
                foreach (var n in p.Nodes()) {
                    XmlDocLinesExtractor.AppendNodeText(n, lines, 0);
                }

                XmlDocLinesExtractor.TrimLeadingEmpty(lines);
                XmlDocLinesExtractor.TrimTrailingEmpty(lines);
                if (lines.Count == 0) {
                    sb.AppendLine($"        - {name}");
                }
                else if (lines.All(l => !MarkdownRenderer.IsStructuralLine(l))) {
                    var para = MarkdownRenderer.InlineParagraph(lines);
                    sb.AppendLine($"        - {name} — {para}");
                }
                else {
                    var first = lines[0];
                    var firstTrim = first.TrimStart();
                    if (MarkdownRenderer.IsStructuralLine(firstTrim)) {
                        sb.AppendLine($"        - {name}");
                        MarkdownRenderer.RenderLinesWithStructure(sb, lines, "          ", bulletizePlain: true, startIndex: 0, insertBlankBeforeTable: true);
                    }
                    else {
                        sb.AppendLine($"        - {name} — {first}");
                        if (lines.Count > 1) {
                            MarkdownRenderer.RenderLinesWithStructure(sb, lines, "          ", bulletizePlain: true, startIndex: 1, insertBlankBeforeTable: true);
                        }
                    }
                }
                sb.AppendLine();
            }
        }

        // Returns
        var returnsEl = doc.Descendants("returns").FirstOrDefault();
        if (returnsEl != null) {
            var lines = new List<string>();
            foreach (var n in returnsEl.Nodes()) {
                XmlDocLinesExtractor.AppendNodeText(n, lines, 0);
            }

            XmlDocLinesExtractor.TrimLeadingEmpty(lines);
            XmlDocLinesExtractor.TrimTrailingEmpty(lines);
            if (lines.Count > 0) {
                sb.AppendLine("      Returns:");
                if (lines.All(l => !MarkdownRenderer.IsStructuralLine(l))) {
                    sb.AppendLine("        - " + MarkdownRenderer.InlineParagraph(lines));
                }
                else {
                    var firstTrim = lines[0].TrimStart();
                    if (MarkdownRenderer.IsStructuralLine(firstTrim)) {
                        MarkdownRenderer.RenderLinesWithStructure(sb, lines, "        ", bulletizePlain: false, startIndex: 0, insertBlankBeforeTable: true);
                    }
                    else {
                        sb.AppendLine("        - " + lines[0].Trim());
                        if (lines.Count > 1) {
                            MarkdownRenderer.RenderLinesWithStructure(sb, lines, "          ", bulletizePlain: true, startIndex: 1, insertBlankBeforeTable: true);
                        }
                    }
                }
                sb.AppendLine();
            }
        }

        // Exceptions
        var exEls = doc.Descendants("exception").ToList();
        if (exEls.Count > 0) {
            sb.AppendLine("      Exceptions:");
            foreach (var ex in exEls) {
                var cref = ex.Attribute("cref")?.Value;
                var type = XmlDocLinesExtractor.CrefToDisplay(cref);
                var lines = new List<string>();
                foreach (var n in ex.Nodes()) {
                    XmlDocLinesExtractor.AppendNodeText(n, lines, 0);
                }

                XmlDocLinesExtractor.TrimLeadingEmpty(lines);
                XmlDocLinesExtractor.TrimTrailingEmpty(lines);
                if (lines.Count == 0) {
                    sb.AppendLine($"        - {type}");
                }
                else if (lines.All(l => !MarkdownRenderer.IsStructuralLine(l))) {
                    sb.AppendLine($"        - {type} — {MarkdownRenderer.InlineParagraph(lines)}");
                }
                else {
                    var firstLine = lines[0];
                    var firstTrim = firstLine.TrimStart();
                    if (MarkdownRenderer.IsStructuralLine(firstTrim)) {
                        // First content is structural (table/list). Show type alone, then render structure from the first line.
                        sb.AppendLine($"        - {type}");
                        MarkdownRenderer.RenderLinesWithStructure(sb, lines, "          ", bulletizePlain: true, startIndex: 0, insertBlankBeforeTable: true);
                    }
                    else {
                        sb.AppendLine($"        - {type} — {firstLine}");
                        if (lines.Count > 1) {
                            MarkdownRenderer.RenderLinesWithStructure(sb, lines, "          ", bulletizePlain: true, startIndex: 1, insertBlankBeforeTable: true);
                        }
                    }
                }
                sb.AppendLine();
            }
        }
    }




    private static string TypeKindKeyword(TypeKind k) => k switch {
        TypeKind.Class => "class",
        TypeKind.Struct => "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => k.ToString().ToLowerInvariant()
    };



}
