namespace Atelia.Galatea.Prompts;

/// <summary>
/// A canonical, single-line player-character label for Galatea prompt
/// attribution. It is independent from the authenticated user id.
/// </summary>
public sealed record GalateaPlayerName {
    public const int MaximumUtf8Bytes =
        GalateaPromptNameValidation.MaximumUtf8Bytes;

    public GalateaPlayerName(string value) {
        int utf8ByteCount = GalateaPromptNameValidation.Validate(
            value,
            "Player"
        );
        Value = value;
        Utf8ByteCount = utf8ByteCount;
    }

    public string Value { get; }

    public int Utf8ByteCount { get; }

    public override string ToString() => Value;
}
