using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

public static class HistoryTimelineCanonicalCodec {
    public const int MaximumPolicyUtf8Bytes = 4 * 1024;
    public const int MaximumDescriptorUtf8Bytes = 16 * 1024;

    public static byte[] Encode(TimelineHeadRef value) {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("timelineId", value.TimelineId.Value);
            writer.WriteString("refId", value.RefId.ToHexString());
            if (value.HeadRowId is { } rowId) {
                writer.WriteString("headRowId", rowId.Value);
            }
            else {
                writer.WriteNull("headRowId");
            }
            writer.WriteString(
                "activePartitionPolicyDigest",
                value.ActivePartitionPolicyDigest
            );
            if (value.SelectedRawHeadAtCommit is { } rawHead) {
                writer.WriteString(
                    "selectedRawHeadAtCommit",
                    SJ.EventAddressTextCodec.Format(rawHead)
                );
            }
            else {
                writer.WriteNull("selectedRawHeadAtCommit");
            }
            writer.WriteNumber("generation", value.Generation);
            writer.WriteEndObject();
        }
        return RequireEncodedBound(
            buffer.WrittenMemory.ToArray(),
            HistoryTimelineStoreLimits.MaximumHeadUtf8Bytes,
            "Timeline head"
        );
    }

    public static TimelineHeadRef DecodeTimelineHead(
        ReadOnlySpan<byte> bytes
    ) => DecodeCanonical(
        bytes,
        HistoryTimelineStoreLimits.MaximumHeadUtf8Bytes,
        static root => {
            RequireVersion(root, "Timeline head");
            string? row = ReadNullableString(root, "headRowId");
            string? raw = ReadNullableString(
                root,
                "selectedRawHeadAtCommit"
            );
            return new TimelineHeadRef(
                new TimelineId(ReadString(root, "timelineId")),
                ReadRefId(root, "refId"),
                row is null ? null : new HistoryRowId(row),
                ReadString(root, "activePartitionPolicyDigest"),
                raw is null
                    ? null
                    : SJ.EventAddressTextCodec.Parse(raw),
                ReadInt64(root, "generation")
            );
        },
        Encode,
        "Timeline head"
    );

    public static byte[] Encode(ActiveTimelineLocator value) {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("refId", value.RefId.ToHexString());
            writer.WriteString(
                "activeTimelineId",
                value.ActiveTimelineId.Value
            );
            writer.WriteNumber("generation", value.Generation);
            writer.WriteEndObject();
        }
        return RequireEncodedBound(
            buffer.WrittenMemory.ToArray(),
            HistoryTimelineStoreLimits.MaximumLocatorUtf8Bytes,
            "active Timeline locator"
        );
    }

    public static ActiveTimelineLocator DecodeActiveTimelineLocator(
        ReadOnlySpan<byte> bytes
    ) => DecodeCanonical(
        bytes,
        HistoryTimelineStoreLimits.MaximumLocatorUtf8Bytes,
        static root => {
            RequireVersion(root, "active Timeline locator");
            return new ActiveTimelineLocator(
                ReadRefId(root, "refId"),
                new TimelineId(ReadString(root, "activeTimelineId")),
                ReadInt64(root, "generation")
            );
        },
        Encode,
        "active Timeline locator"
    );

    public static byte[] Encode(HistoryTimelineBackupManifest value) {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString(
                "locatorCanonical",
                Encoding.UTF8.GetString(
                    value.Locator.ToCanonicalBytes()
                )
            );
            writer.WriteString(
                "headCanonical",
                Encoding.UTF8.GetString(
                    value.Head.ToCanonicalBytes()
                )
            );
            writer.WriteString("headSha256", value.HeadSha256);
            writer.WriteString(
                "databaseSha256",
                value.DatabaseSha256
            );
            writer.WriteNumber(
                "databaseBytes",
                value.DatabaseBytes
            );
            writer.WriteEndObject();
        }
        return RequireEncodedBound(
            buffer.WrittenMemory.ToArray(),
            HistoryTimelineStoreLimits
                .MaximumBackupManifestUtf8Bytes,
            "Timeline backup manifest"
        );
    }

    public static HistoryTimelineBackupManifest
        DecodeHistoryTimelineBackupManifest(
        ReadOnlySpan<byte> bytes
    ) => DecodeCanonical(
        bytes,
        HistoryTimelineStoreLimits.MaximumBackupManifestUtf8Bytes,
        static root => {
            RequireVersion(root, "Timeline backup manifest");
            ActiveTimelineLocator locator =
                DecodeActiveTimelineLocator(Encoding.UTF8.GetBytes(
                    ReadString(root, "locatorCanonical")
                ));
            TimelineHeadRef head = DecodeTimelineHead(
                Encoding.UTF8.GetBytes(
                    ReadString(root, "headCanonical")
                )
            );
            return new HistoryTimelineBackupManifest(
                locator,
                head,
                ReadString(root, "headSha256"),
                ReadString(root, "databaseSha256"),
                ReadInt64(root, "databaseBytes")
            );
        },
        Encode,
        "Timeline backup manifest"
    );

    internal static byte[] Encode(
        HistoryTimelineSelectedPathSnapshotBody value
    ) {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("headRowId", value.HeadRowId.Value);
            writer.WriteString(
                "rowRootDigest",
                value.RowRootDigest
            );
            writer.WriteString(
                "endRootDigest",
                value.EndRootDigest
            );
            writer.WriteNumber("memberCount", value.MemberCount);
            writer.WriteEndObject();
        }
        return RequireEncodedBound(
            buffer.WrittenMemory.ToArray(),
            HistoryTimelineStoreLimits.MaximumHeadUtf8Bytes,
            "selected-path snapshot"
        );
    }

    internal static HistoryTimelineSelectedPathSnapshotBody
        DecodeSelectedPathSnapshot(
        ReadOnlySpan<byte> bytes
    ) => DecodeCanonical(
        bytes,
        HistoryTimelineStoreLimits.MaximumHeadUtf8Bytes,
        static root => {
            RequireVersion(root, "selected-path snapshot");
            return new HistoryTimelineSelectedPathSnapshotBody(
                new HistoryRowId(ReadString(root, "headRowId")),
                ReadString(root, "rowRootDigest"),
                ReadString(root, "endRootDigest"),
                ReadInt32(root, "memberCount")
            );
        },
        Encode,
        "selected-path snapshot"
    );

    private static readonly JsonWriterOptions WriterOptions = new() {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false
    };

    public static byte[] Encode(PartitionPolicyRevision value) {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            WritePolicyFields(writer, value);
            writer.WriteString("policyDigest", value.PolicyDigest);
            writer.WriteEndObject();
        }
        return RequireEncodedBound(
            buffer.WrittenMemory.ToArray(),
            MaximumPolicyUtf8Bytes,
            "partition policy"
        );
    }

    public static PartitionPolicyRevision DecodePartitionPolicy(
        ReadOnlySpan<byte> bytes
    ) => DecodeCanonical(
        bytes,
        MaximumPolicyUtf8Bytes,
        static root => {
            RequireVersion(root, "partition policy");
            return PartitionPolicyRevision.DecodeChecked(
                new TimelineId(ReadString(root, "timelineId")),
                ReadString(root, "partitionAlgorithmId"),
                ReadString(root, "historyLoadEstimatorId"),
                new HistoryLoadUnit(ReadInt64(root, "targetHistoryLoad")),
                ReadInt32(root, "maxRawEvents"),
                ReadInt32(root, "maxRenderedBytes"),
                ReadString(root, "policyDigest")
            );
        },
        Encode,
        "partition policy"
    );

    public static byte[] Encode(HistorySegmentDescriptor value) {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("timelineId", value.TimelineId.Value);
            writer.WriteString(
                "partitionPolicyDigestAtCreation",
                value.PartitionPolicyDigestAtCreation
            );
            writer.WriteString("rowId", value.RowId.Value);
            if (value.PreviousRowId is { } previousRowId) {
                writer.WriteString("previousRowId", previousRowId.Value);
            }
            else {
                writer.WriteNull("previousRowId");
            }
            WriteDescriptorRangeFields(writer, value);
            writer.WriteString(
                "descriptorDigest",
                value.DescriptorDigest.Value
            );
            writer.WriteEndObject();
        }
        return RequireEncodedBound(
            buffer.WrittenMemory.ToArray(),
            MaximumDescriptorUtf8Bytes,
            "history segment descriptor"
        );
    }

    public static HistorySegmentDescriptor DecodeHistorySegmentDescriptor(
        ReadOnlySpan<byte> bytes
    ) => DecodeCanonical(
        bytes,
        MaximumDescriptorUtf8Bytes,
        static root => {
            RequireVersion(root, "history segment descriptor");
            var timelineId = new TimelineId(
                ReadString(root, "timelineId")
            );
            string policyDigest = ReadString(
                root,
                "partitionPolicyDigestAtCreation"
            );
            var rowId = new HistoryRowId(ReadString(root, "rowId"));
            HistoryRowId? previousRowId = ReadNullableString(
                root,
                "previousRowId"
            ) is { } previous
                ? new HistoryRowId(previous)
                : null;
            RefId refId = ReadRefId(root, "refId");
            EventAddress startExclusive = ReadAddress(
                root,
                "startExclusive"
            );
            EventAddress endInclusive = ReadAddress(
                root,
                "endInclusive"
            );
            SJ.SessionContextAnchorSetupReferences startSetups =
                ReadSetups(root, "startSetups");
            SJ.SessionContextAnchorSetupReferences endSetups =
                ReadSetups(root, "endSetups");
            string estimatorId = ReadString(
                root,
                "historyLoadEstimatorId"
            );
            var target = new HistoryLoadUnit(ReadInt64(
                root,
                "targetHistoryLoadAtCreation"
            ));
            var measured = new HistoryLoadUnit(ReadInt64(
                root,
                "measuredHistoryLoad"
            ));
            int rawEventCount = ReadInt32(root, "rawEventCount");
            int renderedBytes = ReadInt32(
                root,
                "measuredRenderedUtf8Bytes"
            );
            string rawRangeSha256 = ReadString(
                root,
                "rawRangeSha256"
            );
            var descriptorDigest =
                new HistorySegmentDescriptorDigest(ReadString(
                    root,
                    "descriptorDigest"
                ));

            byte[] body = EncodeDescriptorBody(
                timelineId,
                policyDigest,
                previousRowId,
                refId,
                startExclusive,
                endInclusive,
                startSetups,
                endSetups,
                estimatorId,
                target,
                measured,
                rawEventCount,
                renderedBytes,
                rawRangeSha256
            );
            string expectedRowId = HistoryTimelineHash.Compute(
                HistoryTimelineHash.RowIdDomain,
                body
            );
            string expectedDescriptorDigest = HistoryTimelineHash.Compute(
                HistoryTimelineHash.DescriptorDomain,
                body
            );
            if (!string.Equals(
                    expectedRowId,
                    rowId.Value,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expectedDescriptorDigest,
                    descriptorDigest.Value,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "History segment identity does not match its canonical body."
                );
            }
            return new HistorySegmentDescriptor(
                timelineId,
                policyDigest,
                rowId,
                previousRowId,
                refId,
                startExclusive,
                endInclusive,
                startSetups,
                endSetups,
                estimatorId,
                target,
                measured,
                rawEventCount,
                renderedBytes,
                rawRangeSha256,
                descriptorDigest
            );
        },
        Encode,
        "history segment descriptor"
    );

    internal static byte[] EncodePolicyBody(
        TimelineId timelineId,
        string partitionAlgorithmId,
        string historyLoadEstimatorId,
        HistoryLoadUnit targetHistoryLoad,
        int maxRawEvents,
        int maxRenderedBytes
    ) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("timelineId", timelineId.Value);
            writer.WriteString(
                "partitionAlgorithmId",
                partitionAlgorithmId
            );
            writer.WriteString(
                "historyLoadEstimatorId",
                historyLoadEstimatorId
            );
            writer.WriteNumber(
                "targetHistoryLoad",
                targetHistoryLoad.Value
            );
            writer.WriteNumber("maxRawEvents", maxRawEvents);
            writer.WriteNumber("maxRenderedBytes", maxRenderedBytes);
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    internal static byte[] EncodeDescriptorBody(
        TimelineId timelineId,
        string partitionPolicyDigestAtCreation,
        HistoryRowId? previousRowId,
        RefId refId,
        EventAddress startExclusive,
        EventAddress endInclusive,
        SJ.SessionContextAnchorSetupReferences startSetups,
        SJ.SessionContextAnchorSetupReferences endSetups,
        string historyLoadEstimatorId,
        HistoryLoadUnit targetHistoryLoadAtCreation,
        HistoryLoadUnit measuredHistoryLoad,
        int rawEventCount,
        int measuredRenderedUtf8Bytes,
        string rawRangeSha256
    ) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("timelineId", timelineId.Value);
            writer.WriteString(
                "partitionPolicyDigestAtCreation",
                partitionPolicyDigestAtCreation
            );
            if (previousRowId is { } previous) {
                writer.WriteString("previousRowId", previous.Value);
            }
            else {
                writer.WriteNull("previousRowId");
            }
            WriteDescriptorRangeFields(
                writer,
                refId,
                startExclusive,
                endInclusive,
                startSetups,
                endSetups,
                historyLoadEstimatorId,
                targetHistoryLoadAtCreation,
                measuredHistoryLoad,
                rawEventCount,
                measuredRenderedUtf8Bytes,
                rawRangeSha256
            );
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    private static void WritePolicyFields(
        Utf8JsonWriter writer,
        PartitionPolicyRevision value
    ) {
        writer.WriteString("timelineId", value.TimelineId.Value);
        writer.WriteString(
            "partitionAlgorithmId",
            value.PartitionAlgorithmId
        );
        writer.WriteString(
            "historyLoadEstimatorId",
            value.HistoryLoadEstimatorId
        );
        writer.WriteNumber(
            "targetHistoryLoad",
            value.TargetHistoryLoad.Value
        );
        writer.WriteNumber("maxRawEvents", value.MaxRawEvents);
        writer.WriteNumber("maxRenderedBytes", value.MaxRenderedBytes);
    }

    private static void WriteDescriptorRangeFields(
        Utf8JsonWriter writer,
        HistorySegmentDescriptor value
    ) => WriteDescriptorRangeFields(
        writer,
        value.RefId,
        value.StartExclusive,
        value.EndInclusive,
        value.StartSetups,
        value.EndSetups,
        value.HistoryLoadEstimatorId,
        value.TargetHistoryLoadAtCreation,
        value.MeasuredHistoryLoad,
        value.RawEventCount,
        value.MeasuredRenderedUtf8Bytes,
        value.RawRangeSha256
    );

    private static void WriteDescriptorRangeFields(
        Utf8JsonWriter writer,
        RefId refId,
        EventAddress startExclusive,
        EventAddress endInclusive,
        SJ.SessionContextAnchorSetupReferences startSetups,
        SJ.SessionContextAnchorSetupReferences endSetups,
        string historyLoadEstimatorId,
        HistoryLoadUnit targetHistoryLoadAtCreation,
        HistoryLoadUnit measuredHistoryLoad,
        int rawEventCount,
        int measuredRenderedUtf8Bytes,
        string rawRangeSha256
    ) {
        writer.WriteString("refId", refId.ToHexString());
        writer.WriteString(
            "startExclusive",
            SJ.EventAddressTextCodec.Format(startExclusive)
        );
        writer.WriteString(
            "endInclusive",
            SJ.EventAddressTextCodec.Format(endInclusive)
        );
        WriteSetups(writer, "startSetups", startSetups);
        WriteSetups(writer, "endSetups", endSetups);
        writer.WriteString(
            "historyLoadEstimatorId",
            historyLoadEstimatorId
        );
        writer.WriteNumber(
            "targetHistoryLoadAtCreation",
            targetHistoryLoadAtCreation.Value
        );
        writer.WriteNumber(
            "measuredHistoryLoad",
            measuredHistoryLoad.Value
        );
        writer.WriteNumber("rawEventCount", rawEventCount);
        writer.WriteNumber(
            "measuredRenderedUtf8Bytes",
            measuredRenderedUtf8Bytes
        );
        writer.WriteString("rawRangeSha256", rawRangeSha256);
    }

    private static void WriteSetups(
        Utf8JsonWriter writer,
        string propertyName,
        SJ.SessionContextAnchorSetupReferences value
    ) {
        writer.WriteStartObject(propertyName);
        WriteSetup(writer, "runtimeConfig", value.RuntimeConfig);
        WriteSetup(writer, "systemPrompt", value.SystemPrompt);
        writer.WriteEndObject();
    }

    private static void WriteSetup(
        Utf8JsonWriter writer,
        string propertyName,
        SJ.SessionContextSetupReference value
    ) {
        writer.WriteStartObject(propertyName);
        writer.WriteString(
            "address",
            SJ.EventAddressTextCodec.Format(value.Address)
        );
        writer.WriteNumber("bodySchemaVersion", value.BodySchemaVersion);
        writer.WriteString("payloadSha256", value.PayloadSha256);
        writer.WriteEndObject();
    }

    private static T DecodeCanonical<T>(
        ReadOnlySpan<byte> bytes,
        int maximumBytes,
        Func<JsonElement, T> decode,
        Func<T, byte[]> encode,
        string label
    ) {
        if (bytes.Length == 0 || bytes.Length > maximumBytes) {
            throw new InvalidDataException(
                $"Canonical {label} must contain 1..{maximumBytes} UTF-8 bytes."
            );
        }
        byte[] input = bytes.ToArray();
        try {
            using JsonDocument document = JsonDocument.Parse(
                input,
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                }
            );
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                throw new InvalidDataException(
                    $"Canonical {label} root must be an object."
                );
            }
            T value = decode(root);
            byte[] canonical = encode(value);
            if (!input.AsSpan().SequenceEqual(canonical)) {
                throw new InvalidDataException(
                    $"{label} bytes are not the exact canonical encoding."
                );
            }
            return value;
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (exception is
            JsonException
            or ArgumentException
            or FormatException
            or InvalidOperationException
            or OverflowException) {
            throw new InvalidDataException(
                $"Canonical {label} is invalid.",
                exception
            );
        }
    }

    private static void RequireVersion(JsonElement root, string label) {
        if (ReadInt32(root, "v") != 1) {
            throw new InvalidDataException(
                $"Unsupported {label} schema version."
            );
        }
    }

    private static string ReadString(JsonElement root, string propertyName) {
        JsonElement value = ReadRequired(root, propertyName);
        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text) {
            throw new InvalidDataException(
                $"Property '{propertyName}' must be a non-null string."
            );
        }
        return text;
    }

    private static string? ReadNullableString(
        JsonElement root,
        string propertyName
    ) {
        JsonElement value = ReadRequired(root, propertyName);
        return value.ValueKind switch {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString()
                ?? throw new InvalidDataException(
                    $"Property '{propertyName}' cannot be null."
                ),
            _ => throw new InvalidDataException(
                $"Property '{propertyName}' must be a string or null."
            )
        };
    }

    private static int ReadInt32(JsonElement root, string propertyName) {
        JsonElement value = ReadRequired(root, propertyName);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int result)) {
            throw new InvalidDataException(
                $"Property '{propertyName}' must be an Int32."
            );
        }
        return result;
    }

    private static long ReadInt64(JsonElement root, string propertyName) {
        JsonElement value = ReadRequired(root, propertyName);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long result)) {
            throw new InvalidDataException(
                $"Property '{propertyName}' must be an Int64."
            );
        }
        return result;
    }

    private static JsonElement ReadRequired(
        JsonElement root,
        string propertyName
    ) {
        if (!root.TryGetProperty(propertyName, out JsonElement value)) {
            throw new InvalidDataException(
                $"Required property '{propertyName}' is missing."
            );
        }
        return value;
    }

    private static RefId ReadRefId(
        JsonElement root,
        string propertyName
    ) {
        string text = ReadString(root, propertyName);
        AteliaResult<RefId> parsed = RefId.ParseHex(text);
        if (!parsed.TryUnwrap(out RefId value, out _)
            || value.IsDefault) {
            throw new InvalidDataException(
                $"Property '{propertyName}' is not a canonical non-default RefId."
            );
        }
        return value;
    }

    private static EventAddress ReadAddress(
        JsonElement root,
        string propertyName
    ) => SJ.EventAddressTextCodec.Parse(
        ReadString(root, propertyName)
    );

    private static SJ.SessionContextAnchorSetupReferences ReadSetups(
        JsonElement root,
        string propertyName
    ) {
        JsonElement value = ReadRequired(root, propertyName);
        if (value.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException(
                $"Property '{propertyName}' must be an object."
            );
        }
        return new SJ.SessionContextAnchorSetupReferences(
            ReadSetup(value, "runtimeConfig"),
            ReadSetup(value, "systemPrompt")
        );
    }

    private static SJ.SessionContextSetupReference ReadSetup(
        JsonElement root,
        string propertyName
    ) {
        JsonElement value = ReadRequired(root, propertyName);
        if (value.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException(
                $"Property '{propertyName}' must be an object."
            );
        }
        return new SJ.SessionContextSetupReference(
            ReadAddress(value, "address"),
            ReadInt32(value, "bodySchemaVersion"),
            ReadString(value, "payloadSha256")
        );
    }

    private static byte[] RequireEncodedBound(
        byte[] bytes,
        int maximumBytes,
        string label
    ) {
        if (bytes.Length > maximumBytes) {
            throw new InvalidOperationException(
                $"Canonical {label} exceeds {maximumBytes} UTF-8 bytes."
            );
        }
        return bytes;
    }
}

internal static class HistoryTimelineHash {
    internal const string PolicyDomain =
        "atelia.history-timeline.partition-policy.v1";
    internal const string RowIdDomain =
        "atelia.history-timeline.row-id.v1";
    internal const string DescriptorDomain =
        "atelia.history-timeline.descriptor.v1";

    internal static string Compute(
        string domain,
        ReadOnlySpan<byte> canonicalBody
    ) {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        Append(hash, Encoding.UTF8.GetBytes(domain));
        Append(hash, canonicalBody);
        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static void Append(
        IncrementalHash hash,
        ReadOnlySpan<byte> bytes
    ) {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
