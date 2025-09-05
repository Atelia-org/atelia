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
public sealed class OutlineExtractor : IOutlineExtractor {
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
            var lines = GetSummaryLines(symbol);
            if (lines.Count > 0) {
                sb.AppendLine("XMLDOC:");
                foreach (var l in lines) sb.AppendLine("  " + l);
            }
        }
        sb.AppendLine();
        sb.AppendLine("Public API:");
        foreach (var m in symbol.GetMembers().Where(IsPublicApiMember).OrderBy(m => m.Name)) {
            // Skip accessor/event access methods; properties/events will be shown as single logical items
            if (m is IMethodSymbol ms && (ms.MethodKind == MethodKind.PropertyGet || ms.MethodKind == MethodKind.PropertySet || ms.MethodKind == MethodKind.EventAdd || ms.MethodKind == MethodKind.EventRemove)) {
                continue;
            }
            string line;
            if (m is IPropertySymbol ps) {
                var acc = new StringBuilder();
                acc.Append("{ ");
                if (ps.GetMethod != null && IsPublicApiMember(ps.GetMethod)) acc.Append("get; ");
                if (ps.SetMethod != null && IsPublicApiMember(ps.SetMethod)) acc.Append("set; ");
                acc.Append("}");
                string namePart;
                if (ps.IsIndexer) {
                    var parms = string.Join(", ", ps.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + p.Name));
                    namePart = $"this[{parms}]";
                } else {
                    namePart = ps.Name;
                }
                var typeStr = ps.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                line = $"{typeStr} {namePart} {acc}";
            } else if (m is INamedTypeSymbol nt && (nt.TypeKind == TypeKind.Class || nt.TypeKind == TypeKind.Struct || nt.TypeKind == TypeKind.Interface || nt.TypeKind == TypeKind.Enum || nt.TypeKind == TypeKind.Delegate)) {
                var display = nt.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                line = $"{TypeKindKeyword(nt.TypeKind)} {display}";
            } else {
                line = m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            }
            sb.AppendLine("  + " + line); // 1级缩进，2个空格
            var mlines = GetSummaryLines(m);
            foreach (var ml in mlines) {
                var tr = ml.TrimStart();
                if (tr.StartsWith("- ") || System.Text.RegularExpressions.Regex.IsMatch(tr, @"^\s*\d+[.)]\s") || tr.StartsWith("| ")) sb.AppendLine("    " + ml); // 2级缩进，2*2个空格
                else sb.AppendLine("    - " + ml); // 2级缩进，2*2个空格
            }
            // Params / Returns / Exceptions sections
            AppendPredefinedSections(sb, m);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static bool IsPublicApiMember(ISymbol s) => s.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal && !s.IsImplicitlyDeclared;

    private static List<string> GetSummaryLines(ISymbol symbol) {
        var xml = symbol.GetDocumentationCommentXml() ?? string.Empty;
        var lines = ExtractSummaryLinesFromXml(xml);
        if (lines.Count == 0) {
            var fb = TryExtractSummaryLinesFromTrivia(symbol);
            lines = fb;
        }
        // Html decode and trim each line; drop empties
        var result = new List<string>(lines.Count);
        foreach (var l in lines) {
            var t = WebUtility.HtmlDecode(l).Trim();
            if (!string.IsNullOrWhiteSpace(t)) result.Add(t);
        }
        return result;
    }

    private static List<string> ExtractSummaryLinesFromXml(string xml) {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(xml)) return list;
        try {
            var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var summary = doc.Descendants("summary").FirstOrDefault();
            if (summary == null) return list;
            AppendNodeText(summary, list, 0);
        } catch { }
        // Trim trailing empties
        TrimTrailingEmpty(list);
        return list;
    }

    // Recursively append text; insert newlines around structural elements like <para/>
    private static void AppendNodeText(XNode node, List<string> lines, int level) {
        switch (node) {
            case XText t:
                AppendText(lines, t.Value);
                break;
            case XElement e:
                var name = e.Name.LocalName.ToLowerInvariant();
                if (name == "para" || name == "br") {
                    NewLine(lines);
                    foreach (var n in e.Nodes()) AppendNodeText(n, lines, level);
                    NewLine(lines);
                } else if (name == "see") {
                    var lang = e.Attribute("langword")?.Value;
                    if (!string.IsNullOrEmpty(lang)) {
                        AppendText(lines, LangwordToDisplay(lang!));
                    } else {
                        var cref = e.Attribute("cref")?.Value;
                        AppendText(lines, CrefToDisplay(cref));
                    }
                } else if (name == "typeparamref" || name == "paramref") {
                    var nm = e.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(nm)) AppendText(lines, nm!);
                } else if (name == "c") {
                    AppendText(lines, "`" + e.Value + "`");
                } else if (name == "code") {
                    NewLine(lines);
                    AppendText(lines, "`" + e.Value + "`");
                    NewLine(lines);
                } else if (name == "list") {
                    RenderList(e, lines, level);
                } else {
                    foreach (var n in e.Nodes()) AppendNodeText(n, lines, level);
                }
                break;
            default:
                // skip comments/processing
                break;
        }
    }

    private static void RenderList(XElement list, List<string> lines, int level) {
        var type = (list.Attribute("type")?.Value ?? "bullet").ToLowerInvariant();
        if (type == "table") {
            RenderTableList(list, lines, level);
            return;
        }
        // bullet or number
        var items = list.Elements("item").ToList();
        foreach (var item in items) {
            string content = string.Empty;
            var term = item.Element("term")?.Value?.Trim();
            var desc = item.Element("description")?.Value?.Trim();
            if (!string.IsNullOrEmpty(term) || !string.IsNullOrEmpty(desc)) {
                content = (term ?? string.Empty) + (string.IsNullOrEmpty(desc) ? string.Empty : " — " + desc);
            } else {
                // Fallback: raw concatenated value
                content = (item.Value ?? string.Empty).Trim();
            }
            var indent = new string(' ', level * 2);
            var prefix = type == "number" ? "1. " : "- ";
            lines.Add(indent + prefix + content);
            // Nested lists inside item
            foreach (var childList in item.Elements("list")) {
                RenderList(childList, lines, level + 1);
            }
        }
    }

    private static void RenderTableList(XElement list, List<string> lines, int level) {
        var indent = new string(' ', level * 2);
        var header = list.Element("listheader");
        var headers = header?.Elements("term").Select(t => t.Value.Trim()).ToList() ?? new List<string>();
        if (headers.Count > 0) {
            lines.Add(indent + "| " + string.Join(" | ", headers) + " |");
            lines.Add(indent + "|" + string.Join("|", headers.Select(_ => "---")) + "|");
        }
        foreach (var item in list.Elements("item")) {
            var cells = item.Elements("term").Select(t => t.Value.Trim()).ToList();
            if (cells.Count == 0) {
                var d = item.Element("description")?.Value?.Trim();
                if (!string.IsNullOrEmpty(d)) cells.Add(d!);
            }
            if (cells.Count > 0) lines.Add(indent + "| " + string.Join(" | ", cells) + " |");
        }
    }

    private static void TrimTrailingEmpty(List<string> list) {
        while (list.Count > 0 && string.IsNullOrWhiteSpace(list[^1])) list.RemoveAt(list.Count - 1);
    }

    private static void TrimLeadingEmpty(List<string> list) {
        while (list.Count > 0 && string.IsNullOrWhiteSpace(list[0])) list.RemoveAt(0);
    }

    private static bool IsStructuralLine(string line) {
        var tr = line.TrimStart();
        return tr.StartsWith("- ") || tr.StartsWith("1.") || tr.StartsWith("| ");
    }

    private static string InlineParagraph(List<string> lines) {
        var sb = new StringBuilder();
        foreach (var l in lines) {
            var t = l.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static void AppendPredefinedSections(StringBuilder sb, ISymbol m) {
        var xml = m.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return;
        XDocument doc;
        try { doc = XDocument.Parse(xml!, LoadOptions.PreserveWhitespace); }
        catch { return; }

        // Params
        var paramEls = doc.Descendants("param").ToList();
        if (paramEls.Count > 0) {
            sb.AppendLine("      Params:");
            foreach (var p in paramEls) {
                var name = p.Attribute("name")?.Value ?? string.Empty;
                var lines = new List<string>();
                foreach (var n in p.Nodes()) AppendNodeText(n, lines, 0);
                TrimLeadingEmpty(lines);
                TrimTrailingEmpty(lines);
                if (lines.Count == 0) {
                    sb.AppendLine($"        - {name}");
                } else if (lines.All(l => !IsStructuralLine(l))) {
                    var para = InlineParagraph(lines);
                    sb.AppendLine($"        - {name} — {para}");
                } else {
                    var first = lines[0];
                    sb.AppendLine($"        - {name} — {first}");
                    for (int i = 1; i < lines.Count; i++) {
                        var tr = lines[i].TrimStart();
                        if (IsStructuralLine(tr)) sb.AppendLine("          " + lines[i]);
                        else sb.AppendLine("          - " + lines[i]);
                    }
                }
                sb.AppendLine();
            }
        }

        // Returns
        var returnsEl = doc.Descendants("returns").FirstOrDefault();
        if (returnsEl != null) {
            var lines = new List<string>();
            foreach (var n in returnsEl.Nodes()) AppendNodeText(n, lines, 0);
            TrimLeadingEmpty(lines);
            TrimTrailingEmpty(lines);
            if (lines.Count > 0) {
                sb.AppendLine("      Returns:");
                if (lines.All(l => !IsStructuralLine(l))) {
                    sb.AppendLine("        - " + InlineParagraph(lines));
                } else {
                    foreach (var line in lines) {
                        var tr = line.TrimStart();
                        if (IsStructuralLine(tr)) sb.AppendLine("        " + line);
                        else sb.AppendLine("        - " + line);
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
                var type = CrefToDisplay(cref);
                var lines = new List<string>();
                foreach (var n in ex.Nodes()) AppendNodeText(n, lines, 0);
                TrimLeadingEmpty(lines);
                TrimTrailingEmpty(lines);
                if (lines.Count == 0) sb.AppendLine($"        - {type}");
                else if (lines.All(l => !IsStructuralLine(l))) {
                    sb.AppendLine($"        - {type} — {InlineParagraph(lines)}");
                } else {
                    sb.AppendLine($"        - {type} — {lines[0]}");
                    for (int i = 1; i < lines.Count; i++) {
                        var tr = lines[i].TrimStart();
                        if (IsStructuralLine(tr)) sb.AppendLine("          " + lines[i]);
                        else sb.AppendLine("          - " + lines[i]);
                    }
                }
                sb.AppendLine();
            }
        }
    }

    private static void AppendText(List<string> lines, string text) {
        if (string.IsNullOrEmpty(text)) return;
        if (lines.Count == 0) lines.Add(string.Empty);
        // preserve author-intended newlines within text
        var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < parts.Length; i++) {
            if (i > 0) NewLine(lines);
            lines[^1] += parts[i];
        }
    }
    private static void NewLine(List<string> lines) {
        // only add a new line if current line has content or previous was also break
        if (lines.Count == 0 || lines[^1].Length > 0) lines.Add(string.Empty);
        else lines.Add(string.Empty);
    }

    private static string CrefToDisplay(string? cref) {
        if (string.IsNullOrEmpty(cref)) return string.Empty;
        // Remove member kind prefix (e.g., "M:", "T:")
        var s = cref;
        int colon = s.IndexOf(':');
        if (colon >= 0 && colon + 1 < s.Length) s = s[(colon + 1)..];
        // Normalize generics: Foo`1 -> Foo<T>
        s = System.Text.RegularExpressions.Regex.Replace(s, "`[0-9]+", _ => "<T>");
        // Method with parameter list?
        int paren = s.IndexOf('(');
        if (paren >= 0) {
            var namePart = s.Substring(0, paren);
            var paramPart = s.Substring(paren + 1).TrimEnd(')');
            var simpleName = namePart.Contains('.') ? namePart[(namePart.LastIndexOf('.') + 1)..] : namePart;
            var paramNames = new List<string>();
            foreach (var p in paramPart.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                var t = p.Trim();
                // Strip namespaces
                var last = t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t;
                // Map common BCL types to C# keywords
                last = last switch {
                    "Boolean" => "bool",
                    "Int32" => "int",
                    "Int64" => "long",
                    "Double" => "double",
                    "Single" => "float",
                    "String" => "string",
                    "Object" => "object",
                    "Void" => "void",
                    _ => last
                };
                paramNames.Add(last);
            }
            return paramNames.Count == 0 ? simpleName + "()" : simpleName + "(" + string.Join(", ", paramNames) + ")";
        }
        // If ends with ')' but no '(' (malformed), strip trailing ')'
        if (s.EndsWith(")")) s = s.TrimEnd(')');
        // Otherwise show last identifier (with keyword mapping)
        var ident = s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..] : s;
        ident = ident switch {
            "Boolean" => "bool",
            "Int32" => "int",
            "Int64" => "long",
            "Double" => "double",
            "Single" => "float",
            "String" => "string",
            "Object" => "object",
            "Void" => "void",
            _ => ident
        };
        return ident;
    }

    private static string LangwordToDisplay(string word) => word switch {
        "true" => "true",
        "false" => "false",
        "null" => "null",
        _ => word
    };

    private static string TypeKindKeyword(TypeKind k) => k switch {
        TypeKind.Class => "class",
        TypeKind.Struct => "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => k.ToString().ToLowerInvariant()
    };

    private static bool IsBadlyFormedXmlMarker(string line)
        => line.StartsWith("<!-- Badly formed XML comment ignored", StringComparison.Ordinal);

    private static List<string> TryExtractSummaryLinesFromTrivia(ISymbol symbol) {
        var result = new List<string>();
        var decl = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (decl == null) return result;
        var node = decl.GetSyntax();
        var trivia = node.GetLeadingTrivia().FirstOrDefault(t => t.HasStructure && t.GetStructure() is DocumentationCommentTriviaSyntax);
        if (trivia.Equals(default(SyntaxTrivia))) return result;
        var text = trivia.GetStructure()!.ToFullString();
        var stripped = StripXmlTags(text);
        foreach (var line in stripped.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) {
            var t = line.TrimEnd();
            if (t.Length == 0 && (result.Count == 0 || string.IsNullOrWhiteSpace(result[^1]))) continue;
            result.Add(t);
        }
        return result;
    }

    private static string StripXmlTags(string s) {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        for (int i = 0; i < s.Length; i++) {
            var ch = s[i];
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }
        return sb.ToString();
    }
}
