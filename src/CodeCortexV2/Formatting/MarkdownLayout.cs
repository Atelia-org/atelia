using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using Microsoft.CodeAnalysis;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Formatting;
// Layer: Layout (Markdown)
// - Responsibility: render Block tree to Markdown with indentation and blank-line policy only.
// - SectionBlock: heading is immediately followed by its child blocks (no blank line inserted between).
// - General rule: insert a single blank line between heterogeneous blocks for readability.
// - Do NOT: decode HTML entities or parse XML; only formatting decisions live here.



public static class MarkdownLayout {
    public static string RenderTypeOutline(TypeOutline outline) {
        var sb = new StringBuilder();
        // Title
        sb.AppendLine($"# {outline.Name}");
        // Type summary (already includes metadata header + summary)
        if (!string.IsNullOrWhiteSpace(outline.Summary)) {
            sb.AppendLine(outline.Summary.TrimEnd());
            sb.AppendLine();
        }
        // Members as sections: Signature as heading, XmlDoc as body
        for (int i = 0; i < outline.Members.Count; i++) {
            MemberOutline m = outline.Members[i];
            sb.AppendLine($"## `{m.Signature}`");
            if (!string.IsNullOrWhiteSpace(m.Summary)) {
                sb.AppendLine(m.Summary.TrimEnd());
            }
            if (i < outline.Members.Count - 1) {
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    // New unified renderer for SymbolOutline (hierarchical)
    public static string RenderSymbolOutline(SymbolOutline outline, int baseHeadingLevel = 2, int maxAtxLevel = 4) {
        var sb = new StringBuilder();
        // Root title by name
        sb.AppendLine($"# {outline.Name}");
        // Render root signature/doc at first member level
        RenderSymbolNode(sb, outline, baseHeadingLevel, maxAtxLevel);
        return sb.ToString().TrimEnd();
    }

    private static void RenderSymbolNode(StringBuilder sb, SymbolOutline node, int level, int maxAtxLevel) {
        if (!string.IsNullOrWhiteSpace(node.Signature)) {
            int lv = Math.Clamp(level, 1, maxAtxLevel);
            sb.AppendLine(new string('#', lv) + " `" + node.Signature + "`");
        }
        // Compose metadata block (if any) + XmlDoc blocks
        var blocks = new List<Block>();
        if (!string.IsNullOrEmpty(node.Metadata.Fqn) || !string.IsNullOrEmpty(node.Metadata.DocId) || !string.IsNullOrEmpty(node.Metadata.Assembly) || !string.IsNullOrEmpty(node.Metadata.FilePath)) {
            blocks.Add(BuildMetadataBlock(node));
        }
        if (node.XmlDocBlocks != null && node.XmlDocBlocks.Count > 0) {
            blocks.AddRange(node.XmlDocBlocks);
        }
        if (blocks.Count > 0) {
            // Doc sections appear under the signature heading
            RenderBlocks(sb, blocks, indent: string.Empty, baseHeadingLevel: level + 1, maxAtxLevel: maxAtxLevel, depth: 0);
        }
        // Children
        for (int i = 0; i < node.Members.Count; i++) {
            sb.AppendLine();
            RenderSymbolNode(sb, node.Members[i], level + 1, maxAtxLevel);
        }
    }

    private static SectionBlock BuildMetadataBlock(SymbolOutline node) {
        string header = node.Kind switch {
            SymbolKinds.Namespace => "Namespace Metadata",
            SymbolKinds.Type => "Type Metadata",
            _ => "Symbol Metadata"
        };
        var inner = new List<Block>();
        void AddKV(string key, string? val) {
            if (!string.IsNullOrWhiteSpace(val)) {
                inner.Add(new SectionBlock(key, new SequenceBlock(new List<Block> { new ParagraphBlock(val!) })));
            }
        }
        AddKV("FQN", node.Metadata.Fqn);
        AddKV("DocId", node.Metadata.DocId);
        AddKV("Assembly", node.Metadata.Assembly);
        if (!string.IsNullOrWhiteSpace(node.Metadata.FilePath)) {
            inner.Add(new SectionBlock("File", new SequenceBlock(new List<Block> { new ParagraphBlock(System.IO.Path.GetFileName(node.Metadata.FilePath!)) })));
        }
        return new SectionBlock(header, new SequenceBlock(inner));
    }

    public static string RenderBlocksToMarkdown(List<Block> blocks, string indent = "") {
        return RenderBlocksToMarkdown(blocks, indent, baseHeadingLevel: 2, maxAtxLevel: 3);
    }

    public static string RenderBlocksToMarkdown(List<Block> blocks, string indent, int baseHeadingLevel, int maxAtxLevel) {
        var sb = new StringBuilder();
        RenderBlocks(sb, blocks, indent, baseHeadingLevel, maxAtxLevel, depth: 0);
        return sb.ToString().TrimEnd();
    }

    // Public facade for CLI to generate blocks without exposing internal XmlDocFormatter
    public static List<Block> BuildMemberBlocks(ISymbol symbol) {
        return XmlDocFormatter.BuildMemberBlocks(symbol);
    }

    private static void RenderBlocks(StringBuilder sb, List<Block> blocks, string indent, int baseHeadingLevel, int maxAtxLevel, int depth) {
        string D(string? s) => s ?? string.Empty;
        for (int i = 0; i < blocks.Count; i++) {
            switch (blocks[i]) {
                case ParagraphBlock p:
                    sb.AppendLine(indent + D(p.Text));
                    break;
                case CodeBlock code:
                    var fence = "```";
                    if (!string.IsNullOrEmpty(code.Language)) {
                        sb.AppendLine(indent + fence + code.Language);
                    }
                    else {
                        sb.AppendLine(indent + fence);
                    }

                    foreach (var ln in (code.Text ?? string.Empty).Replace("\r\n", "\n").Split('\n')) {
                        sb.AppendLine(indent + ln);
                    }

                    sb.AppendLine(indent + fence);
                    break;
                case SequenceBlock seq:
                    RenderBlocks(sb, seq.Children, indent, baseHeadingLevel, maxAtxLevel, depth);
                    break;
                case ListBlock list:
                    if (list.Ordered) {
                        for (int idx = 0; idx < list.Items.Count; idx++) {
                            RenderListItem(sb, indent, isOrdered: true, index: idx + 1, list.Items[idx], baseHeadingLevel, maxAtxLevel, depth);
                        }
                    }
                    else {
                        for (int idx = 0; idx < list.Items.Count; idx++) {
                            RenderListItem(sb, indent, isOrdered: false, index: idx + 1, list.Items[idx], baseHeadingLevel, maxAtxLevel, depth);
                        }
                    }
                    break;
                case TableBlock table:
                    if (table.Headers.Count > 0) {
                        sb.AppendLine(indent + "| " + string.Join(" | ", table.Headers.Select(h => D(h.Text))) + " |");
                        sb.AppendLine(indent + "|" + string.Join("|", table.Headers.Select(_ => "---")) + "|");
                    }
                    foreach (var row in table.Rows) {
                        sb.AppendLine(indent + "| " + string.Join(" | ", row.Select(c => D(c.Text))) + " |");
                    }
                    break;
                case SectionBlock sec:
                    var heading = D(sec.Heading?.TrimEnd(':'));
                    int level = Math.Max(1, baseHeadingLevel + depth);
                    if (level <= maxAtxLevel) {
                        sb.AppendLine(indent + new string('#', level) + " " + heading);
                    }
                    else {
                        sb.AppendLine(indent + heading + ":");
                    }
                    if (sec.Body.Children.Count > 0) {
                        RenderBlocks(sb, sec.Body.Children, indent + "  ", baseHeadingLevel, maxAtxLevel, depth + 1);
                    }
                    break;
            }
            // add blank line between heterogeneous blocks for readability, but not after last
            if (i < blocks.Count - 1) {
                Block cur = blocks[i];
                Block next = blocks[i + 1];
                bool bothLists = cur is ListBlock && next is ListBlock;
                if (!bothLists) {
                    sb.AppendLine();
                }
            }
        }
    }

    private static void RenderListItem(StringBuilder sb, string indent, bool isOrdered, int index, SequenceBlock content, int baseHeadingLevel, int maxAtxLevel, int depth) {
        // If first child is a single paragraph, render tight: "- text"; render remaining children indented
        string marker = isOrdered ? ($"{index}. ") : "- ";
        if (content.Children.Count == 0) {
            sb.AppendLine(indent + marker.TrimEnd());
            return;
        }
        if (content.Children[0] is ParagraphBlock p0) {
            sb.AppendLine(indent + marker + (p0.Text ?? string.Empty));
            if (content.Children.Count > 1) {
                RenderBlocks(sb, content.Children.Skip(1).ToList(), indent + "  ", baseHeadingLevel, maxAtxLevel, depth + 1);
            }
        }
        else {
            sb.AppendLine(indent + marker.TrimEnd());
            RenderBlocks(sb, content.Children, indent + "  ", baseHeadingLevel, maxAtxLevel, depth + 1);
        }
    }
}
