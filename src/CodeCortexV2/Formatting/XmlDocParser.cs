using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Atelia.Diagnostics;

namespace CodeCortexV2.Formatting;
// Layer: Parser
// - Responsibility: transform XDocument → Block tree (summary + typed sections).
// - Normalization: HtmlDecode, cref→C# keyword mapping, textualization of <see>/<paramref>/<typeparamref>, unify list/table structures.
// - Do NOT: add Markdown-specific spacing/indentation policy; no IO or side-effects.
// - Deterministic: same input XDocument → same Block tree.


internal static partial class XmlDocFormatter {
    // Parser: build full member blocks (summary + typed sections) from XDocument
    private static List<Block> BuildBlocksFromDocument(XDocument doc) {
        var blocks = new List<Block>();

        SequenceBlock BodyFrom(XElement el) => new SequenceBlock(MapNodesToBlocks(el.Nodes()));

        // Top sections
        var summary = doc.Descendants("summary").FirstOrDefault();
        if (summary != null) {
            blocks.Add(new SectionBlock("Summary", BodyFrom(summary)));
        }

        var remarks = doc.Descendants("remarks").FirstOrDefault();
        if (remarks != null) {
            blocks.Add(new SectionBlock("Remarks", BodyFrom(remarks)));
        }

        var example = doc.Descendants("example").FirstOrDefault();
        if (example != null) {
            blocks.Add(new SectionBlock("Example", BodyFrom(example)));
        }

        var returns = doc.Descendants("returns").FirstOrDefault();
        if (returns != null) {
            blocks.Add(new SectionBlock("Returns", BodyFrom(returns)));
        }

        var value = doc.Descendants("value").FirstOrDefault();
        if (value != null) {
            blocks.Add(new SectionBlock("Value", BodyFrom(value)));
        }

        // Parameters / Type Parameters / Exceptions (Section of Sections)
        var tparams = doc.Descendants("typeparam").ToList();
        if (tparams.Count > 0) {
            var inner = new List<Block>();
            foreach (var tp in tparams) {
                var name = tp.Attribute("name")?.Value ?? string.Empty;
                inner.Add(new SectionBlock(name, BodyFrom(tp)));
            }
            blocks.Add(new SectionBlock("Type Parameters", new SequenceBlock(inner)));
        }

        var @params = doc.Descendants("param").ToList();
        if (@params.Count > 0) {
            var inner = new List<Block>();
            foreach (var p in @params) {
                var name = p.Attribute("name")?.Value ?? string.Empty;
                inner.Add(new SectionBlock(name, BodyFrom(p)));
            }
            blocks.Add(new SectionBlock("Parameters", new SequenceBlock(inner)));
        }

        var exNodes = doc.Descendants("exception").ToList();
        if (exNodes.Count > 0) {
            var inner = new List<Block>();
            foreach (var ex in exNodes) {
                var disp = CrefToDisplay(ex.Attribute("cref")?.Value);
                if (string.IsNullOrEmpty(disp)) {
                    disp = "Exception";
                }

                inner.Add(new SectionBlock(disp!, BodyFrom(ex)));
            }
            blocks.Add(new SectionBlock("Exceptions", new SequenceBlock(inner)));
        }

        // See Also (aggregate)
        var seeAlso = doc.Descendants("seealso").ToList();
        if (seeAlso.Count > 0) {
            var items = new List<SequenceBlock>();
            foreach (var sa in seeAlso) {
                var text = MapInlineNodesToText(new[] { (XNode)sa });
                if (string.IsNullOrWhiteSpace(text)) {
                    var href = sa.Attribute("href")?.Value;
                    if (!string.IsNullOrEmpty(href)) {
                        text = $"[{href}]({href})";
                    } else {
                        var disp = CrefToDisplay(sa.Attribute("cref")?.Value);
                        if (!string.IsNullOrWhiteSpace(disp)) {
                            text = "[" + disp + "]";
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(text)) {
                    items.Add(new SequenceBlock(new List<Block> { new ParagraphBlock(text) }));
                }
            }
            if (items.Count > 0) {
                blocks.Add(new SectionBlock("See Also", new SequenceBlock(new List<Block> { new ListBlock(false, items) })));
            }
        }

        return blocks;
    }

    // Map arbitrary nodes to a sequence of blocks (paragraphs, code, lists, tables)
    private static List<Block> MapNodesToBlocks(IEnumerable<XNode> nodes) {
        var result = new List<Block>();
        var sb = new System.Text.StringBuilder();

        void FlushParagraph() {
            var t = sb.ToString().Trim();
            if (t.Length > 0) {
                result.Add(new ParagraphBlock(t));
            }

            sb.Clear();
        }

        foreach (var node in nodes) {
            switch (node) {
                case XText t:
                    sb.Append(System.Net.WebUtility.HtmlDecode(t.Value));
                    break;
                case XElement e:
                    var name = e.Name.LocalName.ToLowerInvariant();
                    if (name == "para") {
                        // paragraph boundary
                        var pt = MapInlineNodesToText(e.Nodes());
                        FlushParagraph();
                        if (!string.IsNullOrWhiteSpace(pt)) {
                            result.Add(new ParagraphBlock(pt));
                        }
                    } else if (name == "br") {
                        FlushParagraph();
                    } else if (name == "c") {
                        sb.Append('`').Append(e.Value).Append('`');
                    } else if (name == "code") {
                        FlushParagraph();
                        var codeText = e.Value ?? string.Empty;
                        result.Add(new CodeBlock(codeText));
                    } else if (name == "see" || name == "seealso") {
                        var lang = e.Attribute("langword")?.Value;
                        if (!string.IsNullOrEmpty(lang)) {
                            sb.Append(LangwordToDisplay(lang!));
                        } else {
                            var href = e.Attribute("href")?.Value;
                            if (!string.IsNullOrEmpty(href)) {
                                var text = MapInlineNodesToText(e.Nodes());
                                if (string.IsNullOrWhiteSpace(text)) {
                                    text = href;
                                }

                                sb.Append('[').Append(text).Append("](").Append(href).Append(')');
                            } else {
                                var cref = e.Attribute("cref")?.Value;
                                var disp = CrefToDisplay(cref);
                                if (!string.IsNullOrEmpty(disp)) {
                                    sb.Append('[').Append(disp).Append(']');
                                }
                            }
                        }
                    } else if (name == "paramref" || name == "typeparamref") {
                        var nm = e.Attribute("name")?.Value;
                        if (!string.IsNullOrEmpty(nm)) {
                            sb.Append(nm);
                        }
                    } else if (name == "list") {
                        FlushParagraph();
                        var type = (e.Attribute("type")?.Value ?? "bullet").ToLowerInvariant();
                        if (type == "table") {
                            var table = BuildTableBlock(e, out var liftedBlocks);
                            result.Add(table);
                            if (liftedBlocks.Count > 0) {
                                DebugUtil.Print("XmlDocPipeline", $"Table downgrade: lifted {liftedBlocks.Count} blocks from table cells.");
                                result.AddRange(liftedBlocks);
                            }
                        } else {
                            result.Add(BuildListBlock(e, ordered: type == "number"));
                        }
                    } else {
                        // unknown: recurse; but separate from current paragraph
                        FlushParagraph();
                        var sub = MapNodesToBlocks(e.Nodes());
                        if (sub.Count > 0) {
                            result.AddRange(sub);
                        }
                    }
                    break;
                default:
                    break;
            }
        }
        FlushParagraph();
        return result;
    }

    private static string MapInlineNodesToText(IEnumerable<XNode> nodes) {
        var sb = new System.Text.StringBuilder();
        foreach (var n in nodes) {
            switch (n) {
                case XText t:
                    sb.Append(System.Net.WebUtility.HtmlDecode(t.Value));
                    break;
                case XElement e:
                    var name = e.Name.LocalName.ToLowerInvariant();
                    if (name == "c") {
                        sb.Append('`').Append(e.Value).Append('`');
                    } else if (name == "see" || name == "seealso") {
                        var lang = e.Attribute("langword")?.Value;
                        if (!string.IsNullOrEmpty(lang)) {
                            sb.Append(LangwordToDisplay(lang!));
                        } else {
                            var href = e.Attribute("href")?.Value;
                            if (!string.IsNullOrEmpty(href)) {
                                var text = MapInlineNodesToText(e.Nodes());
                                if (string.IsNullOrWhiteSpace(text)) {
                                    text = href;
                                }

                                sb.Append('[').Append(text).Append("](").Append(href).Append(')');
                            } else {
                                var cref = e.Attribute("cref")?.Value;
                                var disp = CrefToDisplay(cref);
                                if (!string.IsNullOrEmpty(disp)) {
                                    sb.Append('[').Append(disp).Append(']');
                                }
                            }
                        }
                    } else if (name == "paramref" || name == "typeparamref") {
                        var nm = e.Attribute("name")?.Value;
                        if (!string.IsNullOrEmpty(nm)) {
                            sb.Append(nm);
                        }
                    } else if (name == "para" || name == "br") {
                        sb.Append(' ');
                    } else {
                        // inline fallback: recurse children inline
                        var inner = MapInlineNodesToText(e.Nodes());
                        if (!string.IsNullOrEmpty(inner)) {
                            sb.Append(inner);
                        }
                    }
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    private static ListBlock BuildListBlock(XElement list, bool ordered) {
        var items = new List<SequenceBlock>();
        foreach (var item in list.Elements("item")) {
            var seq = new SequenceBlock(MapNodesToBlocks(item.Nodes()));
            items.Add(seq);
        }
        return new ListBlock(ordered, items);
    }

    private static TableBlock BuildTableBlock(XElement list, out List<Block> liftedBlocks) {
        liftedBlocks = new List<Block>();
        var headers = new List<ParagraphBlock>();
        var rows = new List<List<ParagraphBlock>>();
        var header = list.Element("listheader");
        if (header != null) {
            foreach (var h in header.Elements()) {
                var text = MapInlineNodesToText(h.Nodes());
                headers.Add(new ParagraphBlock(text));
            }
        }
        foreach (var item in list.Elements("item")) {
            var cells = new List<ParagraphBlock>();
            foreach (var c in item.Elements()) {
                if (HasBlockLevelContent(c.Nodes())) {
                    // markdownStrict: keep table simple, lift block-level content after the table
                    cells.Add(new ParagraphBlock(string.Empty));
                    var lifted = MapNodesToBlocks(c.Nodes());
                    if (lifted.Count > 0) {
                        liftedBlocks.AddRange(lifted);
                    }
                } else {
                    var t = MapInlineNodesToText(c.Nodes());
                    cells.Add(new ParagraphBlock(t));
                }
            }
            if (cells.Count > 0) {
                rows.Add(cells);
            }
        }
        return new TableBlock(headers, rows);
    }

    private static bool HasBlockLevelContent(IEnumerable<XNode> nodes) {
        foreach (var n in nodes) {
            if (n is XElement el) {
                var name = el.Name.LocalName.ToLowerInvariant();
                if (name == "code" || name == "list" || name == "para") {
                    return true;
                }

                if (HasBlockLevelContent(el.Nodes())) {
                    return true;
                }
            }
        }
        return false;
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
                } else if (name == "see" || name == "seealso") {
                    var lang = e.Attribute("langword")?.Value;
                    if (!string.IsNullOrEmpty(lang)) {
                        AppendText(lines, LangwordToDisplay(lang!));
                    } else {
                        var cref = e.Attribute("cref")?.Value;
                        var disp = CrefToDisplay(cref);
                        if (!string.IsNullOrEmpty(disp)) {
                            AppendText(lines, "[" + disp + "]");
                        }
                    }
                } else if (name == "typeparamref") {
                    var nm = e.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(nm)) {
                        AppendText(lines, "[" + nm + "]");
                    }
                } else if (name == "paramref") {
                    var nm = e.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(nm)) {
                        AppendText(lines, "[" + nm + "]");
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
                break;
        }
    }

    private static void AppendText(List<string> lines, string text) {
        if (string.IsNullOrEmpty(text)) {
            return;
        }

        if (lines.Count == 0) {
            lines.Add(string.Empty);
        }
        // Decode HTML entities early so all downstream formatters share the same normalized text
        var decoded = WebUtility.HtmlDecode(text);
        var parts = decoded.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < parts.Length; i++) {
            if (i > 0) {
                NewLine(lines);
            }

            lines[^1] += parts[i];
        }
    }

    private static void NewLine(List<string> lines) {
        if (lines.Count == 0 || lines[^1].Length > 0) {
            lines.Add(string.Empty);
        } else {
            lines.Add(string.Empty);
        }
    }

    public static string CrefToDisplay(string? cref) {
        if (string.IsNullOrEmpty(cref)) {
            return string.Empty;
        }

        var s = cref;
        int colon = s.IndexOf(':');
        if (colon >= 0 && colon + 1 < s.Length) {
            s = s[(colon + 1)..];
        }

        s = RxGenericArity().Replace(s, "<T>");
        int paren = s.IndexOf('(');
        if (paren >= 0) {
            var namePart = s.Substring(0, paren);
            var paramPart = s.Substring(paren + 1).TrimEnd(')');
            var simpleName = namePart.Contains('.') ? namePart[(namePart.LastIndexOf('.') + 1)..] : namePart;
            var paramNames = new List<string>();
            foreach (var p in paramPart.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                var t = p.Trim();
                var last = t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t;
                last = MapToCSharpKeyword(t) ?? MapToCSharpKeyword(last) ?? last;
                paramNames.Add(last);
            }
            var inside = string.Join(", ", paramNames);
            return simpleName + "(" + inside + ")";
        }
        var mapped = MapToCSharpKeyword(s);
        if (mapped != null) {
            return mapped;
        }

        var lastOnly = s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..] : s;
        mapped = MapToCSharpKeyword(lastOnly);
        return mapped ?? s;
    }

    private static string? MapToCSharpKeyword(string typeName) => typeName switch {
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

    private static string LangwordToDisplay(string word) => word switch {
        "true" => "true",
        "false" => "false",
        "null" => "null",
        _ => word
    };

    [GeneratedRegex(@"`[0-9]+")]
    private static partial Regex RxGenericArity();
}

