using System.Text;

namespace CodeCortexV2.Formatting;

internal static partial class MarkdownRenderer {
    public static void RenderLinesWithStructure(StringBuilder sb, List<string> lines, string indent, bool bulletizePlain, int startIndex = 0) {
        if (lines == null || lines.Count == 0) {
            return;
        }

        bool emittedSomething = false;
        bool lastWasTable = false;
        for (int i = startIndex; i < lines.Count; i++) {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) {
                continue;
            }

            var tr = raw.TrimStart();
            bool isTable = RxTableLine().IsMatch(tr) && HasStructuralPayload(tr);
            // Always ensure a blank line before a table when the previous emitted block wasn't a table
            if (isTable && emittedSomething && !lastWasTable) {
                sb.AppendLine();
            }
            if (IsStructuralLine(tr)) {
                if (!HasStructuralPayload(tr)) {
                    continue;
                }

                sb.AppendLine(indent + raw);
                emittedSomething = true;
                lastWasTable = isTable;
            } else {
                var text = raw.Trim();
                if (text.Length == 0) {
                    continue;
                }

                sb.AppendLine(bulletizePlain ? (indent + "- " + text) : (indent + text));
                emittedSomething = true;
                lastWasTable = false;
            }
        }
    }

    public static bool IsStructuralLine(string line) {
        if (string.IsNullOrEmpty(line)) {
            return false;
        }

        var tr = line.TrimStart();
        return RxBulletLine().IsMatch(tr) || RxOrderedLine().IsMatch(tr) || RxTableLine().IsMatch(tr);
    }

    public static bool HasStructuralPayload(string line) {
        var tr = line.TrimStart();
        if (RxBulletLine().IsMatch(tr)) {
            var payload = RxBulletPrefix().Replace(tr, string.Empty);
            return payload.Trim().Length > 0;
        }
        if (RxOrderedLine().IsMatch(tr)) {
            var payload = RxOrderedPrefix().Replace(tr, string.Empty);
            return payload.Trim().Length > 0;
        }
        if (RxTableLine().IsMatch(tr)) {
            return RxTableHasPayload().IsMatch(tr);
        }
        return line.Trim().Length > 0;
    }

    public static string InlineParagraph(List<string> lines) {
        var sb = new StringBuilder();
        foreach (var l in lines) {
            var t = l.Trim();
            if (t.Length == 0) {
                continue;
            }

            if (sb.Length > 0) {
                sb.Append(' ');
            }

            sb.Append(t);
        }
        return sb.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*-\s+")]
    private static partial System.Text.RegularExpressions.Regex RxBulletLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\d+[.)]\s+")]
    private static partial System.Text.RegularExpressions.Regex RxOrderedLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\|")]
    private static partial System.Text.RegularExpressions.Regex RxTableLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*-\s+")]
    private static partial System.Text.RegularExpressions.Regex RxBulletPrefix();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\d+[.)]\s+")]
    private static partial System.Text.RegularExpressions.Regex RxOrderedPrefix();

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\|\s]")]
    private static partial System.Text.RegularExpressions.Regex RxTableHasPayload();
}

