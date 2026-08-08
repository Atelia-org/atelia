using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

internal static class DerivedRecapRebuildSpoolCodec {
    public const string CaptureSchema =
        "atelia.session-journal.derived-recap-rebuild-capture.v1";
    public const string CheckpointSchema =
        "atelia.session-journal.derived-recap-rebuild-checkpoint.v1";
    public const string PageSchema =
        "atelia.session-journal.derived-recap-rebuild-page.v1";
    public const string SealSchema =
        "atelia.session-journal.derived-recap-rebuild-seal.v1";

    private static readonly JsonWriterOptions WriterOptions = new() {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false
    };

    public static string InitialPageChainSha256 { get; } =
        Sha256Hex(Encoding.UTF8.GetBytes(PageSchema + "\0root"));

    public static byte[] EncodeCapture(
        DerivedRecapRebuildSpoolDescriptor descriptor
    ) => Write(writer => {
        writer.WriteStartObject();
        writer.WriteString("schema", CaptureSchema);
        writer.WriteString("campaignId", descriptor.CampaignId);
        writer.WriteString(
            "branchName",
            descriptor.Capture.BranchName
        );
        writer.WriteString(
            "refId",
            descriptor.Capture.BranchRefId.ToHexString()
        );
        WriteAddress(
            writer,
            "capturedHead",
            descriptor.Capture.CapturedHead
        );
        writer.WriteNumber(
            "pageEventCount",
            descriptor.Limits.PageEventCount
        );
        writer.WriteNumber(
            "maximumPageBytes",
            descriptor.Limits.MaximumPageBytes
        );
        writer.WriteNumber(
            "maximumEventCount",
            descriptor.Limits.MaximumEventCount
        );
        writer.WriteNumber(
            "maximumTotalEncodedBytes",
            descriptor.Limits.MaximumTotalEncodedBytes
        );
        writer.WriteEndObject();
    });

    public static DerivedRecapRebuildSpoolDescriptor DecodeCapture(
        ReadOnlySpan<byte> bytes
    ) {
        using JsonDocument document = Parse(bytes, "rebuild capture");
        JsonElement root = document.RootElement;
        RequireExactProperties(
            root,
            "rebuild capture",
            "schema",
            "campaignId",
            "branchName",
            "refId",
            "capturedHead",
            "pageEventCount",
            "maximumPageBytes",
            "maximumEventCount",
            "maximumTotalEncodedBytes"
        );
        RequireSchema(root, CaptureSchema, "rebuild capture");
        var descriptor = new DerivedRecapRebuildSpoolDescriptor(
            ReadCampaignId(root, "campaignId"),
            new SessionSelectedLineageAuditCapture(
                ReadRequiredString(root, "branchName", 1, 512),
                ReadRefId(root, "refId"),
                ReadAddress(root, "capturedHead")
            ),
            new DerivedRecapRebuildSpoolLimits(
                ReadInt32(root, "pageEventCount"),
                ReadInt64(root, "maximumPageBytes"),
                ReadInt64(root, "maximumEventCount"),
                ReadInt64(root, "maximumTotalEncodedBytes")
            )
        );
        ValidateDescriptor(descriptor);
        RequireCanonical(bytes, EncodeCapture(descriptor), "rebuild capture");
        return descriptor;
    }

    public static byte[] EncodeCheckpoint(
        DerivedRecapRebuildSpoolCheckpoint checkpoint
    ) => Write(writer => {
        writer.WriteStartObject();
        writer.WriteString("schema", CheckpointSchema);
        writer.WriteString(
            "campaignId",
            checkpoint.Descriptor.CampaignId
        );
        writer.WriteNumber(
            "committedPageCount",
            checkpoint.CommittedPageCount
        );
        WriteNullableAddress(
            writer,
            "nextAddress",
            checkpoint.NextAddress
        );
        writer.WriteNumber("eventCount", checkpoint.EventCount);
        writer.WriteNumber(
            "logicalPayloadBytes",
            checkpoint.LogicalPayloadBytes
        );
        writer.WriteNumber(
            "encodedPageBytes",
            checkpoint.EncodedPageBytes
        );
        writer.WriteString(
            "pageChainSha256",
            checkpoint.PageChainSha256
        );
        writer.WriteEndObject();
    });

    public static DerivedRecapRebuildSpoolCheckpoint DecodeCheckpoint(
        ReadOnlySpan<byte> bytes,
        DerivedRecapRebuildSpoolDescriptor descriptor
    ) {
        using JsonDocument document = Parse(bytes, "rebuild checkpoint");
        JsonElement root = document.RootElement;
        RequireExactProperties(
            root,
            "rebuild checkpoint",
            "schema",
            "campaignId",
            "committedPageCount",
            "nextAddress",
            "eventCount",
            "logicalPayloadBytes",
            "encodedPageBytes",
            "pageChainSha256"
        );
        RequireSchema(root, CheckpointSchema, "rebuild checkpoint");
        string campaignId = ReadCampaignId(root, "campaignId");
        if (!string.Equals(
                campaignId,
                descriptor.CampaignId,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Rebuild checkpoint campaign does not match capture."
            );
        }
        var checkpoint = new DerivedRecapRebuildSpoolCheckpoint(
            descriptor,
            ReadInt64(root, "committedPageCount"),
            ReadNullableAddress(root, "nextAddress"),
            ReadInt64(root, "eventCount"),
            ReadInt64(root, "logicalPayloadBytes"),
            ReadInt64(root, "encodedPageBytes"),
            ReadSha256(root, "pageChainSha256")
        );
        ValidateCheckpoint(checkpoint);
        RequireCanonical(
            bytes,
            EncodeCheckpoint(checkpoint),
            "rebuild checkpoint"
        );
        return checkpoint;
    }

    public static byte[] EncodePage(
        string campaignId,
        SessionSelectedLineageAuditPage page
    ) => Write(writer => {
        writer.WriteStartObject();
        writer.WriteString("schema", PageSchema);
        writer.WriteString("campaignId", campaignId);
        writer.WriteNumber("ordinal", page.Ordinal);
        WriteAddress(writer, "pageHead", page.PageHead);
        WriteNullableAddress(
            writer,
            "continuation",
            page.Continuation
        );
        writer.WriteStartArray("entries");
        foreach (SessionSelectedLineageAuditEntry entry
                 in page.HeadToOldest) {
            writer.WriteStartObject();
            WriteAddress(writer, "address", entry.Address);
            WriteNullableAddress(writer, "parent", entry.Parent);
            writer.WriteNumber(
                "sequenceNumber",
                entry.SequenceNumber
            );
            writer.WriteNumber("kind", (uint)entry.Kind);
            writer.WriteNumber(
                "bodySchemaVersion",
                entry.BodySchemaVersion
            );
            writer.WriteNumber(
                "logicalPayloadBytes",
                entry.LogicalPayloadBytes
            );
            writer.WriteString(
                "payloadSha256",
                entry.PayloadSha256
            );
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static SessionSelectedLineageAuditPage DecodePage(
        ReadOnlySpan<byte> bytes,
        string expectedCampaignId
    ) {
        using JsonDocument document = Parse(bytes, "rebuild page");
        JsonElement root = document.RootElement;
        RequireExactProperties(
            root,
            "rebuild page",
            "schema",
            "campaignId",
            "ordinal",
            "pageHead",
            "continuation",
            "entries"
        );
        RequireSchema(root, PageSchema, "rebuild page");
        string campaignId = ReadCampaignId(root, "campaignId");
        if (!string.Equals(
                campaignId,
                expectedCampaignId,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Rebuild page campaign does not match capture."
            );
        }
        JsonElement entriesElement = root.GetProperty("entries");
        if (entriesElement.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException(
                "Rebuild page entries must be an array."
            );
        }
        var entries = new List<SessionSelectedLineageAuditEntry>();
        foreach (JsonElement element in entriesElement.EnumerateArray()) {
            RequireExactProperties(
                element,
                "rebuild page entry",
                "address",
                "parent",
                "sequenceNumber",
                "kind",
                "bodySchemaVersion",
                "logicalPayloadBytes",
                "payloadSha256"
            );
            uint rawKind = ReadUInt32(element, "kind");
            if (!Enum.IsDefined(typeof(SessionEventKind), rawKind)) {
                throw new InvalidDataException(
                    $"Rebuild page contains unknown event kind {rawKind}."
                );
            }
            entries.Add(new SessionSelectedLineageAuditEntry(
                ReadAddress(element, "address"),
                ReadNullableAddress(element, "parent"),
                ReadUInt64(element, "sequenceNumber"),
                (SessionEventKind)rawKind,
                ReadInt32(element, "bodySchemaVersion"),
                ReadUInt32(element, "logicalPayloadBytes"),
                ReadSha256(element, "payloadSha256")
            ));
        }
        var page = new SessionSelectedLineageAuditPage(
            ReadInt64(root, "ordinal"),
            ReadAddress(root, "pageHead"),
            entries.AsReadOnly(),
            ReadNullableAddress(root, "continuation")
        );
        ValidatePage(page);
        RequireCanonical(
            bytes,
            EncodePage(campaignId, page),
            "rebuild page"
        );
        return page;
    }

    public static byte[] EncodeSeal(
        DerivedRecapRebuildSpoolSeal seal
    ) => Write(writer => {
        writer.WriteStartObject();
        writer.WriteString("schema", SealSchema);
        writer.WriteString(
            "campaignId",
            seal.Checkpoint.Descriptor.CampaignId
        );
        writer.WriteNumber(
            "committedPageCount",
            seal.Checkpoint.CommittedPageCount
        );
        writer.WriteNumber(
            "eventCount",
            seal.Checkpoint.EventCount
        );
        writer.WriteNumber(
            "logicalPayloadBytes",
            seal.Checkpoint.LogicalPayloadBytes
        );
        writer.WriteNumber(
            "encodedPageBytes",
            seal.Checkpoint.EncodedPageBytes
        );
        writer.WriteString(
            "pageChainSha256",
            seal.Checkpoint.PageChainSha256
        );
        WriteAddress(writer, "rootAddress", seal.RootAddress);
        WriteAddress(
            writer,
            "bootstrapAddress",
            seal.BootstrapAddress
        );
        WriteSetups(writer, "bootstrapSetups", seal.BootstrapSetups);
        WriteSetups(writer, "headSetups", seal.HeadSetups);
        writer.WriteNumber(
            "executionPhase",
            (int)seal.ExecutionPhase
        );
        if (seal.HeadKind is { } headKind) {
            writer.WriteNumber("headKind", (uint)headKind);
        }
        else {
            writer.WriteNull("headKind");
        }
        writer.WriteEndObject();
    });

    public static DerivedRecapRebuildSpoolSeal DecodeSeal(
        ReadOnlySpan<byte> bytes,
        DerivedRecapRebuildSpoolCheckpoint checkpoint
    ) {
        using JsonDocument document = Parse(bytes, "rebuild seal");
        JsonElement root = document.RootElement;
        RequireExactProperties(
            root,
            "rebuild seal",
            "schema",
            "campaignId",
            "committedPageCount",
            "eventCount",
            "logicalPayloadBytes",
            "encodedPageBytes",
            "pageChainSha256",
            "rootAddress",
            "bootstrapAddress",
            "bootstrapSetups",
            "headSetups",
            "executionPhase",
            "headKind"
        );
        RequireSchema(root, SealSchema, "rebuild seal");
        if (!string.Equals(
                ReadCampaignId(root, "campaignId"),
                checkpoint.Descriptor.CampaignId,
                StringComparison.Ordinal
            )
            || ReadInt64(root, "committedPageCount")
                != checkpoint.CommittedPageCount
            || ReadInt64(root, "eventCount")
                != checkpoint.EventCount
            || ReadInt64(root, "logicalPayloadBytes")
                != checkpoint.LogicalPayloadBytes
            || ReadInt64(root, "encodedPageBytes")
                != checkpoint.EncodedPageBytes
            || !string.Equals(
                ReadSha256(root, "pageChainSha256"),
                checkpoint.PageChainSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Rebuild seal does not match its checkpoint."
            );
        }
        int rawPhase = ReadInt32(root, "executionPhase");
        if (!Enum.IsDefined(typeof(SessionExecutionPhase), rawPhase)) {
            throw new InvalidDataException(
                $"Rebuild seal contains unknown execution phase {rawPhase}."
            );
        }
        SessionEventKind? headKind = null;
        JsonElement headKindElement = root.GetProperty("headKind");
        if (headKindElement.ValueKind != JsonValueKind.Null) {
            uint rawHeadKind = ReadUInt32(root, "headKind");
            if (!Enum.IsDefined(typeof(SessionEventKind), rawHeadKind)) {
                throw new InvalidDataException(
                    $"Rebuild seal contains unknown head kind {rawHeadKind}."
                );
            }
            headKind = (SessionEventKind)rawHeadKind;
        }
        var seal = new DerivedRecapRebuildSpoolSeal(
            checkpoint,
            ReadAddress(root, "rootAddress"),
            ReadAddress(root, "bootstrapAddress"),
            ReadSetups(root, "bootstrapSetups"),
            ReadSetups(root, "headSetups"),
            (SessionExecutionPhase)rawPhase,
            headKind
        );
        RequireCanonical(bytes, EncodeSeal(seal), "rebuild seal");
        return seal;
    }

    public static string AdvancePageChain(
        string priorChainSha256,
        ReadOnlySpan<byte> canonicalPageBytes
    ) {
        byte[] prior = DecodeSha256(priorChainSha256, "prior page chain");
        byte[] page = SHA256.HashData(canonicalPageBytes);
        byte[] domain = Encoding.UTF8.GetBytes(PageSchema + "\0chain");
        byte[] input = new byte[domain.Length + prior.Length + page.Length];
        domain.CopyTo(input, 0);
        prior.CopyTo(input, domain.Length);
        page.CopyTo(input, domain.Length + prior.Length);
        return Sha256Hex(input);
    }

    public static void ValidateDescriptor(
        DerivedRecapRebuildSpoolDescriptor descriptor
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);
        _ = ValidateCampaignId(descriptor.CampaignId);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            descriptor.Capture.BranchName
        );
        if (descriptor.Capture.BranchName.Length > 512
            || descriptor.Capture.BranchRefId == default
            || descriptor.Capture.CapturedHead == default) {
            throw new InvalidDataException(
                "Rebuild capture identity is invalid."
            );
        }
        if (descriptor.Limits.PageEventCount is <= 0
            or > SessionSelectedLineageAuditLimits
                .MaximumPageEventCount
            || descriptor.Limits.MaximumEventCount <= 0
            || descriptor.Limits.MaximumPageBytes <= 0
            || descriptor.Limits.MaximumPageBytes
                > 16L * 1024 * 1024
            || descriptor.Limits.MaximumTotalEncodedBytes
                < descriptor.Limits.MaximumPageBytes) {
            throw new InvalidDataException(
                "Rebuild spool limits are invalid."
            );
        }
    }

    public static void ValidateCheckpoint(
        DerivedRecapRebuildSpoolCheckpoint checkpoint
    ) {
        ValidateDescriptor(checkpoint.Descriptor);
        if (checkpoint.CommittedPageCount < 0
            || checkpoint.EventCount < 0
            || checkpoint.LogicalPayloadBytes < 0
            || checkpoint.EncodedPageBytes < 0
            || checkpoint.EventCount
                > checkpoint.Descriptor.Limits.MaximumEventCount
            || checkpoint.EncodedPageBytes
                > checkpoint.Descriptor.Limits
                    .MaximumTotalEncodedBytes) {
            throw new InvalidDataException(
                "Rebuild checkpoint counters are invalid."
            );
        }
        _ = DecodeSha256(
            checkpoint.PageChainSha256,
            "page chain"
        );
        if (checkpoint.CommittedPageCount == 0
            && (checkpoint.EventCount != 0
                || checkpoint.LogicalPayloadBytes != 0
                || checkpoint.EncodedPageBytes != 0
                || checkpoint.NextAddress
                    != checkpoint.Descriptor.Capture.CapturedHead
                || !string.Equals(
                    checkpoint.PageChainSha256,
                    InitialPageChainSha256,
                    StringComparison.Ordinal
                ))) {
            throw new InvalidDataException(
                "Initial rebuild checkpoint is not canonical."
            );
        }
    }

    public static void ValidatePage(
        SessionSelectedLineageAuditPage page
    ) {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Ordinal < 0
            || page.PageHead == default
            || page.HeadToOldest.Count is <= 0
                or > SessionSelectedLineageAuditLimits
                    .MaximumPageEventCount
            || page.HeadToOldest[0].Address != page.PageHead
            || page.HeadToOldest[^1].Parent != page.Continuation) {
            throw new InvalidDataException(
                "Rebuild audit page shape is invalid."
            );
        }
        ulong? childSequenceExclusive = null;
        for (int index = 0;
             index < page.HeadToOldest.Count;
             index++) {
            SessionSelectedLineageAuditEntry entry =
                page.HeadToOldest[index];
            if (entry.Address == default
                || entry.BodySchemaVersion <= 0
                || entry.PayloadSha256.Length != 64
                || entry.PayloadSha256.Any(static character =>
                    !((character >= '0' && character <= '9')
                      || (character >= 'a'
                          && character <= 'f')))
                || (index > 0
                    && page.HeadToOldest[index - 1].Parent
                        != entry.Address)
                || (childSequenceExclusive is { } child
                    && entry.SequenceNumber >= child)) {
                throw new InvalidDataException(
                    $"Rebuild audit page entry {index} is invalid."
                );
            }
            childSequenceExclusive = entry.SequenceNumber;
        }
    }

    private static byte[] Write(Action<Utf8JsonWriter> write) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            write(writer);
            writer.Flush();
        }
        return buffer.ToArray();
    }

    private static JsonDocument Parse(
        ReadOnlySpan<byte> bytes,
        string label
    ) {
        try {
            return JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                }
            );
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"Invalid JSON in {label}.",
                exception
            );
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        string label,
        params string[] expected
    ) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException($"{label} must be an object.");
        }
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (!observed.Add(property.Name)
                || !expectedSet.Contains(property.Name)) {
                throw new InvalidDataException(
                    $"{label} contains duplicate or unknown property '{property.Name}'."
                );
            }
        }
        if (!observed.SetEquals(expectedSet)) {
            throw new InvalidDataException(
                $"{label} is missing one or more required properties."
            );
        }
    }

    private static void RequireSchema(
        JsonElement root,
        string expected,
        string label
    ) {
        if (!string.Equals(
                ReadRequiredString(root, "schema", 1, 128),
                expected,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Unsupported {label} schema."
            );
        }
    }

    private static void RequireCanonical(
        ReadOnlySpan<byte> observed,
        ReadOnlySpan<byte> canonical,
        string label
    ) {
        if (!observed.SequenceEqual(canonical)) {
            throw new InvalidDataException(
                $"{label} is not in canonical encoding."
            );
        }
    }

    private static string ReadCampaignId(
        JsonElement root,
        string property
    ) => ValidateCampaignId(
        ReadRequiredString(root, property, 32, 32)
    );

    public static string ValidateCampaignId(string campaignId) {
        ArgumentNullException.ThrowIfNull(campaignId);
        if (campaignId.Length != 32
            || campaignId.Any(static character =>
                !((character >= '0' && character <= '9')
                  || (character >= 'a' && character <= 'f')))) {
            throw new ArgumentException(
                "Rebuild campaign id must be 32 lowercase hex characters.",
                nameof(campaignId)
            );
        }
        return campaignId;
    }

    private static string ReadRequiredString(
        JsonElement root,
        string property,
        int minLength,
        int maxLength
    ) {
        JsonElement element = root.GetProperty(property);
        if (element.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException(
                $"Property '{property}' must be a string."
            );
        }
        string value = element.GetString()!;
        if (value.Length < minLength || value.Length > maxLength) {
            throw new InvalidDataException(
                $"Property '{property}' length is invalid."
            );
        }
        return value;
    }

    private static RefId ReadRefId(
        JsonElement root,
        string property
    ) {
        string value = ReadRequiredString(root, property, 16, 16);
        var result = RefId.ParseHex(value);
        if (result.IsFailure || result.Unwrap() == default) {
            throw new InvalidDataException(
                $"Property '{property}' is not a valid RefId."
            );
        }
        return result.Unwrap();
    }

    private static EventAddress ReadAddress(
        JsonElement root,
        string property
    ) {
        string value = ReadRequiredString(root, property, 1, 128);
        try {
            EventAddress address = EventAddressTextCodec.Parse(value);
            if (address == default) {
                throw new InvalidDataException(
                    $"Property '{property}' cannot be the default address."
                );
            }
            return address;
        }
        catch (FormatException exception) {
            throw new InvalidDataException(
                $"Property '{property}' is not an EventAddress.",
                exception
            );
        }
    }

    private static EventAddress? ReadNullableAddress(
        JsonElement root,
        string property
    ) => root.GetProperty(property).ValueKind == JsonValueKind.Null
        ? null
        : ReadAddress(root, property);

    private static int ReadInt32(JsonElement root, string property) {
        if (!root.GetProperty(property).TryGetInt32(out int value)) {
            throw new InvalidDataException(
                $"Property '{property}' must be an Int32."
            );
        }
        return value;
    }

    private static long ReadInt64(JsonElement root, string property) {
        if (!root.GetProperty(property).TryGetInt64(out long value)) {
            throw new InvalidDataException(
                $"Property '{property}' must be an Int64."
            );
        }
        return value;
    }

    private static uint ReadUInt32(JsonElement root, string property) {
        if (!root.GetProperty(property).TryGetUInt32(out uint value)) {
            throw new InvalidDataException(
                $"Property '{property}' must be a UInt32."
            );
        }
        return value;
    }

    private static ulong ReadUInt64(JsonElement root, string property) {
        if (!root.GetProperty(property).TryGetUInt64(out ulong value)) {
            throw new InvalidDataException(
                $"Property '{property}' must be a UInt64."
            );
        }
        return value;
    }

    private static string ReadSha256(
        JsonElement root,
        string property
    ) {
        string value = ReadRequiredString(root, property, 64, 64);
        _ = DecodeSha256(value, property);
        return value;
    }

    private static byte[] DecodeSha256(string value, string label) {
        if (value.Length != 64
            || value.Any(static character =>
                !((character >= '0' && character <= '9')
                  || (character >= 'a' && character <= 'f')))) {
            throw new InvalidDataException(
                $"{label} must be lowercase SHA-256 hex."
            );
        }
        try {
            return Convert.FromHexString(value);
        }
        catch (FormatException exception) {
            throw new InvalidDataException(
                $"{label} must be SHA-256 hex.",
                exception
            );
        }
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void WriteAddress(
        Utf8JsonWriter writer,
        string property,
        EventAddress address
    ) => writer.WriteString(
        property,
        EventAddressTextCodec.Format(address)
    );

    private static void WriteNullableAddress(
        Utf8JsonWriter writer,
        string property,
        EventAddress? address
    ) {
        if (address is { } value) {
            WriteAddress(writer, property, value);
        }
        else {
            writer.WriteNull(property);
        }
    }

    private static void WriteSetups(
        Utf8JsonWriter writer,
        string property,
        SessionContextAnchorSetupReferences setups
    ) {
        writer.WriteStartObject(property);
        WriteSetupReference(
            writer,
            "runtimeConfig",
            setups.RuntimeConfig
        );
        WriteSetupReference(
            writer,
            "systemPrompt",
            setups.SystemPrompt
        );
        writer.WriteEndObject();
    }

    private static void WriteSetupReference(
        Utf8JsonWriter writer,
        string property,
        SessionContextSetupReference reference
    ) {
        writer.WriteStartObject(property);
        WriteAddress(writer, "address", reference.Address);
        writer.WriteNumber(
            "bodySchemaVersion",
            reference.BodySchemaVersion
        );
        writer.WriteString("payloadSha256", reference.PayloadSha256);
        writer.WriteEndObject();
    }

    private static SessionContextAnchorSetupReferences ReadSetups(
        JsonElement root,
        string property
    ) {
        JsonElement element = root.GetProperty(property);
        RequireExactProperties(
            element,
            property,
            "runtimeConfig",
            "systemPrompt"
        );
        return new SessionContextAnchorSetupReferences(
            ReadSetupReference(element, "runtimeConfig"),
            ReadSetupReference(element, "systemPrompt")
        );
    }

    private static SessionContextSetupReference ReadSetupReference(
        JsonElement root,
        string property
    ) {
        JsonElement element = root.GetProperty(property);
        RequireExactProperties(
            element,
            property,
            "address",
            "bodySchemaVersion",
            "payloadSha256"
        );
        int schemaVersion = ReadInt32(element, "bodySchemaVersion");
        if (schemaVersion <= 0) {
            throw new InvalidDataException(
                $"{property} setup schema version is invalid."
            );
        }
        return new SessionContextSetupReference(
            ReadAddress(element, "address"),
            schemaVersion,
            ReadSha256(element, "payloadSha256")
        );
    }
}
