using System.Text;

namespace Atelia.Galatea.Prompts;

/// <summary>
/// Renders Galatea's one-variable, non-recursive character prompt template.
/// </summary>
public static class GalateaPromptTemplate {
    public const string CharacterNameToken = "${characterName}";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );
    private static readonly int CharacterNameTokenUtf8Bytes =
        StrictUtf8.GetByteCount(CharacterNameToken);

    public static string Render(
        string source,
        GalateaCharacterName characterName,
        int maximumUtf8Bytes
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(characterName);
        if (maximumUtf8Bytes < 1) {
            throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        }

        int sourceUtf8Bytes;
        try {
            sourceUtf8Bytes = StrictUtf8.GetByteCount(source);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Prompt template contains invalid UTF-16.",
                nameof(source),
                exception
            );
        }
        if (sourceUtf8Bytes > maximumUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                $"Prompt template exceeds {maximumUtf8Bytes} UTF-8 bytes."
            );
        }

        int tokenCount = CountAndValidateTokens(source);
        long renderedUtf8Bytes = sourceUtf8Bytes
            + (long)tokenCount
                * (characterName.Utf8ByteCount
                    - CharacterNameTokenUtf8Bytes);
        if (renderedUtf8Bytes > maximumUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                $"Rendered prompt exceeds {maximumUtf8Bytes} UTF-8 bytes."
            );
        }
        return source.Replace(
            CharacterNameToken,
            characterName.Value,
            StringComparison.Ordinal
        );
    }

    private static int CountAndValidateTokens(string source) {
        int count = 0;
        int searchStart = 0;
        while (true) {
            int opener = source.IndexOf("${", searchStart,
                StringComparison.Ordinal);
            if (opener < 0) {
                break;
            }
            if (!source.AsSpan(opener).StartsWith(
                    CharacterNameToken,
                    StringComparison.Ordinal)) {
                throw new ArgumentException(
                    "Prompt template contains an unknown or malformed token.",
                    nameof(source)
                );
            }
            count++;
            searchStart = opener + CharacterNameToken.Length;
        }
        if (count == 0) {
            throw new ArgumentException(
                $"Prompt template must contain at least one exact {CharacterNameToken} token.",
                nameof(source)
            );
        }
        return count;
    }
}
