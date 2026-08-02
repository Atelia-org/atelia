using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

public static class DerivedRecapCodec {
    public const string StoreSchema =
        "atelia.session-journal.derived-recap-store.v4";
    public const string ManifestSchema =
        "atelia.session-journal.derived-recap-manifest.v6";
    public const string FrozenInputSchema =
        "atelia.session-journal.derived-recap-frozen-input.v5";
    public const string BlockSchema =
        "atelia.session-journal.derived-recap-block.v4";
    public const string PublicationSchema =
        "atelia.session-journal.published-recap-set.v6";

    private static readonly JsonWriterOptions WriterOptions = new() {
        Indented = false,
        Encoder =
            System.Text.Encodings.Web.JavaScriptEncoder
                .UnsafeRelaxedJsonEscaping,
        SkipValidation = false
    };

    public static DerivedRecapSetManifest CreateManifest(
        RefId refId,
        EventAddress setAdmissionAnchor,
        SessionContextAnchorSetupReferences setAdmissionAnchorSetups,
        IReadOnlyList<RecapBlockPlan> blocks
    ) {
        ArgumentNullException.ThrowIfNull(blocks);
        var provisional = new DerivedRecapSetManifest(
            ManifestSchema,
            refId,
            setAdmissionAnchor,
            setAdmissionAnchorSetups,
            Array.AsReadOnly(blocks.ToArray()),
            string.Empty
        );
        ValidateManifestShape(provisional, requireHash: false);
        return provisional with {
            ManifestPayloadSha256 =
                Sha256Hex(EncodeManifestHashProjection(provisional))
        };
    }

    public static DerivedRecapFrozenInput CreateFrozenInput(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        EventAddress absorbedThrough,
        SessionContextAnchorSetupReferences absorbedThroughSetups,
        string content
    ) {
        var provisional = new DerivedRecapFrozenInput(
            FrozenInputSchema,
            recapBlockId,
            target,
            absorbedThrough,
            absorbedThroughSetups,
            content,
            string.Empty
        );
        ValidateFrozenInputShape(provisional, requireHash: false);
        return provisional with {
            PayloadSha256 =
                Sha256Hex(EncodeFrozenInputHashProjection(provisional))
        };
    }

    public static DerivedRecapBlock CreateBlock(
        RecapBlockPlan plan,
        EventAddress absorbedThrough,
        string content
    ) {
        ArgumentNullException.ThrowIfNull(plan);
        var provisional = new DerivedRecapBlock(
            BlockSchema,
            plan.RecapBlockId,
            plan.Target,
            ComputeBlockPlanSha256(plan),
            absorbedThrough,
            content,
            string.Empty
        );
        ValidateBlockShape(provisional, requireHash: false);
        return provisional with {
            PayloadSha256 =
                Sha256Hex(EncodeBlockHashProjection(provisional))
        };
    }

    public static PublishedRecapSet CreatePublication(
        DerivedRecapSetManifest manifest,
        IReadOnlyList<DerivedRecapBlock> blocks
    ) {
        ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(blocks);
        RecapBlockCommitment[] commitments = [
            .. blocks.Select(static block => new RecapBlockCommitment(
                block.RecapBlockId,
                block.Target,
                block.AbsorbedThrough,
                block.PayloadSha256
            ))
        ];
        var provisional = new PublishedRecapSet(
            PublicationSchema,
            manifest.RefId,
            manifest.SetAdmissionAnchor,
            manifest,
            Array.AsReadOnly(commitments),
            string.Empty
        );
        ValidatePublicationShape(provisional, requireHash: false);
        return provisional with {
            EnvelopeSha256 =
                Sha256Hex(EncodePublicationHashProjection(provisional))
        };
    }

    public static string ComputeBlockPlanSha256(RecapBlockPlan plan) {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlanShape(plan);
        return Sha256Hex(EncodePlan(plan));
    }

    internal static byte[] EncodeStoreHeader(RefId refId) {
        ValidateRefId(refId, "store.refId");
        return Write(writer => {
            writer.WriteStartObject();
            writer.WriteString("schema", StoreSchema);
            writer.WriteString("refId", refId.ToHexString());
            writer.WriteEndObject();
        });
    }

    internal static RefId DecodeStoreHeader(ReadOnlySpan<byte> bytes) {
        using JsonDocument document = Parse(bytes, "Recap Store header");
        JsonElement root = document.RootElement;
        RequireExactProperties(root, "store header", "schema", "refId");
        RequireSchema(root, StoreSchema, "store header");
        return ReadRefId(root, "refId");
    }

    internal static byte[] EncodeManifest(
        DerivedRecapSetManifest manifest
    ) {
        ValidateManifest(manifest);
        return Write(writer =>
            WriteManifest(writer, manifest, includeHash: true));
    }

    internal static DerivedRecapSetManifest DecodeManifest(
        ReadOnlySpan<byte> bytes
    ) {
        using JsonDocument document = Parse(bytes, "Recap manifest");
        DerivedRecapSetManifest manifest =
            ReadManifest(document.RootElement);
        ValidateManifest(manifest);
        return manifest;
    }

    internal static byte[] EncodeFrozenInput(
        DerivedRecapFrozenInput input
    ) {
        ValidateFrozenInput(input);
        return Write(writer =>
            WriteFrozenInput(writer, input, includeHash: true));
    }

    internal static DerivedRecapFrozenInput DecodeFrozenInput(
        ReadOnlySpan<byte> bytes
    ) {
        using JsonDocument document =
            Parse(bytes, "Recap frozen input");
        DerivedRecapFrozenInput input =
            ReadFrozenInput(document.RootElement);
        ValidateFrozenInput(input);
        return input;
    }

    internal static byte[] EncodeBlock(DerivedRecapBlock block) {
        ValidateBlock(block);
        return Write(writer =>
            WriteBlock(writer, block, includeHash: true));
    }

    internal static DerivedRecapBlock DecodeBlock(
        ReadOnlySpan<byte> bytes
    ) {
        using JsonDocument document = Parse(bytes, "Recap block");
        DerivedRecapBlock block = ReadBlock(document.RootElement);
        ValidateBlock(block);
        return block;
    }

    internal static byte[] EncodePublication(
        PublishedRecapSet publication
    ) {
        ValidatePublication(publication);
        return Write(writer =>
            WritePublication(writer, publication, includeHash: true));
    }

    internal static PublishedRecapSet DecodePublication(
        ReadOnlySpan<byte> bytes
    ) {
        using JsonDocument document =
            Parse(bytes, "Recap publication");
        PublishedRecapSet publication =
            ReadPublication(document.RootElement);
        ValidatePublication(publication);
        if (!bytes.SequenceEqual(EncodePublication(publication))) {
            throw new InvalidDataException(
                "Recap publication bytes are not canonical."
            );
        }
        return publication;
    }

    internal static void ValidateManifest(
        DerivedRecapSetManifest manifest
    ) {
        ValidateManifestShape(manifest, requireHash: true);
        RequireMatchingHash(
            manifest.ManifestPayloadSha256,
            EncodeManifestHashProjection(manifest),
            "manifestPayloadSha256"
        );
    }

    internal static void ValidateFrozenInput(
        DerivedRecapFrozenInput input
    ) {
        ValidateFrozenInputShape(input, requireHash: true);
        RequireMatchingHash(
            input.PayloadSha256,
            EncodeFrozenInputHashProjection(input),
            "frozen input payloadSha256"
        );
    }

    internal static void ValidateBlock(DerivedRecapBlock block) {
        ValidateBlockShape(block, requireHash: true);
        RequireMatchingHash(
            block.PayloadSha256,
            EncodeBlockHashProjection(block),
            "block payloadSha256"
        );
    }

    internal static void ValidatePublication(
        PublishedRecapSet publication
    ) {
        ValidatePublicationShape(publication, requireHash: true);
        RequireMatchingHash(
            publication.EnvelopeSha256,
            EncodePublicationHashProjection(publication),
            "envelopeSha256"
        );
    }

    private static byte[] EncodeManifestHashProjection(
        DerivedRecapSetManifest manifest
    ) => Write(writer =>
        WriteManifest(writer, manifest, includeHash: false));

    private static byte[] EncodeFrozenInputHashProjection(
        DerivedRecapFrozenInput input
    ) => Write(writer =>
        WriteFrozenInput(writer, input, includeHash: false));

    private static byte[] EncodeBlockHashProjection(
        DerivedRecapBlock block
    ) => Write(writer =>
        WriteBlock(writer, block, includeHash: false));

    private static byte[] EncodePublicationHashProjection(
        PublishedRecapSet publication
    ) => Write(writer =>
        WritePublication(writer, publication, includeHash: false));

    private static byte[] EncodePlan(RecapBlockPlan plan)
        => Write(writer => WritePlan(writer, plan));

    private static byte[] Write(Action<Utf8JsonWriter> action) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            action(writer);
        }
        return buffer.WrittenMemory.ToArray();
    }

    private static void WriteManifest(
        Utf8JsonWriter writer,
        DerivedRecapSetManifest manifest,
        bool includeHash
    ) {
        writer.WriteStartObject();
        writer.WriteString("schema", manifest.Schema);
        writer.WriteString("refId", manifest.RefId.ToHexString());
        writer.WriteString(
            "setAdmissionAnchor",
            EventAddressTextCodec.Format(manifest.SetAdmissionAnchor)
        );
        WriteSetups(
            writer,
            "setAdmissionAnchorSetups",
            manifest.SetAdmissionAnchorSetups
        );
        writer.WriteStartArray("blocks");
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            WritePlan(writer, plan);
        }
        writer.WriteEndArray();
        if (includeHash) {
            writer.WriteString(
                "manifestPayloadSha256",
                manifest.ManifestPayloadSha256
            );
        }
        writer.WriteEndObject();
    }

    private static void WritePlan(
        Utf8JsonWriter writer,
        RecapBlockPlan plan
    ) {
        writer.WriteStartObject();
        switch (plan) {
            case InheritRecapBlockPlan inherit:
                writer.WriteString("mode", "inherit");
                WritePlanCommon(writer, inherit);
                writer.WriteString(
                    "sourceSetAnchor",
                    EventAddressTextCodec.Format(
                        inherit.SourceSetAnchor
                    )
                );
                WriteSetups(
                    writer,
                    "sourceAbsorbedThroughSetups",
                    inherit.SourceAbsorbedThroughSetups
                );
                writer.WriteString(
                    "sourcePublicationEnvelopeSha256",
                    inherit.SourcePublicationEnvelopeSha256
                );
                writer.WriteString(
                    "sourceInputPayloadSha256",
                    inherit.SourceInputPayloadSha256
                );
                writer.WriteNumber(
                    "maxContentUtf8Bytes",
                    inherit.MaxContentUtf8Bytes
                );
                break;
            case MaintainRecapBlockPlan maintain:
                writer.WriteString("mode", "maintain");
                WritePlanCommon(writer, maintain);
                writer.WriteString(
                    "maintainerId",
                    maintain.MaintainerId
                );
                writer.WriteString(
                    "maintainerCapabilityFingerprint",
                    maintain.MaintainerCapabilityFingerprint
                );
                writer.WritePropertyName("source");
                WriteMaintainSource(writer, maintain.Source);
                writer.WriteStartArray("catchUpBoundaries");
                foreach (RecapReplayBoundary boundary
                         in maintain.CatchUpBoundaries) {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "address",
                        EventAddressTextCodec.Format(boundary.Address)
                    );
                    WriteSetups(writer, "setups", boundary.Setups);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WritePropertyName("priorContext");
                WritePriorContext(writer, maintain.PriorContext);
                writer.WriteNumber(
                    "maxContentUtf8Bytes",
                    maintain.MaxContentUtf8Bytes
                );
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported RecapBlockPlan '{plan.GetType().Name}'."
                );
        }
        writer.WriteEndObject();
    }

    private static void WritePlanCommon(
        Utf8JsonWriter writer,
        RecapBlockPlan plan
    ) {
        writer.WriteString(
            "recapBlockId",
            plan.RecapBlockId.Value
        );
        writer.WritePropertyName("target");
        WriteTarget(writer, plan.Target);
    }

    private static void WriteMaintainSource(
        Utf8JsonWriter writer,
        RecapMaintainSource source
    ) {
        writer.WriteStartObject();
        switch (source) {
            case ExistingRecapMaintainSource existing:
                writer.WriteString("kind", "existing");
                writer.WriteString(
                    "sourceSetAnchor",
                    EventAddressTextCodec.Format(
                        existing.SourceSetAnchor
                    )
                );
                WriteSetups(
                    writer,
                    "replayStartSetups",
                    existing.ReplayStartSetups
                );
                writer.WriteString(
                    "sourcePublicationEnvelopeSha256",
                    existing.SourcePublicationEnvelopeSha256
                );
                writer.WriteString(
                    "sourceInputPayloadSha256",
                    existing.SourceInputPayloadSha256
                );
                break;
            case EmptyRecapMaintainSource empty:
                writer.WriteString("kind", "empty");
                writer.WriteString(
                    "replayStartExclusive",
                    EventAddressTextCodec.Format(
                        empty.ReplayStartExclusive
                    )
                );
                WriteSetups(
                    writer,
                    "replayStartSetups",
                    empty.ReplayStartSetups
                );
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported RecapMaintainSource '{source.GetType().Name}'."
                );
        }
        writer.WriteEndObject();
    }

    private static void WritePriorContext(
        Utf8JsonWriter writer,
        RecapPriorContext priorContext
    ) {
        writer.WriteStartObject();
        switch (priorContext) {
            case EmptyRecapPriorContext:
                writer.WriteString("kind", "empty");
                break;
            case InlineRecapPriorContext inline:
                writer.WriteString("kind", "inline");
                writer.WriteString(
                    "admissionAnchor",
                    EventAddressTextCodec.Format(
                        inline.AdmissionAnchor
                    )
                );
                writer.WritePropertyName("snapshot");
                WriteSnapshot(writer, inline.Snapshot);
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported RecapPriorContext '{priorContext.GetType().Name}'."
                );
        }
        writer.WriteEndObject();
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        ContextHeaderSnapshot snapshot
    ) {
        writer.WriteStartObject();
        writer.WriteString(
            "systemPromptFragment",
            snapshot.SystemPromptFragment
        );
        writer.WriteString(
            "observationMessage",
            snapshot.ObservationMessage
        );
        writer.WriteString(
            "actionMessage",
            snapshot.ActionMessage
        );
        writer.WriteEndObject();
    }

    private static void WriteFrozenInput(
        Utf8JsonWriter writer,
        DerivedRecapFrozenInput input,
        bool includeHash
    ) {
        writer.WriteStartObject();
        writer.WriteString("schema", input.Schema);
        writer.WriteString(
            "recapBlockId",
            input.RecapBlockId.Value
        );
        writer.WritePropertyName("target");
        WriteTarget(writer, input.Target);
        writer.WriteString(
            "absorbedThrough",
            EventAddressTextCodec.Format(input.AbsorbedThrough)
        );
        WriteSetups(
            writer,
            "absorbedThroughSetups",
            input.AbsorbedThroughSetups
        );
        writer.WriteString("content", input.Content);
        if (includeHash) {
            writer.WriteString("payloadSha256", input.PayloadSha256);
        }
        writer.WriteEndObject();
    }

    private static void WriteBlock(
        Utf8JsonWriter writer,
        DerivedRecapBlock block,
        bool includeHash
    ) {
        writer.WriteStartObject();
        writer.WriteString("schema", block.Schema);
        writer.WriteString(
            "recapBlockId",
            block.RecapBlockId.Value
        );
        writer.WritePropertyName("target");
        WriteTarget(writer, block.Target);
        writer.WriteString(
            "blockPlanSha256",
            block.BlockPlanSha256
        );
        writer.WriteString(
            "absorbedThrough",
            EventAddressTextCodec.Format(block.AbsorbedThrough)
        );
        writer.WriteString("content", block.Content);
        if (includeHash) {
            writer.WriteString("payloadSha256", block.PayloadSha256);
        }
        writer.WriteEndObject();
    }

    private static void WritePublication(
        Utf8JsonWriter writer,
        PublishedRecapSet publication,
        bool includeHash
    ) {
        writer.WriteStartObject();
        writer.WriteString("schema", publication.Schema);
        writer.WriteString(
            "refId",
            publication.RefId.ToHexString()
        );
        writer.WriteString(
            "setAdmissionAnchor",
            EventAddressTextCodec.Format(
                publication.SetAdmissionAnchor
            )
        );
        writer.WritePropertyName("frozenPlanSnapshot");
        WriteManifest(
            writer,
            publication.FrozenPlanSnapshot,
            includeHash: true
        );
        writer.WriteStartArray("blockCommitments");
        foreach (RecapBlockCommitment commitment
                 in publication.BlockCommitments) {
            writer.WriteStartObject();
            writer.WriteString(
                "recapBlockId",
                commitment.RecapBlockId.Value
            );
            writer.WritePropertyName("target");
            WriteTarget(writer, commitment.Target);
            writer.WriteString(
                "absorbedThrough",
                EventAddressTextCodec.Format(
                    commitment.AbsorbedThrough
                )
            );
            writer.WriteString(
                "payloadSha256",
                commitment.PayloadSha256
            );
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (includeHash) {
            writer.WriteString(
                "envelopeSha256",
                publication.EnvelopeSha256
            );
        }
        writer.WriteEndObject();
    }

    private static void WriteSetups(
        Utf8JsonWriter writer,
        string propertyName,
        SessionContextAnchorSetupReferences setups
    ) {
        writer.WriteStartObject(propertyName);
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
        string propertyName,
        SessionContextSetupReference reference
    ) {
        writer.WriteStartObject(propertyName);
        writer.WriteString(
            "address",
            EventAddressTextCodec.Format(reference.Address)
        );
        writer.WriteNumber(
            "bodySchemaVersion",
            reference.BodySchemaVersion
        );
        writer.WriteString("payloadSha256", reference.PayloadSha256);
        writer.WriteEndObject();
    }

    private static void WriteTarget(
        Utf8JsonWriter writer,
        ContextHeaderBlockPath target
    ) {
        writer.WriteStartObject();
        writer.WriteString(
            "carrier",
            ContextHeaderCarrierTokens.ToStorageToken(
                target.Carrier
            )
        );
        writer.WriteString("blockKey", target.BlockKey);
        writer.WriteEndObject();
    }

    private static DerivedRecapSetManifest ReadManifest(
        JsonElement root
    ) {
        RequireObject(root, "manifest");
        RequireSchema(root, ManifestSchema, "manifest");
        RequireExactProperties(
            root,
            "manifest",
            "schema",
            "refId",
            "setAdmissionAnchor",
            "setAdmissionAnchorSetups",
            "blocks",
            "manifestPayloadSha256"
        );
        return new DerivedRecapSetManifest(
            ReadString(root, "schema"),
            ReadRefId(root, "refId"),
            ReadAddress(root, "setAdmissionAnchor"),
            ReadSetups(ReadObject(root, "setAdmissionAnchorSetups")),
            Array.AsReadOnly(
                ReadArray(root, "blocks")
                    .Select(ReadPlan)
                    .ToArray()
            ),
            ReadString(root, "manifestPayloadSha256")
        );
    }

    private static RecapBlockPlan ReadPlan(JsonElement element) {
        RequireObject(element, "block plan");
        string mode = ReadString(element, "mode");
        return mode switch {
            "inherit" => ReadInheritPlan(element),
            "maintain" => ReadMaintainPlan(element),
            _ => throw new InvalidDataException(
                $"Unknown RecapBlockPlan mode '{mode}'."
            )
        };
    }

    private static InheritRecapBlockPlan ReadInheritPlan(
        JsonElement element
    ) {
        RequireExactProperties(
            element,
            "inherit block plan",
            "mode",
            "recapBlockId",
            "target",
            "sourceSetAnchor",
            "sourceAbsorbedThroughSetups",
            "sourcePublicationEnvelopeSha256",
            "sourceInputPayloadSha256",
            "maxContentUtf8Bytes"
        );
        return new InheritRecapBlockPlan(
            ReadBlockId(element, "recapBlockId"),
            ReadTarget(ReadObject(element, "target")),
            ReadAddress(element, "sourceSetAnchor"),
            ReadSetups(
                ReadObject(element, "sourceAbsorbedThroughSetups")
            ),
            ReadString(
                element,
                "sourcePublicationEnvelopeSha256"
            ),
            ReadString(element, "sourceInputPayloadSha256"),
            ReadInt32(element, "maxContentUtf8Bytes")
        );
    }

    private static MaintainRecapBlockPlan ReadMaintainPlan(
        JsonElement element
    ) {
        RequireExactProperties(
            element,
            "maintain block plan",
            "mode",
            "recapBlockId",
            "target",
            "maintainerId",
            "maintainerCapabilityFingerprint",
            "source",
            "catchUpBoundaries",
            "priorContext",
            "maxContentUtf8Bytes"
        );
        return new MaintainRecapBlockPlan(
            ReadBlockId(element, "recapBlockId"),
            ReadTarget(ReadObject(element, "target")),
            ReadString(element, "maintainerId"),
            ReadString(
                element,
                "maintainerCapabilityFingerprint"
            ),
            ReadMaintainSource(ReadObject(element, "source")),
            Array.AsReadOnly(
                ReadArray(element, "catchUpBoundaries")
                    .Select(ReadReplayBoundary)
                    .ToArray()
            ),
            ReadPriorContext(ReadObject(element, "priorContext")),
            ReadInt32(element, "maxContentUtf8Bytes")
        );
    }

    private static RecapMaintainSource ReadMaintainSource(
        JsonElement element
    ) {
        string kind = ReadString(element, "kind");
        switch (kind) {
            case "existing":
                RequireExactProperties(
                    element,
                    "existing source",
                    "kind",
                    "sourceSetAnchor",
                    "replayStartSetups",
                    "sourcePublicationEnvelopeSha256",
                    "sourceInputPayloadSha256"
                );
                return new ExistingRecapMaintainSource(
                    ReadAddress(element, "sourceSetAnchor"),
                    ReadSetups(
                        ReadObject(element, "replayStartSetups")
                    ),
                    ReadString(
                        element,
                        "sourcePublicationEnvelopeSha256"
                    ),
                    ReadString(
                        element,
                        "sourceInputPayloadSha256"
                    )
                );
            case "empty":
                RequireExactProperties(
                    element,
                    "empty source",
                    "kind",
                    "replayStartExclusive",
                    "replayStartSetups"
                );
                return new EmptyRecapMaintainSource(
                    ReadAddress(element, "replayStartExclusive"),
                    ReadSetups(
                        ReadObject(element, "replayStartSetups")
                    )
                );
            default:
                throw new InvalidDataException(
                    $"Unknown RecapMaintainSource kind '{kind}'."
                );
        }
    }

    private static RecapPriorContext ReadPriorContext(
        JsonElement element
    ) {
        string kind = ReadString(element, "kind");
        switch (kind) {
            case "empty":
                RequireExactProperties(
                    element,
                    "empty prior context",
                    "kind"
                );
                return EmptyRecapPriorContext.Instance;
            case "inline":
                RequireExactProperties(
                    element,
                    "inline prior context",
                    "kind",
                    "admissionAnchor",
                    "snapshot"
                );
                return new InlineRecapPriorContext(
                    ReadAddress(element, "admissionAnchor"),
                    ReadSnapshot(ReadObject(element, "snapshot"))
                );
            default:
                throw new InvalidDataException(
                    $"Unknown RecapPriorContext kind '{kind}'."
                );
        }
    }

    private static ContextHeaderSnapshot ReadSnapshot(
        JsonElement element
    ) {
        RequireExactProperties(
            element,
            "context header snapshot",
            "systemPromptFragment",
            "observationMessage",
            "actionMessage"
        );
        return new ContextHeaderSnapshot(
            ReadString(element, "systemPromptFragment"),
            ReadString(element, "observationMessage"),
            ReadString(element, "actionMessage")
        );
    }

    private static DerivedRecapFrozenInput ReadFrozenInput(
        JsonElement root
    ) {
        RequireObject(root, "frozen input");
        RequireSchema(root, FrozenInputSchema, "frozen input");
        RequireExactProperties(
            root,
            "frozen input",
            "schema",
            "recapBlockId",
            "target",
            "absorbedThrough",
            "absorbedThroughSetups",
            "content",
            "payloadSha256"
        );
        return new DerivedRecapFrozenInput(
            ReadString(root, "schema"),
            ReadBlockId(root, "recapBlockId"),
            ReadTarget(ReadObject(root, "target")),
            ReadAddress(root, "absorbedThrough"),
            ReadSetups(ReadObject(root, "absorbedThroughSetups")),
            ReadString(root, "content"),
            ReadString(root, "payloadSha256")
        );
    }

    private static DerivedRecapBlock ReadBlock(JsonElement root) {
        RequireExactProperties(
            root,
            "block",
            "schema",
            "recapBlockId",
            "target",
            "blockPlanSha256",
            "absorbedThrough",
            "content",
            "payloadSha256"
        );
        RequireSchema(root, BlockSchema, "block");
        return new DerivedRecapBlock(
            ReadString(root, "schema"),
            ReadBlockId(root, "recapBlockId"),
            ReadTarget(ReadObject(root, "target")),
            ReadString(root, "blockPlanSha256"),
            ReadAddress(root, "absorbedThrough"),
            ReadString(root, "content"),
            ReadString(root, "payloadSha256")
        );
    }

    private static PublishedRecapSet ReadPublication(
        JsonElement root
    ) {
        RequireExactProperties(
            root,
            "publication",
            "schema",
            "refId",
            "setAdmissionAnchor",
            "frozenPlanSnapshot",
            "blockCommitments",
            "envelopeSha256"
        );
        RequireSchema(root, PublicationSchema, "publication");
        var commitments = new List<RecapBlockCommitment>();
        foreach (JsonElement element
                 in ReadArray(root, "blockCommitments")) {
            RequireExactProperties(
                element,
                "block commitment",
                "recapBlockId",
                "target",
                "absorbedThrough",
                "payloadSha256"
            );
            commitments.Add(new RecapBlockCommitment(
                ReadBlockId(element, "recapBlockId"),
                ReadTarget(ReadObject(element, "target")),
                ReadAddress(element, "absorbedThrough"),
                ReadString(element, "payloadSha256")
            ));
        }
        return new PublishedRecapSet(
            ReadString(root, "schema"),
            ReadRefId(root, "refId"),
            ReadAddress(root, "setAdmissionAnchor"),
            ReadManifest(ReadObject(root, "frozenPlanSnapshot")),
            Array.AsReadOnly(commitments.ToArray()),
            ReadString(root, "envelopeSha256")
        );
    }

    private static RecapReplayBoundary ReadReplayBoundary(
        JsonElement element
    ) {
        RequireExactProperties(
            element,
            "replay boundary",
            "address",
            "setups"
        );
        return new RecapReplayBoundary(
            ReadAddress(element, "address"),
            ReadSetups(ReadObject(element, "setups"))
        );
    }

    private static SessionContextAnchorSetupReferences ReadSetups(
        JsonElement element
    ) {
        RequireExactProperties(
            element,
            "setup references",
            "runtimeConfig",
            "systemPrompt"
        );
        return new SessionContextAnchorSetupReferences(
            ReadSetupReference(
                ReadObject(element, "runtimeConfig"),
                "runtimeConfig"
            ),
            ReadSetupReference(
                ReadObject(element, "systemPrompt"),
                "systemPrompt"
            )
        );
    }

    private static SessionContextSetupReference ReadSetupReference(
        JsonElement element,
        string path
    ) {
        RequireExactProperties(
            element,
            $"{path} setup reference",
            "address",
            "bodySchemaVersion",
            "payloadSha256"
        );
        return new SessionContextSetupReference(
            ReadAddress(element, "address"),
            ReadInt32(element, "bodySchemaVersion"),
            ReadString(element, "payloadSha256")
        );
    }

    private static ContextHeaderBlockPath ReadTarget(
        JsonElement element
    ) {
        RequireExactProperties(
            element,
            "target",
            "carrier",
            "blockKey"
        );
        string carrierToken = ReadString(element, "carrier");
        if (!ContextHeaderCarrierTokens.TryParseStorageToken(
                carrierToken,
                out ContextHeaderCarrier carrier
            )) {
            throw new InvalidDataException(
                $"Unknown context-header carrier '{carrierToken}'."
            );
        }
        return new ContextHeaderBlockPath(
            carrier,
            ReadString(element, "blockKey")
        );
    }

    private static void ValidateManifestShape(
        DerivedRecapSetManifest manifest,
        bool requireHash
    ) {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(
                manifest.Schema,
                ManifestSchema,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Recap manifest schema is invalid."
            );
        }
        ValidateRefId(manifest.RefId, "manifest.refId");
        ValidateAddress(
            manifest.SetAdmissionAnchor,
            "manifest.setAdmissionAnchor"
        );
        ValidateSetups(
            manifest.SetAdmissionAnchorSetups,
            "manifest.setAdmissionAnchorSetups"
        );
        ArgumentNullException.ThrowIfNull(manifest.Blocks);
        if (manifest.Blocks.Count is 0
            or > SessionContextContributionContract
                .MaxContributionCount) {
            throw new InvalidDataException(
                "Recap manifest requires 1 through 128 block plans."
            );
        }
        var ids = new HashSet<RecapBlockId>();
        var targets =
            new HashSet<(ContextHeaderCarrier Carrier, string Key)>();
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            ValidatePlanShape(plan);
            if (plan is MaintainRecapBlockPlan maintain
                && (maintain.CatchUpBoundaries[^1].Address
                        != manifest.SetAdmissionAnchor
                    || maintain.CatchUpBoundaries[^1].Setups
                        != manifest.SetAdmissionAnchorSetups)) {
                throw new InvalidDataException(
                    "Maintain final replay boundary must equal the "
                    + "manifest admission address and setups."
                );
            }
            if (!ids.Add(plan.RecapBlockId)) {
                throw new InvalidDataException(
                    "Recap manifest contains duplicate RecapBlockId."
                );
            }
            if (!targets.Add((
                    plan.Target.Carrier,
                    plan.Target.BlockKey
                ))) {
                throw new InvalidDataException(
                    "Recap manifest contains duplicate targets."
                );
            }
        }
        if (requireHash) {
            ValidateSha256(
                manifest.ManifestPayloadSha256,
                "manifest.manifestPayloadSha256"
            );
        }
    }

    private static void ValidatePlanShape(RecapBlockPlan plan) {
        ArgumentNullException.ThrowIfNull(plan);
        _ = new RecapBlockId(plan.RecapBlockId.Value);
        ValidateTarget(plan.Target, "blockPlan.target");
        if (plan.MaxContentUtf8Bytes <= 0
            || plan.MaxContentUtf8Bytes
                > SessionContextContributionContract
                    .MaxContributionUtf8Bytes) {
            throw new InvalidDataException(
                "Recap block plan maxContentUtf8Bytes is invalid."
            );
        }
        switch (plan) {
            case InheritRecapBlockPlan inherit:
                ValidateAddress(
                    inherit.SourceSetAnchor,
                    "inherit.sourceSetAnchor"
                );
                ValidateSetups(
                    inherit.SourceAbsorbedThroughSetups,
                    "inherit.sourceAbsorbedThroughSetups"
                );
                ValidateSha256(
                    inherit.SourcePublicationEnvelopeSha256,
                    "inherit.sourcePublicationEnvelopeSha256"
                );
                ValidateSha256(
                    inherit.SourceInputPayloadSha256,
                    "inherit.sourceInputPayloadSha256"
                );
                break;
            case MaintainRecapBlockPlan maintain:
                ValidateToken(
                    maintain.MaintainerId,
                    256,
                    "maintain.maintainerId"
                );
                ValidateCapabilityFingerprint(
                    maintain.MaintainerCapabilityFingerprint,
                    "maintain.maintainerCapabilityFingerprint"
                );
                ValidateMaintainSource(maintain.Source);
                ArgumentNullException.ThrowIfNull(
                    maintain.CatchUpBoundaries
                );
                if (maintain.CatchUpBoundaries.Count == 0) {
                    throw new InvalidDataException(
                        "Maintain plan requires at least one catch-up endpoint."
                    );
                }
                foreach (RecapReplayBoundary boundary
                         in maintain.CatchUpBoundaries) {
                    ArgumentNullException.ThrowIfNull(boundary);
                    ValidateAddress(
                        boundary.Address,
                        "maintain.catchUpBoundaries.address"
                    );
                    ValidateSetups(
                        boundary.Setups,
                        "maintain.catchUpBoundaries.setups"
                    );
                }
                ValidatePriorContext(maintain.PriorContext);
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported RecapBlockPlan '{plan.GetType().Name}'."
                );
        }
    }

    private static void ValidateMaintainSource(
        RecapMaintainSource source
    ) {
        ArgumentNullException.ThrowIfNull(source);
        switch (source) {
            case ExistingRecapMaintainSource existing:
                ValidateAddress(
                    existing.SourceSetAnchor,
                    "source.sourceSetAnchor"
                );
                ValidateSetups(
                    existing.ReplayStartSetups,
                    "source.replayStartSetups"
                );
                ValidateSha256(
                    existing.SourcePublicationEnvelopeSha256,
                    "source.sourcePublicationEnvelopeSha256"
                );
                ValidateSha256(
                    existing.SourceInputPayloadSha256,
                    "source.sourceInputPayloadSha256"
                );
                break;
            case EmptyRecapMaintainSource empty:
                ValidateAddress(
                    empty.ReplayStartExclusive,
                    "source.replayStartExclusive"
                );
                ValidateSetups(
                    empty.ReplayStartSetups,
                    "source.replayStartSetups"
                );
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported RecapMaintainSource '{source.GetType().Name}'."
                );
        }
    }

    private static void ValidatePriorContext(
        RecapPriorContext priorContext
    ) {
        ArgumentNullException.ThrowIfNull(priorContext);
        switch (priorContext) {
            case EmptyRecapPriorContext:
                return;
            case InlineRecapPriorContext inline:
                ValidateAddress(
                    inline.AdmissionAnchor,
                    "priorContext.admissionAnchor"
                );
                ArgumentNullException.ThrowIfNull(inline.Snapshot);
                ValidateSnapshot(inline.Snapshot);
                return;
            default:
                throw new InvalidDataException(
                    $"Unsupported RecapPriorContext '{priorContext.GetType().Name}'."
                );
        }
    }

    private static void ValidateSnapshot(
        ContextHeaderSnapshot snapshot
    ) {
        ArgumentNullException.ThrowIfNull(snapshot.SystemPromptFragment);
        ArgumentNullException.ThrowIfNull(snapshot.ObservationMessage);
        ArgumentNullException.ThrowIfNull(snapshot.ActionMessage);
        long byteCount =
            (long)StrictUtf8ByteCount(
                snapshot.SystemPromptFragment,
                "snapshot.systemPromptFragment"
            )
            + StrictUtf8ByteCount(
                snapshot.ObservationMessage,
                "snapshot.observationMessage"
            )
            + StrictUtf8ByteCount(
                snapshot.ActionMessage,
                "snapshot.actionMessage"
            );
        if (byteCount > 4L * 1024 * 1024) {
            throw new InvalidDataException(
                "Inline prior context exceeds 4 MiB of UTF-8."
            );
        }
    }

    private static void ValidateFrozenInputShape(
        DerivedRecapFrozenInput input,
        bool requireHash
    ) {
        ArgumentNullException.ThrowIfNull(input);
        if (!string.Equals(
                input.Schema,
                FrozenInputSchema,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Recap frozen input schema is invalid."
            );
        }
        _ = new RecapBlockId(input.RecapBlockId.Value);
        ValidateTarget(input.Target, "frozenInput.target");
        ValidateAddress(
            input.AbsorbedThrough,
            "frozenInput.absorbedThrough"
        );
        ValidateSetups(
            input.AbsorbedThroughSetups,
            "frozenInput.absorbedThroughSetups"
        );
        ValidateContent(
            input.Content,
            SessionContextContributionContract
                .MaxContributionUtf8Bytes,
            "frozenInput.content",
            allowEmpty: true
        );
        if (requireHash) {
            ValidateSha256(
                input.PayloadSha256,
                "frozenInput.payloadSha256"
            );
        }
    }

    private static void ValidateBlockShape(
        DerivedRecapBlock block,
        bool requireHash
    ) {
        ArgumentNullException.ThrowIfNull(block);
        if (!string.Equals(
                block.Schema,
                BlockSchema,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Recap block schema is invalid."
            );
        }
        _ = new RecapBlockId(block.RecapBlockId.Value);
        ValidateTarget(block.Target, "block.target");
        ValidateSha256(
            block.BlockPlanSha256,
            "block.blockPlanSha256"
        );
        ValidateAddress(
            block.AbsorbedThrough,
            "block.absorbedThrough"
        );
        ValidateContent(
            block.Content,
            SessionContextContributionContract
                .MaxContributionUtf8Bytes,
            "block.content",
            allowEmpty: false
        );
        if (requireHash) {
            ValidateSha256(
                block.PayloadSha256,
                "block.payloadSha256"
            );
        }
    }

    private static void ValidatePublicationShape(
        PublishedRecapSet publication,
        bool requireHash
    ) {
        ArgumentNullException.ThrowIfNull(publication);
        if (!string.Equals(
                publication.Schema,
                PublicationSchema,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Recap publication schema is invalid."
            );
        }
        ValidateRefId(publication.RefId, "publication.refId");
        ValidateAddress(
            publication.SetAdmissionAnchor,
            "publication.setAdmissionAnchor"
        );
        ValidateManifest(publication.FrozenPlanSnapshot);
        if (publication.FrozenPlanSnapshot.RefId
                != publication.RefId
            || publication.FrozenPlanSnapshot.SetAdmissionAnchor
                != publication.SetAdmissionAnchor) {
            throw new InvalidDataException(
                "Recap publication does not match its frozen manifest."
            );
        }
        ArgumentNullException.ThrowIfNull(
            publication.BlockCommitments
        );
        if (publication.BlockCommitments.Count
            != publication.FrozenPlanSnapshot.Blocks.Count) {
            throw new InvalidDataException(
                "Recap publication commitment count is invalid."
            );
        }
        var ids = new HashSet<RecapBlockId>();
        for (int index = 0;
             index < publication.BlockCommitments.Count;
             index++) {
            RecapBlockCommitment commitment =
                publication.BlockCommitments[index]
                ?? throw new InvalidDataException(
                    "Recap publication contains a null commitment."
                );
            RecapBlockPlan plan =
                publication.FrozenPlanSnapshot.Blocks[index];
            _ = new RecapBlockId(commitment.RecapBlockId.Value);
            ValidateTarget(
                commitment.Target,
                "commitment.target"
            );
            ValidateAddress(
                commitment.AbsorbedThrough,
                "commitment.absorbedThrough"
            );
            ValidateSha256(
                commitment.PayloadSha256,
                "commitment.payloadSha256"
            );
            if (!ids.Add(commitment.RecapBlockId)
                || commitment.RecapBlockId != plan.RecapBlockId
                || commitment.Target != plan.Target) {
                throw new InvalidDataException(
                    "Recap publication commitments are not in exact manifest order."
                );
            }
        }
        if (requireHash) {
            ValidateSha256(
                publication.EnvelopeSha256,
                "publication.envelopeSha256"
            );
        }
    }

    private static void ValidateTarget(
        ContextHeaderBlockPath target,
        string name
    ) {
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(target.Carrier)
            || string.IsNullOrWhiteSpace(target.BlockKey)
            || target.BlockKey.Length
                > SessionContextContributionContract.MaxBlockKeyLength
            || target.BlockKey.Contains('\0', StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"{name} is invalid."
            );
        }
    }

    private static void ValidateContent(
        string content,
        int maxBytes,
        string name,
        bool allowEmpty
    ) {
        ArgumentNullException.ThrowIfNull(content);
        if (!allowEmpty && content.Length == 0) {
            throw new InvalidDataException($"{name} cannot be empty.");
        }
        if (StrictUtf8ByteCount(content, name) > maxBytes) {
            throw new InvalidDataException(
                $"{name} exceeds {maxBytes} UTF-8 bytes."
            );
        }
    }

    private static int StrictUtf8ByteCount(
        string value,
        string name
    ) {
        try {
            return new UTF8Encoding(false, true).GetByteCount(value);
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidDataException(
                $"{name} is not valid UTF-8.",
                exception
            );
        }
    }

    private static void ValidateRefId(RefId refId, string name) {
        if (refId.IsDefault) {
            throw new InvalidDataException($"{name} cannot be default.");
        }
    }

    private static void ValidateAddress(
        EventAddress address,
        string name
    ) {
        try {
            _ = EventAddressTextCodec.Format(address);
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                $"{name} is invalid.",
                exception
            );
        }
    }

    private static void ValidateSetups(
        SessionContextAnchorSetupReferences setups,
        string name
    ) {
        ArgumentNullException.ThrowIfNull(setups);
        ValidateSetupReference(
            setups.RuntimeConfig,
            $"{name}.runtimeConfig"
        );
        ValidateSetupReference(
            setups.SystemPrompt,
            $"{name}.systemPrompt"
        );
    }

    private static void ValidateSetupReference(
        SessionContextSetupReference reference,
        string name
    ) {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateAddress(reference.Address, $"{name}.address");
        if (reference.BodySchemaVersion <= 0) {
            throw new InvalidDataException(
                $"{name}.bodySchemaVersion must be positive."
            );
        }
        ValidateSha256(
            reference.PayloadSha256,
            $"{name}.payloadSha256"
        );
    }

    private static void ValidateToken(
        string value,
        int maxLength,
        string name
    ) {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maxLength
            || value.Contains('\0', StringComparison.Ordinal)) {
            throw new InvalidDataException($"{name} is invalid.");
        }
    }

    private static void ValidateCapabilityFingerprint(
        string value,
        string name
    ) {
        const string Prefix = "sha256:";
        if (value is null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length != Prefix.Length + 64
            || value.AsSpan(Prefix.Length).ContainsAnyExcept(
                "0123456789abcdef"
            )) {
            throw new InvalidDataException(
                $"{name} must be sha256: followed by lowercase SHA-256 hex."
            );
        }
    }

    internal static void ValidateSha256(
        string value,
        string name
    ) {
        if (value is null
            || value.Length != 64
            || value.Any(static ch =>
                !((ch >= '0' && ch <= '9')
                  || (ch >= 'a' && ch <= 'f')))) {
            throw new InvalidDataException(
                $"{name} must be lowercase SHA-256 hex."
            );
        }
    }

    private static void RequireMatchingHash(
        string expected,
        ReadOnlySpan<byte> projection,
        string name
    ) {
        string actual = Sha256Hex(projection);
        if (!string.Equals(
                expected,
                actual,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"{name} does not match its canonical projection."
            );
        }
    }

    internal static string Sha256Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static JsonDocument Parse(
        ReadOnlySpan<byte> bytes,
        string description
    ) {
        try {
            return JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                }
            );
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"{description} JSON is malformed.",
                exception
            );
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        string description,
        params string[] expected
    ) {
        RequireObject(element, description);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property
                 in element.EnumerateObject()) {
            if (!seen.Add(property.Name)) {
                throw new InvalidDataException(
                    $"{description} contains duplicate property '{property.Name}'."
                );
            }
            if (!expectedSet.Contains(property.Name)) {
                throw new InvalidDataException(
                    $"{description} contains unknown property '{property.Name}'."
                );
            }
        }
        foreach (string property in expected) {
            if (!seen.Contains(property)) {
                throw new InvalidDataException(
                    $"{description} is missing property '{property}'."
                );
            }
        }
    }

    private static void RequireObject(
        JsonElement element,
        string description
    ) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException(
                $"{description} must be a JSON object."
            );
        }
    }

    private static void RequireSchema(
        JsonElement element,
        string expected,
        string description
    ) {
        string actual = ReadString(element, "schema");
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) {
            throw new NotSupportedException(
                $"Unsupported {description} schema '{actual}'."
            );
        }
    }

    private static string ReadString(
        JsonElement element,
        string property
    ) {
        if (!element.TryGetProperty(
                property,
                out JsonElement value
            )
            || value.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException(
                $"Property '{property}' must be a string."
            );
        }
        return value.GetString()
            ?? throw new InvalidDataException(
                $"Property '{property}' cannot be null."
            );
    }

    private static int ReadInt32(
        JsonElement element,
        string property
    ) {
        if (!element.TryGetProperty(
                property,
                out JsonElement value
            )
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int result)) {
            throw new InvalidDataException(
                $"Property '{property}' must be an Int32."
            );
        }
        return result;
    }

    private static JsonElement ReadObject(
        JsonElement element,
        string property
    ) {
        if (!element.TryGetProperty(
                property,
                out JsonElement value
            )
            || value.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException(
                $"Property '{property}' must be an object."
            );
        }
        return value;
    }

    private static IEnumerable<JsonElement> ReadArray(
        JsonElement element,
        string property
    ) {
        if (!element.TryGetProperty(
                property,
                out JsonElement value
            )
            || value.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException(
                $"Property '{property}' must be an array."
            );
        }
        return value.EnumerateArray();
    }

    private static RecapBlockId ReadBlockId(
        JsonElement element,
        string property
    ) {
        try {
            return new RecapBlockId(ReadString(element, property));
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                $"Property '{property}' is not a valid RecapBlockId.",
                exception
            );
        }
    }

    private static RefId ReadRefId(
        JsonElement element,
        string property
    ) {
        string text = ReadString(element, property);
        var result = RefId.ParseHex(text);
        if (result.IsFailure) {
            throw new InvalidDataException(
                $"Property '{property}' is not a valid RefId."
            );
        }
        return result.Unwrap();
    }

    private static EventAddress ReadAddress(
        JsonElement element,
        string property
    ) {
        try {
            return EventAddressTextCodec.Parse(
                ReadString(element, property)
            );
        }
        catch (FormatException exception) {
            throw new InvalidDataException(
                $"Property '{property}' is not a valid EventAddress.",
                exception
            );
        }
    }

    private static EventAddress ReadAddressValue(
        JsonElement element
    ) {
        if (element.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException(
                "EventAddress array item must be a string."
            );
        }
        try {
            return EventAddressTextCodec.Parse(
                element.GetString()!
            );
        }
        catch (FormatException exception) {
            throw new InvalidDataException(
                "EventAddress array item is invalid.",
                exception
            );
        }
    }
}
