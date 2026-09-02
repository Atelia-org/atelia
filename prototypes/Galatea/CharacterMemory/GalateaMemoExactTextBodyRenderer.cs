using System.Text;
using Atelia.MemoPod;

namespace Atelia.Galatea.Server.CharacterMemory;

internal static class GalateaMemoExactTextBodyRenderer {
    internal const string TitlePrefix = "标题：";
    internal const string ExactTextPrefix = "\n\n正文：\n";
    internal const int FixedLabelUtf8Bytes = 21;

    internal static string Render(string title, string exactText) {
        RequireTitle(title);
        RequireExactText(exactText);
        string rendered = string.Concat(
            TitlePrefix,
            title,
            ExactTextPrefix,
            exactText
        );
        if (GalateaBoundedJson.StrictUtf8.GetByteCount(rendered)
                > PlayerTurnObservationEnvelope
                    .MaximumRecallBodyUtf8Bytes) {
            throw new InvalidOperationException(
                "A legal MemoExactText body exceeds the Observation envelope contract."
            );
        }
        return rendered;
    }

    private static void RequireTitle(string? title) {
        ArgumentNullException.ThrowIfNull(title);
        if (string.IsNullOrWhiteSpace(title)
            || !string.Equals(title, title.Trim(), StringComparison.Ordinal)
            || title.Any(char.IsControl)) {
            throw new ArgumentException(
                "Memo recall title must be non-empty, already trimmed, and contain no control characters.",
                nameof(title)
            );
        }
        RequireUtf8Bound(
            title,
            MemoPodLimits.MaximumMemoTitleUtf8Bytes,
            nameof(title)
        );
    }

    private static void RequireExactText(string? exactText) {
        ArgumentNullException.ThrowIfNull(exactText);
        if (string.IsNullOrWhiteSpace(exactText)) {
            throw new ArgumentException(
                "Memo recall exact text must not be blank.",
                nameof(exactText)
            );
        }
        RequireUtf8Bound(
            exactText,
            MemoPodLimits.MaximumMemoExactTextUtf8Bytes,
            nameof(exactText)
        );
    }

    private static void RequireUtf8Bound(
        string value,
        int maximumUtf8Bytes,
        string parameterName
    ) {
        try {
            if (GalateaBoundedJson.StrictUtf8.GetByteCount(value)
                    > maximumUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"Value exceeds {maximumUtf8Bytes} UTF-8 bytes."
                );
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Value must contain valid Unicode.",
                parameterName,
                exception
            );
        }
    }
}
