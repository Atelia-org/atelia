using System.Text;
using Atelia.MutableContextAgentProto.Phase2.Model;

namespace Atelia.MutableContextAgentProto.Phase2.Tools;

public sealed class SelectRememberToolLogic {
    public SelectRememberResult Select(
        NumberedFileView view,
        IEnumerable<string> ranges,
        string summary,
        string? notes = null
    ) {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(ranges);

        IReadOnlyList<LineRange> parsedRanges = LineRange.ParseMany(ranges);
        return Select(view, parsedRanges, summary, notes);
    }

    public SelectRememberResult Select(
        NumberedFileView view,
        IEnumerable<LineRange> ranges,
        string summary,
        string? notes = null
    ) {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(ranges);

        IReadOnlyList<LineRange> normalizedRanges = LineRange.NormalizeAndMerge(ranges);
        if (normalizedRanges.Count == 0) { throw new ArgumentException("select_remember requires at least one selected line range.", nameof(ranges)); }

        foreach (LineRange range in normalizedRanges) {
            view.ValidateRangeWithinView(range);
        }

        SelectedFileMemory memory = new(
            view.Path,
            view.Intention,
            normalizedRanges,
            summary,
            notes
        );

        string reducedViewText = RenderReducedView(view, memory);
        return new SelectRememberResult(memory, reducedViewText);
    }

    public static string RenderReducedView(NumberedFileView view, SelectedFileMemory memory) {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(memory);

        if (!string.Equals(view.Path, memory.Path, StringComparison.Ordinal)) { throw new ArgumentException("Selected memory path must match the numbered file view path.", nameof(memory)); }

        if (!string.Equals(view.Intention, memory.Intention, StringComparison.Ordinal)) { throw new ArgumentException("Selected memory intention must match the numbered file view intention.", nameof(memory)); }

        foreach (LineRange range in memory.Ranges) {
            view.ValidateRangeWithinView(range);
        }

        StringBuilder builder = new();
        builder.AppendLine($"File: {view.Path}");
        builder.AppendLine($"Intention: {view.Intention}");
        builder.AppendLine("Line format: <line>|<content>");
        builder.AppendLine($"Selected ranges: {FormatRanges(memory.Ranges)}");
        builder.AppendLine($"Omitted ranges: {FormatRanges(ComputeOmittedRanges(view.LineCount, memory.Ranges))}");
        builder.AppendLine($"Summary: {memory.Summary.Trim()}");
        if (!string.IsNullOrWhiteSpace(memory.Notes)) {
            builder.AppendLine($"Notes: {memory.Notes}");
        }

        builder.AppendLine();

        bool wroteAnyLine = false;
        foreach (LineRange range in memory.Ranges) {
            if (wroteAnyLine) {
                builder.AppendLine();
                builder.AppendLine($"... omitted lines before selected range {range} ...");
            }

            builder.AppendLine($"--- selected lines {range} ---");
            for (int lineNumber = range.Start; lineNumber <= range.End; lineNumber++) {
                builder.Append(view.Lines[lineNumber - 1].Render());
                if (lineNumber < range.End) {
                    builder.AppendLine();
                }
            }

            wroteAnyLine = true;
        }

        return builder.ToString();
    }

    public static IReadOnlyList<LineRange> ComputeOmittedRanges(
        int lineCount,
        IEnumerable<LineRange> selectedRanges
    ) {
        if (lineCount < 0) { throw new ArgumentOutOfRangeException(nameof(lineCount), lineCount, "Line count cannot be negative."); }

        IReadOnlyList<LineRange> normalizedSelectedRanges = LineRange.NormalizeAndMerge(selectedRanges);
        List<LineRange> omitted = [];
        int nextLine = 1;

        foreach (LineRange range in normalizedSelectedRanges) {
            if (range.End > lineCount) {
                throw new ArgumentOutOfRangeException(
                    nameof(selectedRanges),
                    range.ToString(),
                    $"Selected range {range} exceeds line count {lineCount}."
                );
            }

            if (nextLine < range.Start) {
                omitted.Add(new LineRange(nextLine, range.Start - 1));
            }

            nextLine = range.End + 1;
        }

        if (nextLine <= lineCount) {
            omitted.Add(new LineRange(nextLine, lineCount));
        }

        return omitted;
    }

    private static string FormatRanges(IReadOnlyList<LineRange> ranges)
        => ranges.Count == 0 ? "(none)" : string.Join(", ", ranges);
}

public sealed record SelectRememberResult(
    SelectedFileMemory Memory,
    string ReducedViewText
);
