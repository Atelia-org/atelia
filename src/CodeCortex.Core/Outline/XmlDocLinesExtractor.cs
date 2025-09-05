using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeCortex.Core.Outline;

internal static partial class XmlDocLinesExtractor {
    public static List<string> GetSummaryLines(ISymbol symbol) {
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
            if (!string.IsNullOrWhiteSpace(t)) {
                result.Add(t);
            }
        }
        return result;
    }

    public static List<string> ExtractSummaryLinesFromXml(string xml) {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(xml)) {
            return list;
        }

        try {
            var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var summary = doc.Descendants("summary").FirstOrDefault();
            if (summary == null) {
                return list;
            }

            AppendNodeText(summary, list, 0);
        } catch { }
        // Trim trailing empties
        TrimTrailingEmpty(list);
        return list;
    }

    public static void AppendNodeText(XNode node, List<string> lines, int level) {
        switch (node) {
            case XText t:
                AppendText(lines, t.Value);
                break;
            case XElement e:
                var name = e.Name.LocalName.ToLowerInvariant();
                if (name == "para" || name == "br") {
                    NewLine(lines);
                    foreach (var n in e.Nodes()) {
                        AppendNodeText(n, lines, level);
                    }

                    NewLine(lines);
                } else if (name == "see") {
                    var lang = e.Attribute("langword")?.Value;
                    if (!string.IsNullOrEmpty(lang)) {
                        AppendText(lines, LangwordToDisplay(lang!));
                    } else {
                        var cref = e.Attribute("cref")?.Value;
                        var disp = CrefToDisplay(cref);
                        if (!string.IsNullOrEmpty(disp)) {
                            // Render <see cref="..."/> as markdown [cref value]
                            AppendText(lines, "[" + disp + "]");
                        }
                    }
                } else if (name == "typeparamref" || name == "paramref") {
                    var nm = e.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(nm)) {
                        AppendText(lines, nm!);
                    }
                } else if (name == "c") {
                    AppendText(lines, "`" + e.Value + "`");
                } else if (name == "code") {
                    NewLine(lines);
                    AppendText(lines, "`" + e.Value + "`");
                    NewLine(lines);
                } else if (name == "list") {
                    RenderList(e, lines, level);
                } else {
                    foreach (var n in e.Nodes()) {
                        AppendNodeText(n, lines, level);
                    }
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
        for (int i = 0; i < items.Count; i++) {
            var item = items[i];
            string content;
            var term = item.Element("term")?.Value?.Trim();
            var desc = item.Element("description")?.Value?.Trim();
            if (!string.IsNullOrEmpty(term) && !string.IsNullOrEmpty(desc)) {
                content = term + " — " + desc;
            } else if (!string.IsNullOrEmpty(desc)) {
                content = desc!;
            } else if (!string.IsNullOrEmpty(term)) {
                content = term!;
            } else {
                // Fallback: build inline text from child nodes to preserve <see/> and <c>
                var tmp = new List<string>();
                foreach (var n in item.Nodes()) {
                    AppendNodeText(n, tmp, level);
                }

                TrimLeadingEmpty(tmp);
                TrimTrailingEmpty(tmp);
                var pieces = tmp.Select(t => t.Trim()).Where(t => t.Length > 0);
                content = string.Join(" ", pieces);
            }
            var indent = new string(' ', level * 2);
            var prefix = type == "number" ? ($"{i + 1}. ") : "- ";
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
                if (!string.IsNullOrEmpty(d)) {
                    cells.Add(d!);
                }
            }
            if (cells.Count > 0) {
                lines.Add(indent + "| " + string.Join(" | ", cells) + " |");
            }
        }
    }

    public static void TrimTrailingEmpty(List<string> list) {
        while (list.Count > 0 && string.IsNullOrWhiteSpace(list[^1])) {
            list.RemoveAt(list.Count - 1);
        }
    }

    public static void TrimLeadingEmpty(List<string> list) {
        while (list.Count > 0 && string.IsNullOrWhiteSpace(list[0])) {
            list.RemoveAt(0);
        }
    }

    public static string CrefToDisplay(string? cref) {
        if (string.IsNullOrEmpty(cref)) {
            return string.Empty;
        }
        // Remove member kind prefix (e.g., "M:", "T:")
        var s = cref;
        int colon = s.IndexOf(':');
        if (colon >= 0 && colon + 1 < s.Length) {
            s = s[(colon + 1)..];
        }
        // Normalize generics: Foo`1 -> Foo<T>
        s = RxGenericArity().Replace(s, "<T>");
        // Method with parameter list?
        int paren = s.IndexOf('(');
        if (paren >= 0) {
            var namePart = s.Substring(0, paren);
            var paramPart = s.Substring(paren + 1).TrimEnd(')');
            var simpleNameFull = namePart;
            var simpleName = namePart.Contains('.') ? namePart[(namePart.LastIndexOf('.') + 1)..] : namePart;
            // Map method name if it's a type (rare) — typically methods; keep as-is
            var paramNames = new List<string>();
            foreach (var p in paramPart.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                var t = p.Trim();
                // Strip namespaces
                var last = t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t;
                last = MapToCSharpKeyword(t) ?? MapToCSharpKeyword(last) ?? last;
                paramNames.Add(last);
            }
            var inside = string.Join(", ", paramNames);
            return simpleName + "(" + inside + ")";
        }
        // No parameter list: could be a type/member. Prefer keyword for common BCL types.
        var mapped = MapToCSharpKeyword(s);
        if (mapped != null) {
            return mapped;
        }

        var lastOnly = s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..] : s;
        mapped = MapToCSharpKeyword(lastOnly);
        return mapped ?? s;
    }

    private static string? MapToCSharpKeyword(string typeName) {
        switch (typeName) {
            case "System.String":
            case "String":
                return "string";
            case "System.Int32":
            case "Int32":
                return "int";
            case "System.Boolean":
            case "Boolean":
                return "bool";
            case "System.Object":
            case "Object":
                return "object";
            case "System.Void":
            case "Void":
                return "void";
            case "System.Char":
            case "Char":
                return "char";
            case "System.Byte":
            case "Byte":
                return "byte";
            case "System.SByte":
            case "SByte":
                return "sbyte";
            case "System.Int16":
            case "Int16":
                return "short";
            case "System.Int64":
            case "Int64":
                return "long";
            case "System.UInt16":
            case "UInt16":
                return "ushort";
            case "System.UInt32":
            case "UInt32":
                return "uint";
            case "System.UInt64":
            case "UInt64":
                return "ulong";
            case "System.Single":
            case "Single":
                return "float";
            case "System.Double":
            case "Double":
                return "double";
            case "System.Decimal":
            case "Decimal":
                return "decimal";
            default:
                return null;
        }
    }

    private static string LangwordToDisplay(string word) => word switch {
        "true" => "true",
        "false" => "false",
        "null" => "null",
        _ => word
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"`[0-9]+")]
    private static partial System.Text.RegularExpressions.Regex RxGenericArity();

    public static List<string> TryExtractSummaryLinesFromTrivia(ISymbol symbol) {
        var list = new List<string>();
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences) {
            var node = syntaxRef.GetSyntax();
            var leading = node.GetLeadingTrivia();
            foreach (var tr in leading) {
                if (tr.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || tr.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) {
                    var xmlWithPrefixes = tr.ToFullString();
                    var cleaned = StripDocCommentPrefixes(xmlWithPrefixes);
                    try {
                        var doc = XDocument.Parse(cleaned, LoadOptions.PreserveWhitespace);
                        var summary = doc.Descendants("summary").FirstOrDefault();
                        if (summary != null) {
                            AppendNodeText(summary, list, 0);
                            TrimTrailingEmpty(list);
                            return list;
                        }
                    } catch { }
                }
            }
        }
        return list;
    }

    private static void AppendText(List<string> lines, string text) {
        if (string.IsNullOrEmpty(text)) {
            return;
        }

        if (lines.Count == 0) {
            lines.Add(string.Empty);
        }
        // preserve author-intended newlines within text
        var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < parts.Length; i++) {
            if (i > 0) {
                NewLine(lines);
            }

            lines[^1] += parts[i];
        }
    }

    private static void NewLine(List<string> lines) {
        // only add a new line if current line has content or previous was also break
        if (lines.Count == 0 || lines[^1].Length > 0) {
            lines.Add(string.Empty);
        } else {
            lines.Add(string.Empty);
        }
    }

    private static string StripDocCommentPrefixes(string xml) {
        if (string.IsNullOrEmpty(xml)) {
            return string.Empty;
        }

        var sb = new StringBuilder(xml.Length);
        var lines = xml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines) {
            var t = line.TrimStart();
            if (t.StartsWith("///")) {
                // remove leading '///' and at most one space after it
                var idx = line.IndexOf("///");
                var rest = line[(idx + 3)..];
                if (rest.StartsWith(" ")) {
                    rest = rest.Substring(1);
                }

                sb.AppendLine(rest);
            } else {
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }
}

