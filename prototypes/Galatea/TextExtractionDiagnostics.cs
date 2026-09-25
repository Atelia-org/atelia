using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Diagnostics;

namespace Atelia.Galatea.Server;

/// <summary>
/// Correlates one extraction attempt with its terminal Action. The attempt ID is
/// diagnostic only; it is never a durable capture or dispatch identity.
/// </summary>
internal sealed record TextExtractionSource(
    string CharacterId,
    string SourceAction,
    string AttemptId
) {
    internal static TextExtractionSource Create(
        string characterId,
        string sourceAction
    ) => new(
        characterId,
        sourceAction,
#if DEBUG
        Guid.NewGuid().ToString("N")
#else
        string.Empty
#endif
    );
}

/// <summary>
/// Best-effort DEBUG diagnostics shared by mail and Note extraction. Each event
/// is one JSON line with a stable attempt identity and a stage-specific payload.
/// Diagnostic failures must not affect provider calls, validation, or capture.
/// </summary>
internal sealed class TextExtractionTrace {
    private const string Category = "Galatea.TextExtraction";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Action<string>? _sinkForTest;

    private TextExtractionTrace(
        string feature,
        string? contractId,
        string? characterName,
        TextExtractionSource? source,
        string targetText,
        Action<string>? sinkForTest
    ) {
        Feature = feature;
        ContractId = contractId;
        CharacterName = characterName;
        Source = source;
        AttemptId = source?.AttemptId
#if DEBUG
            ?? Guid.NewGuid().ToString("N");
#else
            ?? string.Empty;
#endif
        _sinkForTest = sinkForTest;
#if DEBUG
        try {
            byte[] utf8 = StrictUtf8.GetBytes(targetText);
            TargetUtf8Bytes = utf8.Length;
            TargetSha256 = Convert.ToHexString(SHA256.HashData(utf8))
                .ToLowerInvariant();
        }
        catch (EncoderFallbackException) {
            // The extractor reports invalid input through its own validation.
            TargetUtf8Bytes = null;
            TargetSha256 = null;
        }
#endif
    }

    internal string Feature { get; }
    internal string? ContractId { get; }
    internal string? CharacterName { get; }
    internal TextExtractionSource? Source { get; }
    internal string AttemptId { get; }
    internal int? TargetUtf8Bytes { get; }
    internal string? TargetSha256 { get; }

    internal static TextExtractionTrace Create(
        string feature,
        string? contractId,
        string? characterName,
        TextExtractionSource? source,
        string targetText,
        Action<string>? sinkForTest = null
    ) {
#if DEBUG
        return new(feature, contractId, characterName, source, targetText,
            sinkForTest);
#else
        return Disabled;
#endif
    }

#if !DEBUG
    private static readonly TextExtractionTrace Disabled = new(
        string.Empty, null, null, null, string.Empty, null
    );
#endif

    [Conditional("DEBUG")]
    internal void Emit(string eventName, object details) {
        try {
            string json = JsonSerializer.Serialize(new {
                schema = "atelia.galatea.text-extraction-diagnostic.v1",
                @event = eventName,
                feature = Feature,
                attemptId = AttemptId,
                characterId = Source?.CharacterId,
                characterName = CharacterName,
                sourceAction = Source?.SourceAction,
                extractorContractId = ContractId,
                visibleActionSha256 = TargetSha256,
                visibleActionUtf8Bytes = TargetUtf8Bytes,
                details,
            });
            _sinkForTest?.Invoke(json);
            DebugUtil.Debug(Category, json);
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            // A diagnostic is never an extraction or capture failure.
        }
    }

    internal static string? Preview(string? text, int maximumChars = 4096) =>
        text is null || text.Length <= maximumChars
            ? text
            : text[..maximumChars];

    internal static string? Sha256(string? text) {
        if (text is null) { return null; }
        try {
            return Convert.ToHexString(SHA256.HashData(
                StrictUtf8.GetBytes(text))).ToLowerInvariant();
        }
        catch (EncoderFallbackException) {
            return null;
        }
    }
}
