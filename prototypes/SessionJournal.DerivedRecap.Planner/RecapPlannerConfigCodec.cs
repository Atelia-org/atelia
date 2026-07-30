using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public static class RecapPlannerConfigCodec {
    public const string SchemaV1 =
        "atelia.session-journal.recap-planner-config.v1";
    public const int MaxDocumentUtf8Bytes = 64 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    public static RecapPlannerConfigDecodeResult Decode(
        ReadOnlySpan<byte> utf8Json
    ) {
        if (utf8Json.Length > MaxDocumentUtf8Bytes) {
            return Invalid(
                RecapPlannerConfigDefectCodes.SizeLimitExceeded,
                $"Planner config exceeds {MaxDocumentUtf8Bytes} UTF-8 bytes."
            );
        }

        try {
            using JsonDocument json = JsonDocument.Parse(
                utf8Json.ToArray(),
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                }
            );
            RecapPlannerConfigDocument document =
                ReadDocument(json.RootElement);
            IReadOnlyList<RecapPlannerConfigDefect> defects =
                ValidateDocument(document);
            if (defects.Count != 0) {
                return new RecapPlannerConfigDecodeResult.Invalid(
                    defects
                );
            }

            byte[] canonical = WriteCanonical(document);
            if (canonical.Length > MaxDocumentUtf8Bytes) {
                return Invalid(
                    RecapPlannerConfigDefectCodes.SizeLimitExceeded,
                    $"Canonical planner config exceeds "
                    + $"{MaxDocumentUtf8Bytes} UTF-8 bytes."
                );
            }
            return new RecapPlannerConfigDecodeResult.Valid(
                document,
                ImmutableArray.CreateRange(canonical),
                ComputeSha256(canonical)
            );
        }
        catch (ConfigDocumentException exception) {
            return Invalid(exception.Code, exception.Message);
        }
        catch (JsonException exception) {
            return Invalid(
                RecapPlannerConfigDefectCodes.Malformed,
                $"Planner config is not strict JSON: {exception.Message}"
            );
        }
    }

    public static byte[] EncodeCanonical(
        RecapPlannerConfigDocument document
    ) {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<RecapPlannerConfigDefect> defects =
            ValidateDocument(document);
        if (defects.Count != 0) {
            throw new InvalidDataException(
                "Cannot encode invalid recap planner config: "
                + string.Join(
                    "; ",
                    defects.Select(static defect =>
                        $"{defect.Code}: {defect.Detail}"
                    )
                )
            );
        }
        byte[] canonical = WriteCanonical(document);
        if (canonical.Length > MaxDocumentUtf8Bytes) {
            throw new InvalidDataException(
                $"Canonical planner config exceeds "
                + $"{MaxDocumentUtf8Bytes} UTF-8 bytes."
            );
        }
        return canonical;
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static RecapPlannerConfigDocument ReadDocument(
        JsonElement root
    ) {
        IReadOnlyDictionary<string, JsonElement> properties =
            ReadExactObject(
                root,
                "$",
                "schema",
                "planningPolicy",
                "cadence",
                "catalog",
                "limits"
            );
        string schema = ReadString(properties["schema"], "$.schema");
        if (!string.Equals(schema, SchemaV1, StringComparison.Ordinal)) {
            throw new ConfigDocumentException(
                RecapPlannerConfigDefectCodes.UnsupportedSchema,
                $"Unsupported recap planner config schema '{schema}'."
            );
        }
        string planningPolicy = ReadString(
            properties["planningPolicy"],
            "$.planningPolicy"
        );
        RecapCadenceConfigDocument cadence =
            ReadCadence(properties["cadence"]);
        IReadOnlyList<RecapPlannerCatalogEntryDocument> catalog =
            ReadCatalog(properties["catalog"]);
        RecapPlannerLimitsDocument limits =
            ReadLimits(properties["limits"]);
        return new RecapPlannerConfigDocument(
            schema,
            planningPolicy,
            cadence,
            catalog,
            limits
        );
    }

    private static RecapCadenceConfigDocument ReadCadence(
        JsonElement element
    ) {
        IReadOnlyDictionary<string, JsonElement> properties =
            ReadExactObject(
                element,
                "$.cadence",
                "minimumRecentHistoryUnitCount",
                "recapBuildIntervalUnitCount"
            );
        return new RecapCadenceConfigDocument(
            ReadInt32(
                properties["minimumRecentHistoryUnitCount"],
                "$.cadence.minimumRecentHistoryUnitCount"
            ),
            ReadInt32(
                properties["recapBuildIntervalUnitCount"],
                "$.cadence.recapBuildIntervalUnitCount"
            )
        );
    }

    private static IReadOnlyList<RecapPlannerCatalogEntryDocument>
        ReadCatalog(JsonElement element) {
        if (element.ValueKind != JsonValueKind.Array) {
            throw Malformed("$.catalog must be an array.");
        }
        var entries =
            new List<RecapPlannerCatalogEntryDocument>();
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray()) {
            string path = $"$.catalog[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties =
                ReadExactObject(
                    item,
                    path,
                    "maintainerProfile",
                    "maxContentUtf8Bytes"
                );
            entries.Add(new RecapPlannerCatalogEntryDocument(
                ReadString(
                    properties["maintainerProfile"],
                    $"{path}.maintainerProfile"
                ),
                ReadInt32(
                    properties["maxContentUtf8Bytes"],
                    $"{path}.maxContentUtf8Bytes"
                )
            ));
            index++;
        }
        return entries.AsReadOnly();
    }

    private static RecapPlannerLimitsDocument ReadLimits(
        JsonElement element
    ) {
        IReadOnlyDictionary<string, JsonElement> properties =
            ReadExactObject(
                element,
                "$.limits",
                "maxRawGrowthEventCount",
                "maxRouteEndpointsPerBlock",
                "maxMaintainerCallsPerBuild",
                "maxRawEventsPerStep",
                "maxRawEventsPerBuild"
            );
        return new RecapPlannerLimitsDocument(
            ReadInt32(
                properties["maxRawGrowthEventCount"],
                "$.limits.maxRawGrowthEventCount"
            ),
            ReadInt32(
                properties["maxRouteEndpointsPerBlock"],
                "$.limits.maxRouteEndpointsPerBlock"
            ),
            ReadInt32(
                properties["maxMaintainerCallsPerBuild"],
                "$.limits.maxMaintainerCallsPerBuild"
            ),
            ReadInt32(
                properties["maxRawEventsPerStep"],
                "$.limits.maxRawEventsPerStep"
            ),
            ReadInt32(
                properties["maxRawEventsPerBuild"],
                "$.limits.maxRawEventsPerBuild"
            )
        );
    }

    private static IReadOnlyDictionary<string, JsonElement>
        ReadExactObject(
        JsonElement element,
        string path,
        params string[] expectedNames
    ) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw Malformed($"{path} must be an object.");
        }

        var expected = new HashSet<string>(
            expectedNames,
            StringComparer.Ordinal
        );
        var found = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal
        );
        foreach (JsonProperty property in
                 element.EnumerateObject()) {
            if (!expected.Contains(property.Name)) {
                throw Malformed(
                    $"{path} contains unknown property "
                    + $"'{property.Name}'."
                );
            }
            if (!found.TryAdd(property.Name, property.Value)) {
                throw Malformed(
                    $"{path} contains duplicate property "
                    + $"'{property.Name}'."
                );
            }
        }
        foreach (string expectedName in expectedNames) {
            if (!found.ContainsKey(expectedName)) {
                throw Malformed(
                    $"{path} is missing required property "
                    + $"'{expectedName}'."
                );
            }
        }
        return found;
    }

    private static string ReadString(
        JsonElement element,
        string path
    ) {
        if (element.ValueKind != JsonValueKind.String) {
            throw Malformed($"{path} must be a string.");
        }
        return element.GetString()!;
    }

    private static int ReadInt32(
        JsonElement element,
        string path
    ) {
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int value)) {
            throw new ConfigDocumentException(
                RecapPlannerConfigDefectCodes.InvalidLimit,
                $"{path} must be a 32-bit integer."
            );
        }
        return value;
    }

    internal static IReadOnlyList<RecapPlannerConfigDefect>
        ValidateDocument(
        RecapPlannerConfigDocument document
    ) {
        var defects = new List<RecapPlannerConfigDefect>();
        if (!string.Equals(
                document.Schema,
                SchemaV1,
                StringComparison.Ordinal
            )) {
            Add(
                defects,
                RecapPlannerConfigDefectCodes.UnsupportedSchema,
                $"Unsupported recap planner config schema "
                + $"'{document.Schema ?? "<null>"}'."
            );
        }
        if (string.IsNullOrWhiteSpace(document.PlanningPolicy)) {
            Add(
                defects,
                RecapPlannerConfigDefectCodes.Malformed,
                "planningPolicy cannot be empty."
            );
        }

        ValidateCadence(document.Cadence, defects);
        ValidateCatalog(document.Catalog, defects);
        ValidateLimits(document.Cadence, document.Limits, defects);
        return defects.AsReadOnly();
    }

    private static void ValidateCadence(
        RecapCadenceConfigDocument? cadence,
        ICollection<RecapPlannerConfigDefect> defects
    ) {
        if (cadence is null) {
            Add(
                defects,
                RecapPlannerConfigDefectCodes.InvalidLimit,
                "cadence cannot be null."
            );
            return;
        }
        if (cadence.MinimumRecentHistoryUnitCount < 0) {
            Add(
                defects,
                RecapPlannerConfigDefectCodes.InvalidLimit,
                "minimumRecentHistoryUnitCount cannot be negative."
            );
        }
        if (cadence.RecapBuildIntervalUnitCount <= 0) {
            Add(
                defects,
                RecapPlannerConfigDefectCodes.InvalidLimit,
                "recapBuildIntervalUnitCount must be positive."
            );
        }
        try {
            _ = checked(
                cadence.MinimumRecentHistoryUnitCount
                + cadence.RecapBuildIntervalUnitCount
            );
        }
        catch (OverflowException) {
            Add(
                defects,
                RecapPlannerConfigDefectCodes.InvalidLimit,
                "The cadence threshold overflows Int32."
            );
        }
    }

    private static void ValidateCatalog(
        IReadOnlyList<RecapPlannerCatalogEntryDocument>? catalog,
        ICollection<RecapPlannerConfigDefect> defects
    ) {
        if (catalog is null
            || catalog.Count is < 1
                or > SessionContextContributionContract
                    .MaxContributionCount) {
            Add(
                defects,
                RecapPlannerConfigDefectCodes.InvalidCatalog,
                "catalog must contain 1 through "
                + $"{SessionContextContributionContract.MaxContributionCount} "
                + "profiles."
            );
            return;
        }

        var profileNames =
            new HashSet<string>(StringComparer.Ordinal);
        foreach (RecapPlannerCatalogEntryDocument? entry in catalog) {
            if (entry is null
                || string.IsNullOrWhiteSpace(
                    entry.MaintainerProfile
                )) {
                Add(
                    defects,
                    RecapPlannerConfigDefectCodes.InvalidCatalog,
                    "Catalog profile names cannot be empty."
                );
                continue;
            }
            if (!profileNames.Add(entry.MaintainerProfile)) {
                Add(
                    defects,
                    RecapPlannerConfigDefectCodes
                        .DuplicateProfileName,
                    $"Catalog profile '{entry.MaintainerProfile}' "
                    + "is duplicated."
                );
            }
            if (entry.MaxContentUtf8Bytes <= 0
                || entry.MaxContentUtf8Bytes
                    > SessionContextContributionContract
                        .MaxContributionUtf8Bytes) {
                Add(
                    defects,
                    RecapPlannerConfigDefectCodes.InvalidCatalog,
                    $"Catalog profile '{entry.MaintainerProfile}' "
                    + "has an invalid maxContentUtf8Bytes."
                );
            }
        }
    }

    private static void ValidateLimits(
        RecapCadenceConfigDocument? cadence,
        RecapPlannerLimitsDocument? limits,
        ICollection<RecapPlannerConfigDefect> defects
    ) {
        if (limits is null) {
            Add(
                defects,
                RecapPlannerConfigDefectCodes.InvalidLimit,
                "limits cannot be null."
            );
            return;
        }
        if (limits.MaxRawGrowthEventCount <= 0
            || limits.MaxRouteEndpointsPerBlock <= 0
            || limits.MaxMaintainerCallsPerBuild <= 0
            || limits.MaxRawEventsPerStep <= 0
            || limits.MaxRawEventsPerBuild <= 0) {
            Add(
                defects,
                RecapPlannerConfigDefectCodes.InvalidLimit,
                "All planner limits must be positive."
            );
        }
        if (cadence is null) {
            return;
        }
        try {
            int threshold = checked(
                cadence.MinimumRecentHistoryUnitCount
                + cadence.RecapBuildIntervalUnitCount
            );
            if (threshold > limits.MaxRawGrowthEventCount) {
                Add(
                    defects,
                    RecapPlannerConfigDefectCodes.InvalidLimit,
                    "The cadence threshold must not exceed "
                    + "maxRawGrowthEventCount."
                );
            }
        }
        catch (OverflowException) {
            // ValidateCadence already reports the overflow.
        }
    }

    private static byte[] WriteCanonical(
        RecapPlannerConfigDocument document
    ) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   WriterOptions
               )) {
            writer.WriteStartObject();
            writer.WriteString("schema", document.Schema);
            writer.WriteString(
                "planningPolicy",
                document.PlanningPolicy
            );

            writer.WritePropertyName("cadence");
            writer.WriteStartObject();
            writer.WriteNumber(
                "minimumRecentHistoryUnitCount",
                document.Cadence.MinimumRecentHistoryUnitCount
            );
            writer.WriteNumber(
                "recapBuildIntervalUnitCount",
                document.Cadence.RecapBuildIntervalUnitCount
            );
            writer.WriteEndObject();

            writer.WritePropertyName("catalog");
            writer.WriteStartArray();
            foreach (RecapPlannerCatalogEntryDocument entry in
                     document.Catalog) {
                writer.WriteStartObject();
                writer.WriteString(
                    "maintainerProfile",
                    entry.MaintainerProfile
                );
                writer.WriteNumber(
                    "maxContentUtf8Bytes",
                    entry.MaxContentUtf8Bytes
                );
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("limits");
            writer.WriteStartObject();
            writer.WriteNumber(
                "maxRawGrowthEventCount",
                document.Limits.MaxRawGrowthEventCount
            );
            writer.WriteNumber(
                "maxRouteEndpointsPerBlock",
                document.Limits.MaxRouteEndpointsPerBlock
            );
            writer.WriteNumber(
                "maxMaintainerCallsPerBuild",
                document.Limits.MaxMaintainerCallsPerBuild
            );
            writer.WriteNumber(
                "maxRawEventsPerStep",
                document.Limits.MaxRawEventsPerStep
            );
            writer.WriteNumber(
                "maxRawEventsPerBuild",
                document.Limits.MaxRawEventsPerBuild
            );
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static RecapPlannerConfigDecodeResult.Invalid Invalid(
        string code,
        string detail
    ) => new([
        new RecapPlannerConfigDefect(code, detail)
    ]);

    private static ConfigDocumentException Malformed(string detail)
        => new(RecapPlannerConfigDefectCodes.Malformed, detail);

    private static void Add(
        ICollection<RecapPlannerConfigDefect> defects,
        string code,
        string detail
    ) => defects.Add(new RecapPlannerConfigDefect(code, detail));

    private sealed class ConfigDocumentException(
        string code,
        string message
    ) : Exception(message) {
        public string Code { get; } = code;
    }
}
