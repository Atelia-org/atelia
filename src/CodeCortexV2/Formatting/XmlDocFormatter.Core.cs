using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeCortexV2.Formatting;

internal static partial class XmlDocFormatter
{
    public static List<string> GetSummaryLines(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml() ?? string.Empty;
        var lines = ExtractSummaryLinesFromXml(xml);
        if (lines.Count == 0)
        {
            lines = TryExtractSummaryLinesFromTrivia(symbol);
        }
        var result = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            var t = WebUtility.HtmlDecode(l).Trim();
            if (!string.IsNullOrWhiteSpace(t)) result.Add(t);
        }
        return result;
    }

    public static List<string> ExtractSummaryLinesFromXml(string xml)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(xml)) return list;
        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var summary = doc.Descendants("summary").FirstOrDefault();
            if (summary == null) return list;
            AppendNodeText(summary, list, 0);
        }
        catch { }
        TrimTrailingEmpty(list);
        return list;
    }

    public static void AppendNodeText(XNode node, List<string> lines, int level)
    {
        switch (node)
        {
            case XText t:
                AppendText(lines, t.Value);
                break;
            case XElement e:
                var name = e.Name.LocalName.ToLowerInvariant();
                if (name == "para" || name == "br")
                {
                    NewLine(lines);
                    foreach (var n in e.Nodes()) AppendNodeText(n, lines, level);
                    NewLine(lines);
                }
                else if (name == "see")
                {
                    var lang = e.Attribute("langword")?.Value;
                    if (!string.IsNullOrEmpty(lang))
                    {
                        AppendText(lines, LangwordToDisplay(lang!));
                    }
                    else
                    {
                        var cref = e.Attribute("cref")?.Value;
                        var disp = CrefToDisplay(cref);
                        if (!string.IsNullOrEmpty(disp)) AppendText(lines, "[" + disp + "]");
                    }
                }
                else if (name == "typeparamref" || name == "paramref")
                {
                    var nm = e.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(nm)) AppendText(lines, nm!);
                }
                else if (name == "c")
                {
                    AppendText(lines, "`" + e.Value + "`");
                }
                else if (name == "code")
                {
                    NewLine(lines);
                    AppendText(lines, "`" + e.Value + "`");
                    NewLine(lines);
                }
                else if (name == "list")
                {
                    RenderList(e, lines, level);
                }
                else
                {
                    foreach (var n in e.Nodes()) AppendNodeText(n, lines, level);
                }
                break;
            default:
                break;
        }
    }

    public static List<string> TryExtractSummaryLinesFromTrivia(ISymbol symbol)
    {
        var list = new List<string>();
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var node = syntaxRef.GetSyntax();
            var leading = node.GetLeadingTrivia();
            foreach (var tr in leading)
            {
                if (tr.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || tr.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    var xmlWithPrefixes = tr.ToFullString();
                    var cleaned = StripDocCommentPrefixes(xmlWithPrefixes);
                    try
                    {
                        var doc = XDocument.Parse(cleaned, LoadOptions.PreserveWhitespace);
                        var summary = doc.Descendants("summary").FirstOrDefault();
                        if (summary != null)
                        {
                            AppendNodeText(summary, list, 0);
                            TrimTrailingEmpty(list);
                            return list;
                        }
                    }
                    catch { }
                }
            }
        }
        return list;
    }

    private static void AppendText(List<string> lines, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (lines.Count == 0) lines.Add(string.Empty);
        var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) NewLine(lines);
            lines[^1] += parts[i];
        }
    }

    private static void NewLine(List<string> lines)
    {
        if (lines.Count == 0 || lines[^1].Length > 0) lines.Add(string.Empty);
        else lines.Add(string.Empty);
    }

    private static string StripDocCommentPrefixes(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return string.Empty;
        var sb = new StringBuilder(xml.Length);
        var lines = xml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("///"))
            {
                var idx = line.IndexOf("///");
                var rest = line[(idx + 3)..];
                if (rest.StartsWith(" ")) rest = rest.Substring(1);
                sb.AppendLine(rest);
            }
            else
            {
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    public static string CrefToDisplay(string? cref)
    {
        if (string.IsNullOrEmpty(cref)) return string.Empty;
        var s = cref;
        int colon = s.IndexOf(':');
        if (colon >= 0 && colon + 1 < s.Length) s = s[(colon + 1)..];
        s = RxGenericArity().Replace(s, "<T>");
        int paren = s.IndexOf('(');
        if (paren >= 0)
        {
            var namePart = s.Substring(0, paren);
            var paramPart = s.Substring(paren + 1).TrimEnd(')');
            var simpleName = namePart.Contains('.') ? namePart[(namePart.LastIndexOf('.') + 1)..] : namePart;
            var paramNames = new List<string>();
            foreach (var p in paramPart.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = p.Trim();
                var last = t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t;
                last = MapToCSharpKeyword(t) ?? MapToCSharpKeyword(last) ?? last;
                paramNames.Add(last);
            }
            var inside = string.Join(", ", paramNames);
            return simpleName + "(" + inside + ")";
        }
        var mapped = MapToCSharpKeyword(s);
        if (mapped != null) return mapped;
        var lastOnly = s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..] : s;
        mapped = MapToCSharpKeyword(lastOnly);
        return mapped ?? s;
    }

    private static string? MapToCSharpKeyword(string typeName) => typeName switch
    {
        "System.String" or "String" => "string",
        "System.Int32" or "Int32" => "int",
        "System.Boolean" or "Boolean" => "bool",
        "System.Object" or "Object" => "object",
        "System.Void" or "Void" => "void",
        "System.Char" or "Char" => "char",
        "System.Byte" or "Byte" => "byte",
        "System.SByte" or "SByte" => "sbyte",
        "System.Int16" or "Int16" => "short",
        "System.Int64" or "Int64" => "long",
        "System.UInt16" or "UInt16" => "ushort",
        "System.UInt32" or "UInt32" => "uint",
        "System.UInt64" or "UInt64" => "ulong",
        "System.Single" or "Single" => "float",
        "System.Double" or "Double" => "double",
        "System.Decimal" or "Decimal" => "decimal",
        _ => null
    };

    private static string LangwordToDisplay(string word) => word switch
    {
        "true" => "true",
        "false" => "false",
        "null" => "null",
        _ => word
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"`[0-9]+")]
    private static partial System.Text.RegularExpressions.Regex RxGenericArity();
}

