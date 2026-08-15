using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.SessionJournal.RecapGrid;

internal static class RecapGridCanonical {
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    internal static byte[] Encode<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    internal static T DecodeExact<T>(
        ReadOnlySpan<byte> bytes,
        int maximumBytes,
        string valueName
    ) {
        if (bytes.Length is < 2 || bytes.Length > maximumBytes) {
            throw new ArgumentOutOfRangeException(
                valueName,
                $"The canonical value must contain between 2 and {maximumBytes} bytes."
            );
        }
        T? decoded;
        try {
            decoded = JsonSerializer.Deserialize<T>(bytes, Options);
        }
        catch (JsonException exception) {
            throw new ArgumentException(
                "The value is not valid canonical JSON.",
                valueName,
                exception
            );
        }
        if (decoded is null) {
            throw new ArgumentException(
                "The canonical value must not be null.",
                valueName
            );
        }
        byte[] encoded = Encode(decoded);
        if (!bytes.SequenceEqual(encoded)) {
            throw new ArgumentException(
                "The JSON bytes are not the exact canonical encoding.",
                valueName
            );
        }
        return decoded;
    }

    private static JsonSerializerOptions CreateOptions() => new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
