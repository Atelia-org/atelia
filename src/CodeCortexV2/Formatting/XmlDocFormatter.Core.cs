using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Atelia.Diagnostics;

namespace CodeCortexV2.Formatting;
// Facade: XmlDocFormatter (partial)
// - Public API: GetSummaryLines / BuildSummaryMarkdown / BuildDetailedMemberMarkdown / BuildMemberBlocks.
// - Orchestration only: Extractor (TryGetXDocument) + Parser (BuildBlocksFromDocument) + Layout/Render via MarkdownLayout.
// - Fallback behavior: when no XDocument is available, produce summary-only blocks/markdown.
// - Do NOT: embed parsing logic or layout policy here; keep pipeline stages isolated for maintainability.


internal static partial class XmlDocFormatter {
    public static List<string> GetSummaryLines(ISymbol symbol) {
        var xml = symbol.GetDocumentationCommentXml() ?? string.Empty;
        var lines = ExtractSummaryLinesFromXml(xml);
        if (lines.Count == 0) {
            lines = TryExtractSummaryLinesFromTrivia(symbol);
        }
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
        TrimTrailingEmpty(list);
        return list;
    }


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


    public static string BuildSummaryMarkdown(ISymbol symbol) {
        var lines = GetSummaryLines(symbol);
        if (lines.Count == 0) {
            return string.Empty;
        }

        var sb = new StringBuilder();
        MarkdownRenderer.RenderLinesWithStructure(sb, lines, indent: string.Empty, bulletizePlain: false, startIndex: 0, insertBlankBeforeTable: false);
        return sb.ToString().TrimEnd();
    }

    public static string BuildDetailedMemberMarkdown(ISymbol symbol) {
        var blocks = BuildMemberBlocks(symbol);
        // Default external rendering uses Final mode with ATX headers; base level 3 works well inside outline lists
        return MarkdownLayout.RenderBlocksToMarkdown(blocks, indent: string.Empty, mode: RenderMode.Final, baseHeadingLevel: 3);
    }

    public static List<Block> BuildMemberBlocks(ISymbol symbol) {
        var blocks = new List<Block>();

        var doc = TryGetXDocument(symbol);
        if (doc is null) {
            // Fallback to summary-only lines when XML doc is not available
            var summaryLines = GetSummaryLines(symbol);
            if (summaryLines.Count > 0) {
                var paras = summaryLines
                    .Select(l => l.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => (Block)new ParagraphBlock(t))
                    .ToList();
                if (paras.Count > 0) {
                    blocks.Add(new SectionBlock("Summary", new SequenceBlock(paras)));
                }
            }
            return blocks;
        }

        // Build full block tree from document
        blocks.AddRange(BuildBlocksFromDocument(doc));
        try {
            var symName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var preview = JsonFormatter.RenderBlocksToJson(blocks, indented: false);
            if (preview.Length > 400) {
                preview = preview.Substring(0, 400) + "...";
            }

            DebugUtil.Print("XmlDocPipeline", $"Built {blocks.Count} blocks for {symName}: {preview}");
        } catch { /* logging must not break pipeline */ }
        return blocks;
    }











}

