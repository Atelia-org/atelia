using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.RecapGrid.Cadence;

internal static class CadenceCanonicalCodec {
    internal const string Schema =
        "atelia.session-journal.recap-grid.cadence.v1";
    private const string Domain =
        "atelia.session-journal.recap-grid.cadence.domain.v1";
    private static readonly JsonWriterOptions WriterOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static RecapGridCadenceSnapshot Create(
        RefId refId,
        long generation,
        RecapGridCadencePolicySpec policy
    ) {
        if (refId.IsDefault) {
            throw new ArgumentException("RefId must not be default.", nameof(refId));
        }
        if (generation < 0) {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        ArgumentNullException.ThrowIfNull(policy);
        RecapGridCadenceDomainDigest digest = ComputeDigest(refId, policy);
        byte[] bytes = Encode(refId, generation, policy, digest);
        if (bytes.Length > RecapGridCadenceLimits.MaximumCanonicalUtf8Bytes) {
            throw new CadenceLimitException("CadenceCanonicalBytes");
        }
        return new RecapGridCadenceSnapshot(
            new RecapGridCadenceHeadRef(refId, generation, digest),
            policy,
            bytes);
    }

    internal static RecapGridCadenceSnapshot Decode(ReadOnlySpan<byte> bytes) {
        if (bytes.Length is < 2
            or > RecapGridCadenceLimits.MaximumCanonicalUtf8Bytes) {
            throw new CadenceStoreException(
                "CadenceCanonicalLimitExceeded",
                "Cadence canonical bytes are outside the code-owned bound.");
        }
        try {
            _ = StrictUtf8.GetString(bytes);
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                throw new InvalidDataException("Cadence root must be an object.");
            }
            JsonProperty[] properties = [.. root.EnumerateObject()];
            if (properties.Length == 0
                || !string.Equals(
                    properties[0].Name,
                    "schema",
                    StringComparison.Ordinal)
                || properties.Count(static property => string.Equals(
                    property.Name,
                    "schema",
                    StringComparison.OrdinalIgnoreCase)) != 1
                || properties[0].Value.ValueKind
                    != JsonValueKind.String) {
                throw new InvalidDataException(
                    "Cadence requires one exact leading schema discriminator.");
            }
            string schema = properties[0].Value.GetString()!;
            if (!string.Equals(schema, Schema, StringComparison.Ordinal)) {
                throw new CadenceUnsupportedSchemaException(
                    ReadSchemaVersion(schema));
            }
            RequireProperties(root,
                "schema", "refId", "generation",
                "minimumRecentHistoryLoad", "partitionAlgorithmId",
                "historyLoadEstimatorId", "targetHistoryLoad",
                "maxRawEvents", "maxRenderedBytes", "domainDigest");
            RefId refId = RefId.ParseHex(ReadString(root, "refId")).Unwrap();
            long generation = ReadInt64(root, "generation");
            var policy = new RecapGridCadencePolicySpec(
                ReadInt64(root, "minimumRecentHistoryLoad"),
                ReadString(root, "partitionAlgorithmId"),
                ReadString(root, "historyLoadEstimatorId"),
                ReadInt64(root, "targetHistoryLoad"),
                ReadInt32(root, "maxRawEvents"),
                ReadInt32(root, "maxRenderedBytes"));
            var digest = new RecapGridCadenceDomainDigest(
                ReadString(root, "domainDigest"));
            RecapGridCadenceSnapshot expected = Create(
                refId, generation, policy);
            if (expected.Head.DomainDigest != digest
                || !bytes.SequenceEqual(expected.ToCanonicalBytes())) {
                throw new InvalidDataException(
                    "Cadence bytes are not exact canonical bytes.");
            }
            return expected;
        }
        catch (CadenceUnsupportedSchemaException) { throw; }
        catch (CadenceStoreException) { throw; }
        catch (Exception exception) when (exception is JsonException
            or DecoderFallbackException
            or InvalidDataException
            or ArgumentException
            or InvalidOperationException
            or OverflowException) {
            throw new CadenceStoreException(
                "CadenceCanonicalInvalid",
                "Cadence canonical bytes are invalid.", exception);
        }
    }

    private static RecapGridCadenceDomainDigest ComputeDigest(
        RefId refId,
        RecapGridCadencePolicySpec policy
    ) {
        string preimage = string.Join('\0',
            Domain,
            refId.ToHexString(),
            policy.MinimumRecentHistoryLoad.ToString(CultureInfo.InvariantCulture),
            policy.PartitionAlgorithmId,
            policy.HistoryLoadEstimatorId,
            policy.TargetHistoryLoad.ToString(CultureInfo.InvariantCulture),
            policy.MaxRawEvents.ToString(CultureInfo.InvariantCulture),
            policy.MaxRenderedBytes.ToString(CultureInfo.InvariantCulture));
        return new RecapGridCadenceDomainDigest(
            Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(preimage))));
    }

    private static byte[] Encode(
        RefId refId,
        long generation,
        RecapGridCadencePolicySpec policy,
        RecapGridCadenceDomainDigest digest
    ) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("refId", refId.ToHexString());
            writer.WriteNumber("generation", generation);
            writer.WriteNumber("minimumRecentHistoryLoad",
                policy.MinimumRecentHistoryLoad);
            writer.WriteString("partitionAlgorithmId",
                policy.PartitionAlgorithmId);
            writer.WriteString("historyLoadEstimatorId",
                policy.HistoryLoadEstimatorId);
            writer.WriteNumber("targetHistoryLoad", policy.TargetHistoryLoad);
            writer.WriteNumber("maxRawEvents", policy.MaxRawEvents);
            writer.WriteNumber("maxRenderedBytes", policy.MaxRenderedBytes);
            writer.WriteString("domainDigest", digest.Value);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void RequireProperties(
        JsonElement value,
        params string[] expected
    ) {
        string[] actual = [.. value.EnumerateObject().Select(
            static property => property.Name)];
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) {
            throw new InvalidDataException(
                "Cadence properties are missing, duplicated, unknown, or reordered.");
        }
    }

    private static string ReadString(JsonElement root, string name) {
        JsonElement value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"Cadence '{name}' must be text.");
    }

    private static long ReadInt64(JsonElement root, string name) {
        JsonElement value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long result)
            ? result
            : throw new InvalidDataException($"Cadence '{name}' must be Int64.");
    }

    private static int ReadInt32(JsonElement root, string name) {
        long value = ReadInt64(root, name);
        return checked((int)value);
    }

    private static int ReadSchemaVersion(string schema) {
        const string prefix =
            "atelia.session-journal.recap-grid.cadence.v";
        return schema.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(
                schema.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int version)
            && version > 0
                ? version
                : -1;
    }
}
