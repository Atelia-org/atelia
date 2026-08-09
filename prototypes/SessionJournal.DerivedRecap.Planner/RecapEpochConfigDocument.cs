using Microsoft.Win32.SafeHandles;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed record RecapEpochCadenceConfigDocument(
    string HistoryUnitLoadEstimatorId,
    long MinimumRecentHistoryLoad,
    long RecapBuildIntervalHistoryLoad
);

public sealed record RecapEpochCatalogEntryDocument(
    string ProfileName,
    int MaxContentUtf8Bytes
);

public sealed record RecapEpochLimitsDocument(
    int MaxRawGrowthEventCount,
    int MaxRawEventsPerEpoch,
    int MaxMaintainerCallsPerEpoch,
    int MaxEpochsPerOperation,
    int MaxMaintainerCallsPerOperation,
    int MaxRecapBlockCount,
    int MaxRebuildForwardRangeEventCount,
    int MaxTotalRecapPackUtf8Bytes,
    int MaxCanonicalPriorPackBytes,
    int MaxEpochInputBytes,
    int MaxManifestBytes,
    int MaxFinalBlockBytes,
    int MaxPublicationBytes
);

public sealed record RecapEpochConfigDocument(
    string Schema,
    string PlanningPolicy,
    RecapEpochCadenceConfigDocument Cadence,
    IReadOnlyList<RecapEpochCatalogEntryDocument> Catalog,
    RecapEpochLimitsDocument Limits
);

public static class RecapEpochConfigCodec {
    public const string SchemaV3 =
        "atelia.session-journal.recap-epoch-config.v3";
    public const int MaximumEncodedBytes = 64 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    public static RecapEpochConfigDocument Decode(
        ReadOnlySpan<byte> utf8Json
    ) {
        if (utf8Json.Length == 0
            || utf8Json.Length > MaximumEncodedBytes) {
            throw new InvalidDataException(
                "Recap epoch config size is invalid."
            );
        }
        try {
            using JsonDocument json = JsonDocument.Parse(
                utf8Json.ToArray(),
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                }
            );
            JsonElement root = json.RootElement;
            RequireObject(root, "config");
            RequireProperties(
                root,
                "config",
                "schema",
                "planningPolicy",
                "cadence",
                "catalog",
                "limits"
            );
            string schema = String(root, "schema");
            if (!string.Equals(schema, SchemaV3, StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    $"Unsupported recap epoch config schema '{schema}'."
                );
            }
            JsonElement cadence = root.GetProperty("cadence");
            RequireObject(cadence, "cadence");
            RequireProperties(
                cadence,
                "cadence",
                "historyUnitLoadEstimatorId",
                "minimumRecentHistoryLoad",
                "recapBuildIntervalHistoryLoad"
            );
            JsonElement catalog = root.GetProperty("catalog");
            if (catalog.ValueKind != JsonValueKind.Array) {
                throw new InvalidDataException("Catalog must be an array.");
            }
            var entries = new List<RecapEpochCatalogEntryDocument>();
            foreach (JsonElement entry in catalog.EnumerateArray()) {
                RequireObject(entry, "catalog entry");
                RequireProperties(
                    entry,
                    "catalog entry",
                    "profileName",
                    "maxContentUtf8Bytes"
                );
                entries.Add(new RecapEpochCatalogEntryDocument(
                    String(entry, "profileName"),
                    Int32(entry, "maxContentUtf8Bytes")
                ));
            }
            JsonElement limits = root.GetProperty("limits");
            RequireObject(limits, "limits");
            string[] limitProperties = [
                "maxRawGrowthEventCount",
                "maxRawEventsPerEpoch",
                "maxMaintainerCallsPerEpoch",
                "maxEpochsPerOperation",
                "maxMaintainerCallsPerOperation",
                "maxRecapBlockCount",
                "maxRebuildForwardRangeEventCount",
                "maxTotalRecapPackUtf8Bytes",
                "maxCanonicalPriorPackBytes",
                "maxEpochInputBytes",
                "maxManifestBytes",
                "maxFinalBlockBytes",
                "maxPublicationBytes"
            ];
            RequireProperties(limits, "limits", limitProperties);
            var document = new RecapEpochConfigDocument(
                schema,
                String(root, "planningPolicy"),
                new RecapEpochCadenceConfigDocument(
                    String(cadence, "historyUnitLoadEstimatorId"),
                    Int64(cadence, "minimumRecentHistoryLoad"),
                    Int64(cadence, "recapBuildIntervalHistoryLoad")
                ),
                Array.AsReadOnly(entries.ToArray()),
                new RecapEpochLimitsDocument(
                    Int32(limits, "maxRawGrowthEventCount"),
                    Int32(limits, "maxRawEventsPerEpoch"),
                    Int32(limits, "maxMaintainerCallsPerEpoch"),
                    Int32(limits, "maxEpochsPerOperation"),
                    Int32(limits, "maxMaintainerCallsPerOperation"),
                    Int32(limits, "maxRecapBlockCount"),
                    Int32(limits, "maxRebuildForwardRangeEventCount"),
                    Int32(limits, "maxTotalRecapPackUtf8Bytes"),
                    Int32(limits, "maxCanonicalPriorPackBytes"),
                    Int32(limits, "maxEpochInputBytes"),
                    Int32(limits, "maxManifestBytes"),
                    Int32(limits, "maxFinalBlockBytes"),
                    Int32(limits, "maxPublicationBytes")
                )
            );
            Validate(document);
            byte[] canonical = Encode(document);
            if (!utf8Json.SequenceEqual(canonical)) {
                throw new InvalidDataException(
                    "Recap epoch config is not canonical v3 JSON."
                );
            }
            return document;
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                "Recap epoch config is not strict JSON.",
                exception
            );
        }
    }

    public static byte[] Encode(RecapEpochConfigDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteString("schema", document.Schema);
            writer.WriteString("planningPolicy", document.PlanningPolicy);
            writer.WritePropertyName("cadence");
            writer.WriteStartObject();
            writer.WriteString(
                "historyUnitLoadEstimatorId",
                document.Cadence.HistoryUnitLoadEstimatorId
            );
            writer.WriteNumber(
                "minimumRecentHistoryLoad",
                document.Cadence.MinimumRecentHistoryLoad
            );
            writer.WriteNumber(
                "recapBuildIntervalHistoryLoad",
                document.Cadence.RecapBuildIntervalHistoryLoad
            );
            writer.WriteEndObject();
            writer.WritePropertyName("catalog");
            writer.WriteStartArray();
            foreach (RecapEpochCatalogEntryDocument entry
                     in document.Catalog) {
                writer.WriteStartObject();
                writer.WriteString("profileName", entry.ProfileName);
                writer.WriteNumber(
                    "maxContentUtf8Bytes",
                    entry.MaxContentUtf8Bytes
                );
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("limits");
            writer.WriteStartObject();
            WriteLimits(writer, document.Limits);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        byte[] bytes = stream.ToArray();
        if (bytes.Length > MaximumEncodedBytes) {
            throw new InvalidDataException(
                "Canonical recap epoch config exceeds its size limit."
            );
        }
        return bytes;
    }

    private static void Validate(RecapEpochConfigDocument document) {
        if (!string.Equals(
                document.Schema,
                SchemaV3,
                StringComparison.Ordinal
            )
            || !string.Equals(
                document.PlanningPolicy,
                MaintainCompleteRosterEpochPolicy.PolicyId,
                StringComparison.Ordinal
            )
            || string.IsNullOrWhiteSpace(
                document.Cadence.HistoryUnitLoadEstimatorId
            )
            || document.Cadence.MinimumRecentHistoryLoad < 0
            || document.Cadence.RecapBuildIntervalHistoryLoad <= 0
            || document.Catalog.Count == 0
            || document.Catalog.Any(static entry =>
                entry is null
                || string.IsNullOrWhiteSpace(entry.ProfileName)
                || entry.MaxContentUtf8Bytes <= 0)
            || document.Catalog.Select(static entry => entry.ProfileName)
                .Distinct(StringComparer.Ordinal).Count()
                != document.Catalog.Count) {
            throw new InvalidDataException(
                "Recap epoch config shape is invalid."
            );
        }
        RecapEpochLimitsDocument limits = document.Limits;
        int[] positive = [
            limits.MaxRawGrowthEventCount,
            limits.MaxRawEventsPerEpoch,
            limits.MaxMaintainerCallsPerEpoch,
            limits.MaxEpochsPerOperation,
            limits.MaxMaintainerCallsPerOperation,
            limits.MaxRecapBlockCount,
            limits.MaxRebuildForwardRangeEventCount,
            limits.MaxTotalRecapPackUtf8Bytes,
            limits.MaxCanonicalPriorPackBytes,
            limits.MaxEpochInputBytes,
            limits.MaxManifestBytes,
            limits.MaxFinalBlockBytes,
            limits.MaxPublicationBytes
        ];
        if (positive.Any(static value => value <= 0)) {
            throw new InvalidDataException(
                "Recap epoch config limits must be positive."
            );
        }
    }

    private static void WriteLimits(
        Utf8JsonWriter writer,
        RecapEpochLimitsDocument limits
    ) {
        writer.WriteNumber("maxRawGrowthEventCount", limits.MaxRawGrowthEventCount);
        writer.WriteNumber("maxRawEventsPerEpoch", limits.MaxRawEventsPerEpoch);
        writer.WriteNumber("maxMaintainerCallsPerEpoch", limits.MaxMaintainerCallsPerEpoch);
        writer.WriteNumber("maxEpochsPerOperation", limits.MaxEpochsPerOperation);
        writer.WriteNumber("maxMaintainerCallsPerOperation", limits.MaxMaintainerCallsPerOperation);
        writer.WriteNumber("maxRecapBlockCount", limits.MaxRecapBlockCount);
        writer.WriteNumber("maxRebuildForwardRangeEventCount", limits.MaxRebuildForwardRangeEventCount);
        writer.WriteNumber("maxTotalRecapPackUtf8Bytes", limits.MaxTotalRecapPackUtf8Bytes);
        writer.WriteNumber("maxCanonicalPriorPackBytes", limits.MaxCanonicalPriorPackBytes);
        writer.WriteNumber("maxEpochInputBytes", limits.MaxEpochInputBytes);
        writer.WriteNumber("maxManifestBytes", limits.MaxManifestBytes);
        writer.WriteNumber("maxFinalBlockBytes", limits.MaxFinalBlockBytes);
        writer.WriteNumber("maxPublicationBytes", limits.MaxPublicationBytes);
    }

    private static void RequireObject(JsonElement value, string label) {
        if (value.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException($"{label} must be an object.");
        }
    }

    private static void RequireProperties(
        JsonElement value,
        string label,
        params string[] expected
    ) {
        string[] actual = [
            .. value.EnumerateObject().Select(static item => item.Name)
        ];
        if (actual.Length != expected.Length
            || actual.Distinct(StringComparer.Ordinal).Count()
                != actual.Length
            || expected.Any(name => !actual.Contains(
                name,
                StringComparer.Ordinal
            ))) {
            throw new InvalidDataException(
                $"{label} has unknown, missing, or duplicate properties."
            );
        }
    }

    private static string String(JsonElement owner, string name) {
        JsonElement value = owner.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : throw new InvalidDataException(
                    $"Property '{name}' must be a non-empty string."
                );
    }

    private static int Int32(JsonElement owner, string name) {
        JsonElement value = owner.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int parsed)
                ? parsed
                : throw new InvalidDataException(
                    $"Property '{name}' must be an Int32."
                );
    }

    private static long Int64(JsonElement owner, string name) {
        JsonElement value = owner.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long parsed)
                ? parsed
                : throw new InvalidDataException(
                    $"Property '{name}' must be an Int64."
                );
    }
}

public static class RecapEpochConfigLoader {
    public const string ConfigDirectoryName = "config";
    public const string ConfigFileName = "recap-planner-config.json";

    public static string GetCanonicalPath(string repositoryRoot) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        return Path.GetFullPath(Path.Combine(
            repositoryRoot,
            ConfigDirectoryName,
            ConfigFileName
        ));
    }

    public static bool TryLoad(
        string repositoryRoot,
        out RecapEpochConfigDocument document
    ) {
        string path = GetCanonicalPath(repositoryRoot);
        SafeFileHandle handle;
        try {
            handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                FileOptions.RandomAccess
            );
        }
        catch (FileNotFoundException) {
            document = null!;
            return false;
        }
        catch (DirectoryNotFoundException) {
            document = null!;
            return false;
        }
        using (handle) {
            FileAttributes attributes = File.GetAttributes(handle);
            if ((attributes & (
                    FileAttributes.Directory
                    | FileAttributes.Device
                    | FileAttributes.ReparsePoint
                )) != 0) {
                throw new InvalidDataException(
                    "Recap epoch config must be a regular file."
                );
            }
            long length = RandomAccess.GetLength(handle);
            if (length is <= 0
                or > RecapEpochConfigCodec.MaximumEncodedBytes) {
                throw new InvalidDataException(
                    "Recap epoch config size is invalid."
                );
            }
            byte[] bytes = new byte[checked((int)length)];
            int offset = 0;
            while (offset < bytes.Length) {
                int read = RandomAccess.Read(
                    handle,
                    bytes.AsSpan(offset),
                    offset
                );
                if (read == 0) {
                    throw new EndOfStreamException(
                        "Recap epoch config ended during one-handle read."
                    );
                }
                offset = checked(offset + read);
            }
            if (RandomAccess.GetLength(handle) != length) {
                throw new IOException(
                    "Recap epoch config changed during read."
                );
            }
            document = RecapEpochConfigCodec.Decode(bytes);
            return true;
        }
    }
}
