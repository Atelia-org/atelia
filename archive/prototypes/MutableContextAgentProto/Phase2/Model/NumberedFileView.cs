using System.Text;

namespace Atelia.MutableContextAgentProto.Phase2.Model;

public sealed record NumberedFileView {
    public NumberedFileView(
        string path,
        string intention,
        IReadOnlyList<NumberedLine> lines
    ) {
        if (string.IsNullOrWhiteSpace(path)) { throw new ArgumentException("File path cannot be empty.", nameof(path)); }

        if (string.IsNullOrWhiteSpace(intention)) { throw new ArgumentException("Intention cannot be empty.", nameof(intention)); }

        Path = path;
        Intention = intention;
        Lines = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        for (int i = 0; i < Lines.Count; i++) {
            int expectedNumber = i + 1;
            if (Lines[i].Number != expectedNumber) {
                throw new ArgumentException(
                    $"Lines must be contiguous 1-based virtual lines. Expected line {expectedNumber}, got {Lines[i].Number}.",
                    nameof(lines)
                );
            }
        }
    }

    public string Path { get; }

    public string Intention { get; }

    public IReadOnlyList<NumberedLine> Lines { get; }

    public int LineCount => Lines.Count;

    public string Render() => RenderWithLines(Lines);

    public string RenderWithRanges(IReadOnlyList<LineRange> ranges) {
        ArgumentNullException.ThrowIfNull(ranges);

        HashSet<int> selectedLineNumbers = [];
        foreach (LineRange range in ranges) {
            ValidateRangeWithinView(range);
            for (int lineNumber = range.Start; lineNumber <= range.End; lineNumber++) {
                selectedLineNumbers.Add(lineNumber);
            }
        }

        NumberedLine[] selectedLines = Lines
            .Where(line => selectedLineNumbers.Contains(line.Number))
            .ToArray();

        return RenderWithLines(selectedLines);
    }

    public void ValidateRangeWithinView(LineRange range) {
        ArgumentNullException.ThrowIfNull(range);

        if (range.End > LineCount) {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                range.ToString(),
                $"Line range {range} exceeds view line count {LineCount}."
            );
        }
    }

    public static NumberedFileView FromText(string path, string intention, string text) {
        ArgumentNullException.ThrowIfNull(text);

        List<NumberedLine> lines = [];
        using StringReader reader = new(text);
        int number = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null) {
            lines.Add(new NumberedLine(number, line));
            number++;
        }

        return new NumberedFileView(path, intention, lines);
    }

    private string RenderWithLines(IReadOnlyList<NumberedLine> lines) {
        StringBuilder builder = new();
        builder.AppendLine($"File: {Path}");
        builder.AppendLine($"Intention: {Intention}");
        builder.AppendLine("Line format: <line>|<content>");
        builder.AppendLine();

        for (int i = 0; i < lines.Count; i++) {
            builder.Append(lines[i].Render());
            if (i + 1 < lines.Count) {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }
}
