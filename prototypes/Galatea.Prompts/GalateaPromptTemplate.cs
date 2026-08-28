using System.Text;

namespace Atelia.Galatea.Prompts;

/// <summary>
/// Renders Galatea's closed, non-recursive prompt-template language.
/// </summary>
public static class GalateaPromptTemplate {
    public const string CharacterNameToken = "${characterName}";
    public const string PlayerNameToken = "${playerName}";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );
    private static readonly int CharacterNameTokenUtf8Bytes =
        StrictUtf8.GetByteCount(CharacterNameToken);
    private static readonly int PlayerNameTokenUtf8Bytes =
        StrictUtf8.GetByteCount(PlayerNameToken);

    public static string Render(
        string source,
        GalateaCharacterName characterName,
        int maximumUtf8Bytes
    ) => RenderCore(
        source,
        characterName,
        playerName: null,
        maximumUtf8Bytes
    );

    public static string Render(
        string source,
        GalateaCharacterName characterName,
        GalateaPlayerName playerName,
        int maximumUtf8Bytes
    ) {
        ArgumentNullException.ThrowIfNull(playerName);
        return RenderCore(
            source,
            characterName,
            playerName,
            maximumUtf8Bytes
        );
    }

    private static string RenderCore(
        string source,
        GalateaCharacterName characterName,
        GalateaPlayerName? playerName,
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

        (int characterTokenCount, int playerTokenCount) =
            CountAndValidateTokens(source, playerName is not null);
        long renderedUtf8Bytes = sourceUtf8Bytes
            + (long)characterTokenCount
                * (characterName.Utf8ByteCount
                    - CharacterNameTokenUtf8Bytes)
            + (playerName is null
                ? 0L
                : (long)playerTokenCount
                    * (playerName.Utf8ByteCount
                        - PlayerNameTokenUtf8Bytes));
        if (renderedUtf8Bytes > maximumUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                $"Rendered prompt exceeds {maximumUtf8Bytes} UTF-8 bytes."
            );
        }
        string rendered = source.Replace(
            CharacterNameToken,
            characterName.Value,
            StringComparison.Ordinal
        );
        return playerName is null
            ? rendered
            : rendered.Replace(
                PlayerNameToken,
                playerName.Value,
                StringComparison.Ordinal
            );
    }

    private static (int Character, int Player) CountAndValidateTokens(
        string source,
        bool allowPlayerName
    ) {
        int characterCount = 0;
        int playerCount = 0;
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
                if (allowPlayerName
                    && source.AsSpan(opener).StartsWith(
                        PlayerNameToken,
                        StringComparison.Ordinal)) {
                    playerCount++;
                    searchStart = opener + PlayerNameToken.Length;
                    continue;
                }
                throw new ArgumentException(
                    "Prompt template contains an unknown or malformed token.",
                    nameof(source)
                );
            }
            characterCount++;
            searchStart = opener + CharacterNameToken.Length;
        }
        if (characterCount == 0) {
            throw new ArgumentException(
                $"Prompt template must contain at least one exact {CharacterNameToken} token.",
                nameof(source)
            );
        }
        return (characterCount, playerCount);
    }
}
