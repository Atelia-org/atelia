using System.Collections.Generic;
using System.Text;

namespace Atelia.Agent.Text;

internal static class TextToolUtilities {
    internal static string NormalizeLineEndings(string? value) {
        if (string.IsNullOrEmpty(value)) { return string.Empty; }

        return value
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    internal static string GetContext(string text, int position, int length, int contextSize = 50) {
        var start = Math.Max(0, position - contextSize);
        var end = Math.Min(text.Length, position + length + contextSize);
        var prefix = start > 0 ? "..." : "";
        var suffix = end < text.Length ? "..." : "";
        return $"{prefix}{text.Substring(start, end - start)}{suffix}";
    }

    internal static string FormatMatchesForError(IReadOnlyList<int> positions, string text, int matchLength, int contextSize = 80) {
        if (positions.Count == 0) { return string.Empty; }

        var builder = new StringBuilder();
        for (var i = 0; i < positions.Count; i++) {
            if (i > 0) {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append("Match ");
            builder.Append(i + 1);
            builder.Append(" at position ");
            builder.Append(positions[i]);
            builder.AppendLine(":");
            builder.AppendLine(GetContext(text, positions[i], matchLength, contextSize));
        }

        return builder.ToString();
    }

    internal static AnchorResolution ResolveAnchor(string memory, string? searchAfter) {
        if (searchAfter is null) { return AnchorResolution.NotRequested; }

        if (searchAfter.Length == 0) { return AnchorResolution.FromStart; }

        var index = memory.IndexOf(searchAfter, StringComparison.Ordinal);
        if (index < 0) { return AnchorResolution.Failed($"Error: 找不到 search_after: '{searchAfter}'"); }

        return AnchorResolution.Resolved(index + searchAfter.Length);
    }
}

internal readonly record struct AnchorResolution(
    bool IsRequested,
    bool Success,
    int SearchStart,
    string? ErrorMessage
) {
    public static AnchorResolution NotRequested { get; } = new(false, true, 0, null);
    public static AnchorResolution FromStart { get; } = new(true, true, 0, null);
    public static AnchorResolution Resolved(int searchStart) => new(true, true, searchStart, null);
    public static AnchorResolution Failed(string errorMessage) => new(true, false, -1, errorMessage);
}
