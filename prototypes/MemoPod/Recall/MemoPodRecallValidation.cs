using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.MemoPod;

internal static class MemoPodRecallValidation {
    internal const int MaximumToolArgumentsUtf8Bytes = 8 * 1024;
    internal const int MaximumToolCallIdUtf8Bytes = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static void RequireQuery(string query) {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query)) {
            throw new ArgumentException(
                "MemoPod recall query must not be blank.",
                nameof(query)
            );
        }

        int byteCount;
        try {
            byteCount = StrictUtf8.GetByteCount(query);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "MemoPod recall query contains invalid UTF-16.",
                nameof(query),
                exception
            );
        }
        if (byteCount > MemoPodLimits.MaximumRecallQueryUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                $"MemoPod recall query exceeds {MemoPodLimits.MaximumRecallQueryUtf8Bytes} UTF-8 bytes."
            );
        }
    }

    internal static ImmutableArray<MemoId> ParseMemoIds(
        string? rawArgumentsJson,
        int maxResults
    ) {
        if (rawArgumentsJson is null) {
            throw InvalidOutput("Recall tool arguments must not be null.");
        }

        int byteCount;
        try {
            byteCount = StrictUtf8.GetByteCount(rawArgumentsJson);
        }
        catch (EncoderFallbackException exception) {
            throw InvalidOutput(
                "Recall tool arguments contain invalid UTF-16.",
                exception
            );
        }
        if (byteCount > MaximumToolArgumentsUtf8Bytes) {
            throw InvalidOutput(
                $"Recall tool arguments exceed {MaximumToolArgumentsUtf8Bytes} UTF-8 bytes."
            );
        }

        byte[] utf8 = GC.AllocateUninitializedArray<byte>(byteCount);
        try {
            int written = StrictUtf8.GetBytes(rawArgumentsJson, utf8);
            if (written != utf8.Length) {
                throw new InvalidOperationException(
                    "Recall argument UTF-8 byte pre-count did not match encoding."
                );
            }
        }
        catch (EncoderFallbackException exception) {
            throw InvalidOutput(
                "Recall tool arguments contain invalid UTF-16.",
                exception
            );
        }

        try {
            return ParseUtf8(utf8, maxResults);
        }
        catch (JsonException exception) {
            throw InvalidOutput(
                "Recall tool arguments are not strict JSON.",
                exception
            );
        }
    }

    internal static void RequireToolCallId(string? toolCallId) {
        if (string.IsNullOrWhiteSpace(toolCallId)) {
            throw InvalidOutput(
                "Recall tool-call ID must not be null or blank."
            );
        }

        int byteCount;
        try {
            byteCount = StrictUtf8.GetByteCount(toolCallId);
        }
        catch (EncoderFallbackException exception) {
            throw InvalidOutput(
                "Recall tool-call ID contains invalid UTF-16.",
                exception
            );
        }
        if (byteCount > MaximumToolCallIdUtf8Bytes) {
            throw InvalidOutput(
                $"Recall tool-call ID exceeds {MaximumToolCallIdUtf8Bytes} UTF-8 bytes."
            );
        }
    }

    internal static CompletionUsage SanitizeUsage(CompletionUsage usage) {
        ArgumentNullException.ThrowIfNull(usage);
        PromptCacheTelemetry promptCache = usage.PromptCache;
        return new CompletionUsage(
            usage.UncachedInputTokens,
            usage.CacheCreationInputTokens,
            usage.CacheReadInputTokens,
            usage.OutputTokens,
            new PromptCacheTelemetry(
                promptCache.RequestStatus,
                promptCache.SupportStatus,
                promptCache.ObservationStatus,
                providerDiagnostics: null
            )
        );
    }

    internal static MemoRecallException InvalidOutput(
        string message,
        Exception? innerException = null
    ) => new(
        MemoRecallFailureKind.InvalidModelOutput,
        message,
        innerException
    );

    internal static MemoRecallException ProviderFailure(
        string message,
        Exception? innerException = null
    ) => new(
        MemoRecallFailureKind.ProviderFailure,
        message,
        innerException
    );

    internal static MemoRecallException LocalLimit(string message)
        => new(MemoRecallFailureKind.LocalLimitExceeded, message);

    private static ImmutableArray<MemoId> ParseUtf8(
        ReadOnlySpan<byte> utf8,
        int maxResults
    ) {
        var reader = new Utf8JsonReader(
            utf8,
            new JsonReaderOptions {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            }
        );
        RequireToken(ref reader, JsonTokenType.StartObject, "root object");
        RequireToken(ref reader, JsonTokenType.PropertyName, "memoIds");
        if (!reader.ValueTextEquals("memoIds")) {
            throw InvalidOutput(
                "Recall tool arguments must contain only the exact 'memoIds' property."
            );
        }
        RequireToken(ref reader, JsonTokenType.StartArray, "memoIds array");

        var builder = ImmutableArray.CreateBuilder<MemoId>();
        var seen = new HashSet<MemoId>();
        while (ReadNext(ref reader, "memo ID or array end")
            is not JsonTokenType.EndArray) {
            if (reader.TokenType is not JsonTokenType.String) {
                throw InvalidOutput(
                    "Every recall memoIds item must be a string."
                );
            }
            if (builder.Count == maxResults) {
                throw InvalidOutput(
                    $"Recall output exceeds the requested maximum of {maxResults} IDs."
                );
            }
            string value = reader.GetString()
                ?? throw InvalidOutput(
                    "Recall memoIds items must not be null."
                );
            if (!MemoId.TryParse(value, out MemoId id)) {
                throw InvalidOutput(
                    "Recall memoIds must contain only canonical MemoId strings."
                );
            }
            if (!seen.Add(id)) {
                throw InvalidOutput(
                    $"Recall output contains duplicate MemoId '{id}'."
                );
            }
            builder.Add(id);
        }

        RequireToken(ref reader, JsonTokenType.EndObject, "root end");
        if (reader.Read()) {
            throw InvalidOutput(
                "Recall tool arguments contain trailing JSON content."
            );
        }
        return builder.ToImmutable();
    }

    private static void RequireToken(
        ref Utf8JsonReader reader,
        JsonTokenType expected,
        string context
    ) {
        JsonTokenType actual = ReadNext(ref reader, context);
        if (actual != expected) {
            throw InvalidOutput(
                $"Recall tool arguments expected {expected} for {context}, but found {actual}."
            );
        }
    }

    private static JsonTokenType ReadNext(
        ref Utf8JsonReader reader,
        string context
    ) {
        if (!reader.Read()) {
            throw InvalidOutput(
                $"Recall tool arguments ended before {context}."
            );
        }
        return reader.TokenType;
    }
}
