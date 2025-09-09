using System.Text.Json;

namespace CodeCortexV2.Formatting;

/// <summary>
/// Minimal JSON formatter for the Block tree, intended for downstream renderers or tooling.
/// Keeps a plain structure to ease consumption from other languages.
/// </summary>
// Layer: Formatter (JSON)
// - Responsibility: serialize Block tree into a plain, stable JSON shape for tests/tools.
// - Do NOT: mutate blocks, re-compute semantics, or perform layout-specific transformations.
// - Stability: field names kept minimal and stable; consumers should not rely on incidental ordering beyond arrays.

public static class JsonFormatter {
    public static string RenderBlocksToJson(IReadOnlyList<Block> blocks, bool indented = true) {
        var list = new List<Dictionary<string, object?>>();
        foreach (var b in blocks) {
            list.Add(ToNode(b));
        }
        var opts = new JsonSerializerOptions { WriteIndented = indented };
        return JsonSerializer.Serialize(list, opts);
    }

    private static Dictionary<string, object?> ToNode(Block b) => b switch {
        ParagraphBlock p => new() {
            ["type"] = "paragraph",
            ["text"] = p.Text
        },
        CodeBlock c => new() {
            ["type"] = "code",
            ["text"] = c.Text,
            ["language"] = c.Language
        },
        SequenceBlock seq => new() {
            ["type"] = "sequence",
            ["children"] = seq.Children.Select(ToNode).ToList()
        },
        ListBlock lb => new() {
            ["type"] = lb.Ordered ? "list-ordered" : "list-bullet",
            ["items"] = lb.Items.Select(item => item.Children.Select(ToNode).ToList()).ToList()
        },
        TableBlock tb => new() {
            ["type"] = "table",
            ["headers"] = tb.Headers.Select(h => h.Text).ToList(),
            ["rows"] = tb.Rows.Select(r => r.Select(c => c.Text).ToList()).ToList()
        },
        SectionBlock sb => new() {
            ["type"] = "section",
            ["heading"] = sb.Heading,
            ["body"] = ToNode(sb.Body)
        },
        _ => new() {
            ["type"] = "unknown"
        }
    };
}

