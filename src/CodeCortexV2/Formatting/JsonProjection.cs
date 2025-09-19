using System.Collections.Generic;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Formatting;

/// <summary>
/// Projects SymbolOutline and Block trees to plain JSON-friendly objects
/// (Dictionary/List/Primitive) without relying on polymorphic JSON settings.
/// </summary>
public static class JsonProjection {
    public static object ToPlainObject(SymbolOutline outline) {
        return new Dictionary<string, object?> {
            ["kind"] = outline.Kind.ToString(),
            ["name"] = outline.Name,
            ["signature"] = outline.Signature,
            ["metadata"] = new Dictionary<string, object?> {
                ["fqn"] = outline.Metadata.Fqn,
                ["docId"] = outline.Metadata.DocId,
                ["assembly"] = outline.Metadata.Assembly,
                ["filePath"] = outline.Metadata.FilePath,
            },
            ["xmlDoc"] = BlocksToPlainArray(outline.XmlDocBlocks),
            ["members"] = MembersToPlainArray(outline.Members)
        };
    }

    private static List<object?> MembersToPlainArray(IReadOnlyList<SymbolOutline> members) {
        var list = new List<object?>(members.Count);
        for (int i = 0; i < members.Count; i++) {
            list.Add(ToPlainObject(members[i]));
        }
        return list;
    }

    private static List<object?> BlocksToPlainArray(IReadOnlyList<Block> blocks) {
        var list = new List<object?>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++) {
            list.Add(BlockToPlain(blocks[i]));
        }
        return list;
    }

    private static object BlockToPlain(Block b) {
        switch (b) {
            case ParagraphBlock p:
                return new Dictionary<string, object?> {
                    ["kind"] = "paragraph",
                    ["text"] = p.Text
                };
            case CodeBlock c:
                return new Dictionary<string, object?> {
                    ["kind"] = "code",
                    ["language"] = c.Language,
                    ["text"] = c.Text
                };
            case SequenceBlock seq:
                return new Dictionary<string, object?> {
                    ["kind"] = "sequence",
                    ["children"] = BlocksToPlainArray(seq.Children)
                };
            case ListBlock list:
                return new Dictionary<string, object?> {
                    ["kind"] = "list",
                    ["ordered"] = list.Ordered,
                    ["items"] = SequenceItemsToPlainArray(list.Items)
                };
            case TableBlock table:
                return new Dictionary<string, object?> {
                    ["kind"] = "table",
                    ["headers"] = ParagraphsToPlainStrings(table.Headers),
                    ["rows"] = TableRowsToPlainStrings(table.Rows)
                };
            case SectionBlock sec:
                return new Dictionary<string, object?> {
                    ["kind"] = "section",
                    ["heading"] = sec.Heading,
                    ["body"] = BlocksToPlainArray(sec.Body.Children)
                };
            default:
                return new Dictionary<string, object?> {
                    ["kind"] = b.GetType().Name
                };
        }
    }

    private static List<string> ParagraphsToPlainStrings(List<ParagraphBlock> ps) {
        var list = new List<string>(ps.Count);
        for (int i = 0; i < ps.Count; i++) {
            list.Add(ps[i].Text);
        }

        return list;
    }

    private static List<List<string>> TableRowsToPlainStrings(List<List<ParagraphBlock>> rows) {
        var outer = new List<List<string>>(rows.Count);
        for (int i = 0; i < rows.Count; i++) {
            var inner = new List<string>(rows[i].Count);
            for (int j = 0; j < rows[i].Count; j++) {
                inner.Add(rows[i][j].Text);
            }

            outer.Add(inner);
        }
        return outer;
    }

    private static List<object?> SequenceItemsToPlainArray(List<SequenceBlock> items) {
        var arr = new List<object?>(items.Count);
        for (int i = 0; i < items.Count; i++) {
            arr.Add(BlocksToPlainArray(items[i].Children));
        }
        return arr;
    }
}

