using System.Xml.Linq;

namespace CodeCortexV2.Formatting;

internal static partial class XmlDocFormatter {
    private static void RenderList(XElement list, List<string> lines, int level) {
        var type = (list.Attribute("type")?.Value ?? "bullet").ToLowerInvariant();
        if (type == "table") {
            RenderTableList(list, lines, level);
            return;
        }
        var items = list.Elements("item").ToList();
        for (int i = 0; i < items.Count; i++) {
            var item = items[i];
            string content;
            var term = item.Element("term")?.Value?.Trim();
            var desc = item.Element("description")?.Value?.Trim();
            if (!string.IsNullOrEmpty(term) && !string.IsNullOrEmpty(desc)) { content = term + " — " + desc; }
            else if (!string.IsNullOrEmpty(desc)) { content = desc!; }
            else if (!string.IsNullOrEmpty(term)) { content = term!; }
            else {
                var tmp = new List<string>();
                foreach (var n in item.Nodes()) { AppendNodeText(n, tmp, level); }
                TrimLeadingEmpty(tmp);
                TrimTrailingEmpty(tmp);
                var pieces = tmp.Select(t => t.Trim()).Where(t => t.Length > 0);
                content = string.Join(" ", pieces);
            }
            var indent = new string(' ', level * 2);
            var prefix = type == "number" ? ($"{i + 1}. ") : "- ";
            lines.Add(indent + prefix + content);
            foreach (var childList in item.Elements("list")) { RenderList(childList, lines, level + 1); }
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
                if (!string.IsNullOrEmpty(d)) { cells.Add(d!); }
            }
            if (cells.Count > 0) { lines.Add(indent + "| " + string.Join(" | ", cells) + " |"); }
        }
    }

    public static void TrimTrailingEmpty(List<string> list) {
        while (list.Count > 0 && string.IsNullOrWhiteSpace(list[^1])) { list.RemoveAt(list.Count - 1); }
    }

    public static void TrimLeadingEmpty(List<string> list) {
        while (list.Count > 0 && string.IsNullOrWhiteSpace(list[0])) { list.RemoveAt(0); }
    }
}

