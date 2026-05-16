using System.Globalization;

namespace Atelia.TextEditScript;

public readonly record struct TextAnchor {
    public TextAnchorKind Kind { get; }
    public uint BlockId { get; }

    private TextAnchor(TextAnchorKind kind, uint blockId) {
        if (kind is TextAnchorKind.BlockId && blockId == 0) {
            throw new ArgumentOutOfRangeException(nameof(blockId), "BlockId anchor must be greater than 0.");
        }

        if (kind is not TextAnchorKind.BlockId && blockId != 0) {
            throw new ArgumentOutOfRangeException(nameof(blockId), "Only BlockId anchors may carry a numeric block id.");
        }

        Kind = kind;
        BlockId = blockId;
    }

    public static TextAnchor Head { get; } = new(TextAnchorKind.Head, 0);
    public static TextAnchor Tail { get; } = new(TextAnchorKind.Tail, 0);

    public static TextAnchor ForBlockId(uint blockId) => new(TextAnchorKind.BlockId, blockId);

    public static AteliaResult<TextAnchor> Parse(string rawAnchor) {
        if (string.IsNullOrWhiteSpace(rawAnchor)) {
            return AteliaResult<TextAnchor>.Failure(
                new TextEditScriptParseError(
                    "Anchor cannot be empty.",
                    RecoveryHint: "Use 'head', 'tail', or a decimal DurableText block id such as '123'."));
        }

        var normalized = rawAnchor.Trim();
        if (string.Equals(normalized, "head", StringComparison.OrdinalIgnoreCase)) {
            return Head;
        }

        if (string.Equals(normalized, "tail", StringComparison.OrdinalIgnoreCase)) {
            return Tail;
        }

        if (!uint.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var blockId) || blockId == 0) {
            return AteliaResult<TextAnchor>.Failure(
                new TextEditScriptParseError(
                    $"Invalid anchor '{rawAnchor}'.",
                    RecoveryHint: "Use 'head', 'tail', or a decimal block id greater than 0."));
        }

        return ForBlockId(blockId);
    }

    public override string ToString() => Kind switch {
        TextAnchorKind.Head => "head",
        TextAnchorKind.Tail => "tail",
        TextAnchorKind.BlockId => BlockId.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Unknown {nameof(TextAnchorKind)}: {Kind}")
    };
}
