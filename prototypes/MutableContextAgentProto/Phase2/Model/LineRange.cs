namespace Atelia.MutableContextAgentProto.Phase2.Model;

public sealed record LineRange : IComparable<LineRange> {
    public LineRange(int start, int end) {
        if (start < 1) { throw new ArgumentOutOfRangeException(nameof(start), start, "Line range start must be 1 or greater."); }

        if (end < 1) { throw new ArgumentOutOfRangeException(nameof(end), end, "Line range end must be 1 or greater."); }

        if (start > end) { throw new ArgumentException($"Line range start ({start}) must be less than or equal to end ({end})."); }

        Start = start;
        End = end;
    }

    public int Start { get; }

    public int End { get; }

    public int Count => End - Start + 1;

    public bool Contains(int lineNumber) => lineNumber >= Start && lineNumber <= End;

    public int CompareTo(LineRange? other) {
        if (other is null) { return 1; }

        int startComparison = Start.CompareTo(other.Start);
        return startComparison != 0 ? startComparison : End.CompareTo(other.End);
    }

    public override string ToString() => Start == End ? Start.ToString() : $"{Start}-{End}";

    public static LineRange Parse(string text) {
        if (string.IsNullOrWhiteSpace(text)) { throw new ArgumentException("Line range cannot be empty.", nameof(text)); }

        string trimmed = text.Trim();
        string[] parts = trimmed.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2) { throw new FormatException($"Invalid line range '{text}'. Expected '3' or '3-8'."); }

        if (!int.TryParse(parts[0], out int start)) { throw new FormatException($"Invalid line range start in '{text}'. Expected a positive integer."); }

        int end = start;
        if (parts.Length == 2) {
            if (parts[1].Length == 0) { throw new FormatException($"Invalid line range '{text}'. Range end is missing."); }

            if (!int.TryParse(parts[1], out end)) { throw new FormatException($"Invalid line range end in '{text}'. Expected a positive integer."); }
        }

        return new LineRange(start, end);
    }

    public static IReadOnlyList<LineRange> ParseMany(IEnumerable<string> texts) {
        ArgumentNullException.ThrowIfNull(texts);
        return NormalizeAndMerge(texts.Select(Parse));
    }

    public static IReadOnlyList<LineRange> NormalizeAndMerge(IEnumerable<LineRange> ranges) {
        ArgumentNullException.ThrowIfNull(ranges);

        List<LineRange> sorted = ranges.Order().ToList();
        if (sorted.Count == 0) { return []; }

        List<LineRange> merged = [];
        LineRange current = sorted[0];

        for (int i = 1; i < sorted.Count; i++) {
            LineRange next = sorted[i];
            if (next.Start <= current.End + 1) {
                current = new LineRange(current.Start, Math.Max(current.End, next.End));
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        return merged;
    }
}
