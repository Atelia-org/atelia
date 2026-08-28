using System.Globalization;
using System.Text;

namespace Atelia.Galatea.Prompts;

/// <summary>
/// A canonical, single-line character label that is safe to place inside
/// Galatea's exact voice-marker grammar.
/// </summary>
public sealed record GalateaCharacterName {
    public const int MaximumUtf8Bytes = 128;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    public GalateaCharacterName(string value) {
        ArgumentNullException.ThrowIfNull(value);
        int utf8Bytes;
        try {
            utf8Bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Character name contains invalid UTF-16.",
                nameof(value),
                exception
            );
        }
        if (utf8Bytes is < 1 or > MaximumUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Character name must contain between 1 and {MaximumUtf8Bytes} UTF-8 bytes."
            );
        }
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)) {
            throw new ArgumentException(
                "Character name must already be trimmed.",
                nameof(value)
            );
        }
        if (!value.IsNormalized(NormalizationForm.FormC)) {
            throw new ArgumentException(
                "Character name must already use Unicode NFC.",
                nameof(value)
            );
        }
        foreach (Rune rune in value.EnumerateRunes()) {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator
                || rune.Value is '[' or ']') {
                throw new ArgumentException(
                    "Character name must be a single-line label without control characters or voice-marker delimiters.",
                    nameof(value)
                );
            }
        }
        if (string.Equals(value, "旁白", StringComparison.Ordinal)
            || string.Equals(value, "状态摘要", StringComparison.Ordinal)) {
            throw new ArgumentException(
                "Character name conflicts with a reserved Galatea output marker.",
                nameof(value)
            );
        }
        Value = value;
        Utf8ByteCount = utf8Bytes;
    }

    public string Value { get; }

    public int Utf8ByteCount { get; }

    public override string ToString() => Value;
}
