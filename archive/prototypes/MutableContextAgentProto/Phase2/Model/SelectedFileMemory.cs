namespace Atelia.MutableContextAgentProto.Phase2.Model;

public sealed record SelectedFileMemory {
    public SelectedFileMemory(
        string path,
        string intention,
        IReadOnlyList<LineRange> ranges,
        string summary,
        string? notes
    ) {
        if (string.IsNullOrWhiteSpace(path)) { throw new ArgumentException("File path cannot be empty.", nameof(path)); }

        if (string.IsNullOrWhiteSpace(intention)) { throw new ArgumentException("Intention cannot be empty.", nameof(intention)); }

        Ranges = LineRange.NormalizeAndMerge(ranges ?? throw new ArgumentNullException(nameof(ranges)));
        if (Ranges.Count == 0) { throw new ArgumentException("At least one selected line range is required.", nameof(ranges)); }

        if (string.IsNullOrWhiteSpace(summary)) { throw new ArgumentException("Summary cannot be empty.", nameof(summary)); }

        Path = path;
        Intention = intention;
        Summary = summary.Trim();
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    public string Path { get; }

    public string Intention { get; }

    public IReadOnlyList<LineRange> Ranges { get; }

    public string Summary { get; }

    public string? Notes { get; }
}
