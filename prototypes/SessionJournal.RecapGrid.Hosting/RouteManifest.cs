using System.Buffers;
using System.Text;
using System.Text.Json;
using Atelia.SessionJournal.RecapGrid.Runtime;

namespace Atelia.SessionJournal.RecapGrid.Hosting;

public static class RecapGridRouteManifestLimits {
    public const int MaximumCanonicalUtf8Bytes = 1024 * 1024;
    public const int MaximumRouteCount = 4_096;
    public const int MaximumIdentifierUtf8Bytes = 128;
}

public sealed record RecapGridRouteManifestEntry {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public RecapGridRouteManifestEntry(
        RecapCompletionRouteKey key,
        string connectionId,
        int maximumConcurrency,
        TimeSpan dispatchTimeout,
        int? maximumOutputTokens
    ) {
        Key = key;
        ConnectionId = RequireIdentifier(connectionId, nameof(connectionId));
        if (maximumConcurrency is < 1 or > 1_024) {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }
        if (dispatchTimeout <= TimeSpan.Zero
            || dispatchTimeout > TimeSpan.FromDays(1)
            || dispatchTimeout.Ticks % TimeSpan.TicksPerMillisecond != 0) {
            throw new ArgumentOutOfRangeException(nameof(dispatchTimeout));
        }
        if (maximumOutputTokens is <= 0) {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        }
        MaximumConcurrency = maximumConcurrency;
        DispatchTimeout = dispatchTimeout;
        MaximumOutputTokens = maximumOutputTokens;
    }

    public RecapCompletionRouteKey Key { get; }
    public string ConnectionId { get; }
    public int MaximumConcurrency { get; }
    public TimeSpan DispatchTimeout { get; }
    public int? MaximumOutputTokens { get; }

    private static string RequireIdentifier(
        string value,
        string parameterName
    ) {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)) {
            throw new ArgumentException(
                "A bounded non-empty identifier is required.",
                parameterName
            );
        }
        try {
            if (StrictUtf8.GetByteCount(value)
                    > RecapGridRouteManifestLimits.MaximumIdentifierUtf8Bytes) {
                throw new ArgumentException(
                    "A bounded non-empty identifier is required.",
                    parameterName
                );
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "A bounded strict UTF-8 identifier is required.",
                parameterName,
                exception
            );
        }
        return value;
    }
}

public sealed class RecapGridRouteManifest {
    private const int SchemaVersion = 1;
    private readonly byte[] _canonicalBytes;

    private RecapGridRouteManifest(
        IReadOnlyList<RecapGridRouteManifestEntry> routes,
        byte[] canonicalBytes
    ) {
        Routes = Array.AsReadOnly(routes.ToArray());
        _canonicalBytes = (byte[])canonicalBytes.Clone();
    }

    public IReadOnlyList<RecapGridRouteManifestEntry> Routes { get; }

    public static RecapGridRouteManifest Create(
        IEnumerable<RecapGridRouteManifestEntry> routes
    ) {
        ArgumentNullException.ThrowIfNull(routes);
        RecapGridRouteManifestEntry[] materialized = routes
            .Take(RecapGridRouteManifestLimits.MaximumRouteCount + 1)
            .ToArray();
        if (materialized.Length
            > RecapGridRouteManifestLimits.MaximumRouteCount) {
            throw new ArgumentOutOfRangeException(nameof(routes));
        }
        if (materialized.Any(static route => route is null)) {
            throw new ArgumentException(
                "Route entries must not be null.",
                nameof(routes)
            );
        }
        RecapGridRouteManifestEntry[] ordered = [.. materialized
            .OrderBy(static route => route.Key.FamilyDigest.Value,
                StringComparer.Ordinal)
            .ThenBy(static route => route.Key.RuntimeProtocolId,
                StringComparer.Ordinal)
            .ThenBy(static route => route.Key.SemanticModelId is null ? 0 : 1)
            .ThenBy(static route => route.Key.SemanticModelId,
                StringComparer.Ordinal)];
        if (ordered.Select(static route => route.Key).Distinct().Count()
            != ordered.Length) {
            throw new ArgumentException(
                "Route keys must be exact and unique.",
                nameof(routes)
            );
        }
        byte[] canonical = Encode(ordered);
        if (canonical.Length
            > RecapGridRouteManifestLimits.MaximumCanonicalUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(routes));
        }
        return new RecapGridRouteManifest(ordered, canonical);
    }

    public static RecapGridRouteManifest DecodeCanonical(
        ReadOnlySpan<byte> bytes
    ) {
        if (bytes.Length is < 1
            or > RecapGridRouteManifestLimits.MaximumCanonicalUtf8Bytes) {
            throw new InvalidDataException(
                "Route manifest canonical bytes exceed the V1 bound."
            );
        }
        try {
            using JsonDocument document = JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                }
            );
            JsonElement root = document.RootElement;
            RequireObjectProperties(root, "v", "routes");
            if (root.GetProperty("v").GetInt32() != SchemaVersion) {
                throw new InvalidDataException(
                    "Route manifest schema version is unsupported."
                );
            }
            JsonElement array = root.GetProperty("routes");
            if (array.ValueKind != JsonValueKind.Array
                || array.GetArrayLength()
                    > RecapGridRouteManifestLimits.MaximumRouteCount) {
                throw new InvalidDataException(
                    "Route manifest routes are invalid or exceed the V1 bound."
                );
            }
            var routes = new List<RecapGridRouteManifestEntry>(
                array.GetArrayLength()
            );
            foreach (JsonElement item in array.EnumerateArray()) {
                RequireObjectProperties(
                    item,
                    "familyDigest",
                    "runtimeProtocolId",
                    "semanticModelId",
                    "connectionId",
                    "maximumConcurrency",
                    "dispatchTimeoutMilliseconds",
                    "maximumOutputTokens"
                );
                JsonElement semantic = item.GetProperty("semanticModelId");
                string? semanticModelId = semantic.ValueKind switch {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => semantic.GetString(),
                    _ => throw new InvalidDataException(
                        "semanticModelId must be an explicit string or null."
                    )
                };
                JsonElement output = item.GetProperty("maximumOutputTokens");
                int? maximumOutputTokens = output.ValueKind switch {
                    JsonValueKind.Null => null,
                    JsonValueKind.Number => output.GetInt32(),
                    _ => throw new InvalidDataException(
                        "maximumOutputTokens must be an integer or null."
                    )
                };
                routes.Add(new RecapGridRouteManifestEntry(
                    new RecapCompletionRouteKey(
                        new FamilyDefinitionDigest(
                            RequireString(item, "familyDigest")
                        ),
                        RequireString(item, "runtimeProtocolId"),
                        semanticModelId
                    ),
                    RequireString(item, "connectionId"),
                    item.GetProperty("maximumConcurrency").GetInt32(),
                    TimeSpan.FromMilliseconds(item.GetProperty(
                        "dispatchTimeoutMilliseconds").GetInt64()),
                    maximumOutputTokens
                ));
            }
            RecapGridRouteManifest decoded = Create(routes);
            if (!bytes.SequenceEqual(decoded._canonicalBytes)) {
                throw new InvalidDataException(
                    "Route manifest bytes are not exact canonical V1 bytes."
                );
            }
            return decoded;
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or ArgumentException
                or FormatException
                or InvalidOperationException
                or OverflowException) {
            throw new InvalidDataException(
                "Route manifest is not a valid canonical V1 value.",
                exception
            );
        }
    }

    public byte[] ToCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    private static byte[] Encode(
        IReadOnlyList<RecapGridRouteManifestEntry> routes
    ) {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions {
            Indented = false,
            SkipValidation = false
        });
        writer.WriteStartObject();
        writer.WriteNumber("v", SchemaVersion);
        writer.WriteStartArray("routes");
        foreach (RecapGridRouteManifestEntry route in routes) {
            writer.WriteStartObject();
            writer.WriteString("familyDigest", route.Key.FamilyDigest.Value);
            writer.WriteString("runtimeProtocolId", route.Key.RuntimeProtocolId);
            if (route.Key.SemanticModelId is null) {
                writer.WriteNull("semanticModelId");
            }
            else {
                writer.WriteString(
                    "semanticModelId",
                    route.Key.SemanticModelId
                );
            }
            writer.WriteString("connectionId", route.ConnectionId);
            writer.WriteNumber(
                "maximumConcurrency",
                route.MaximumConcurrency
            );
            writer.WriteNumber(
                "dispatchTimeoutMilliseconds",
                checked((long)route.DispatchTimeout.TotalMilliseconds)
            );
            if (route.MaximumOutputTokens is { } tokens) {
                writer.WriteNumber("maximumOutputTokens", tokens);
            }
            else {
                writer.WriteNull("maximumOutputTokens");
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static string RequireString(JsonElement owner, string property) {
        string? value = owner.GetProperty(property).GetString();
        if (value is null) {
            throw new InvalidDataException($"{property} must be a string.");
        }
        return value;
    }

    private static void RequireObjectProperties(
        JsonElement value,
        params string[] exactNames
    ) {
        if (value.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException("A JSON object is required.");
        }
        string[] actual = [.. value.EnumerateObject()
            .Select(static property => property.Name)];
        if (!actual.SequenceEqual(exactNames, StringComparer.Ordinal)) {
            throw new InvalidDataException(
                "JSON properties are missing, duplicated, unknown, or out of order."
            );
        }
    }
}
