namespace Atelia.TextEditScript;

public sealed record TextBlockSnapshotDocument {
    private static readonly IReadOnlyList<TextBlockSnapshot> s_emptyBlocks = Array.AsReadOnly(Array.Empty<TextBlockSnapshot>());

    public TextBlockSnapshotDocument(IReadOnlyList<TextBlockSnapshot> blocks) {
        ArgumentNullException.ThrowIfNull(blocks);
        Blocks = Freeze(blocks);
    }

    public static TextBlockSnapshotDocument Empty { get; } = new([]);

    public IReadOnlyList<TextBlockSnapshot> Blocks { get; }

    private static IReadOnlyList<TextBlockSnapshot> Freeze(IReadOnlyList<TextBlockSnapshot> blocks)
        => blocks.Count == 0 ? s_emptyBlocks : Array.AsReadOnly(blocks.ToArray());
}
