using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Disconnected R3B candidate codec. Production Store switches to this wire
/// only in the atomic Store/Planner direct-cut commit.
/// </summary>
public static class DerivedRecapV8Codec {
    public const int MaximumFrozenHistoryMessageCount = 4096;
    public const string StoreSchema =
        "atelia.session-journal.derived-recap-store.v8";
    public const string ManifestSchema =
        "atelia.session-journal.derived-recap-manifest.v8";
    public const string EpochInputSchema =
        "atelia.session-journal.derived-recap-epoch-input.v8";
    public const string FinalBlockSchema =
        "atelia.session-journal.derived-recap-final-block.v8";
    public const string PublicationSchema =
        "atelia.session-journal.published-recap-epoch.v8";
    public const string HistoryProjectionSchema =
        "atelia.session-journal.recap-history-projection.v1";
    public const string PriorPackProjectionSchema =
        "atelia.session-journal.prior-recap-pack.v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static DerivedRecapEpochInput CreateEpochInput(
        RecapEpochBoundary startBoundary,
        RecapEpochBoundary admissionBoundary,
        int rawEventCount,
        string rawRangeCommitmentSha256,
        IReadOnlyList<IHistoryMessage> historyMessages,
        RecapEpochPrevious previous
    ) {
        ArgumentNullException.ThrowIfNull(historyMessages);
        IHistoryMessage[] frozenMessages = [.. historyMessages];
        var provisional = new DerivedRecapEpochInput(
            EpochInputSchema,
            startBoundary,
            admissionBoundary,
            rawEventCount,
            rawRangeCommitmentSha256,
            HistoryProjectionSchema,
            Array.AsReadOnly(frozenMessages),
            previous,
            string.Empty
        );
        ValidateEpochInputShape(provisional, requireHash: false);
        return provisional with {
            PayloadSha256 = Sha256Hex(Serialize(
                ToWire(provisional, includeHash: false)
            ))
        };
    }

    public static PriorRecapPackSnapshot CreatePriorPack(
        PublishedRecapEpochDescriptor source,
        IReadOnlyList<PriorRecapBlockSnapshot> blocks,
        string projectionSchema = PriorPackProjectionSchema
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(blocks);
        PriorRecapBlockSnapshot[] frozenBlocks = [.. blocks];
        var provisional = new PriorRecapPackSnapshot(
            source,
            projectionSchema,
            Array.AsReadOnly(frozenBlocks),
            string.Empty
        );
        ValidatePriorPackShape(provisional, requireHash: false);
        return provisional with {
            PayloadSha256 = Sha256Hex(Serialize(
                ToWire(provisional, includeHash: false)
            ))
        };
    }

    public static PriorRecapBlockSnapshot CreatePriorBlock(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        string content,
        string sourceEpochBlockExecutionSha256,
        string sourcePayloadSha256
    ) => new(
        recapBlockId,
        target,
        content,
        SessionContextContributionHasher.ComputeSha256(content),
        sourceEpochBlockExecutionSha256,
        sourcePayloadSha256
    );

    public static DerivedRecapEpochManifest CreateManifest(
        RefId refId,
        EventAddress admissionAnchor,
        string epochInputPayloadSha256,
        IReadOnlyList<RecapEpochBlockDefinition> blocks
    ) {
        ArgumentNullException.ThrowIfNull(blocks);
        RecapEpochBlockDefinition[] frozenBlocks = [.. blocks];
        var provisional = new DerivedRecapEpochManifest(
            ManifestSchema,
            refId,
            admissionAnchor,
            epochInputPayloadSha256,
            Array.AsReadOnly(frozenBlocks),
            string.Empty
        );
        ValidateManifestShape(provisional, requireHash: false);
        return provisional with {
            ManifestPayloadSha256 = Sha256Hex(Serialize(
                ToWire(provisional, includeHash: false)
            ))
        };
    }

    public static string ComputeEpochBlockExecutionSha256(
        DerivedRecapEpochManifest manifest,
        RecapEpochBlockDefinition definition
    ) {
        ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Ordinal < 0
            || definition.Ordinal >= manifest.Blocks.Count
            || manifest.Blocks[definition.Ordinal] != definition) {
            throw new InvalidDataException(
                "Block definition is not the exact manifest ordinal entry."
            );
        }
        return Sha256Hex(Serialize(new ExecutionWire(
            manifest.ManifestPayloadSha256,
            definition.Ordinal,
            ToWire(definition)
        )));
    }

    public static DerivedRecapFinalBlock CreateFinalBlock(
        DerivedRecapEpochManifest manifest,
        RecapEpochBlockDefinition definition,
        string content
    ) {
        ArgumentNullException.ThrowIfNull(content);
        var provisional = new DerivedRecapFinalBlock(
            FinalBlockSchema,
            definition.RecapBlockId,
            definition.Target,
            ComputeEpochBlockExecutionSha256(manifest, definition),
            content,
            SessionContextContributionHasher.ComputeSha256(content),
            string.Empty
        );
        ValidateFinalBlockShape(provisional, requireHash: false);
        var created = provisional with {
            PayloadSha256 = Sha256Hex(Serialize(
                ToWire(provisional, includeHash: false)
            ))
        };
        ValidateFinalForManifest(manifest, created);
        return created;
    }

    public static PublishedRecapEpoch CreatePublication(
        DerivedRecapEpochManifest manifest,
        IReadOnlyList<DerivedRecapFinalBlock> blocks
    ) {
        ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count != manifest.Blocks.Count) {
            throw new InvalidDataException(
                "Publication requires the complete manifest roster."
            );
        }
        var commitments = new RecapEpochBlockCommitment[blocks.Count];
        for (int ordinal = 0; ordinal < blocks.Count; ordinal++) {
            DerivedRecapFinalBlock block = blocks[ordinal]
                ?? throw new InvalidDataException(
                    $"Final block at ordinal {ordinal} is null."
                );
            RecapEpochBlockDefinition definition = manifest.Blocks[ordinal];
            ValidateFinalForManifest(manifest, block);
            if (block.RecapBlockId != definition.RecapBlockId
                || block.Target != definition.Target) {
                throw new InvalidDataException(
                    "Publication finals must follow manifest order."
                );
            }
            commitments[ordinal] = new RecapEpochBlockCommitment(
                block.RecapBlockId,
                block.Target,
                ordinal,
                block.EpochBlockExecutionSha256,
                block.PayloadSha256
            );
        }
        var provisional = new PublishedRecapEpoch(
            PublicationSchema,
            manifest.RefId,
            manifest.AdmissionAnchor,
            manifest,
            Array.AsReadOnly(commitments),
            string.Empty
        );
        ValidatePublicationShape(provisional, requireHash: false);
        return provisional with {
            EnvelopeSha256 = Sha256Hex(Serialize(
                ToWire(provisional, includeHash: false)
            ))
        };
    }

    public static byte[] EncodeStoreHeader(RefId refId)
        => !refId.IsDefault
            ? Serialize(new StoreHeaderWire(StoreSchema, refId.ToHexString()))
            : throw new InvalidDataException(
                "Store header RefId cannot be default."
            );

    public static int GetCanonicalPriorPackByteCount(
        PriorRecapPackSnapshot pack
    ) => EncodePriorPack(pack).Length;

    public static byte[] EncodePriorPack(PriorRecapPackSnapshot pack) {
        ValidatePriorPack(pack);
        return Serialize(ToWire(pack, includeHash: true));
    }

    public static int GetTotalRecapPackUtf8Bytes(
        IReadOnlyList<PriorRecapBlockSnapshot> blocks
    ) {
        ValidatePriorBlocks(blocks);
        int total = 0;
        foreach (PriorRecapBlockSnapshot block in blocks) {
            total = checked(total + StrictUtf8.GetByteCount(block.Content));
        }
        return total;
    }

    public static RefId DecodeStoreHeader(ReadOnlySpan<byte> bytes) {
        StoreHeaderWire wire = DeserializeCanonical<StoreHeaderWire>(bytes);
        RequireSchema(wire.Schema, StoreSchema, "store header");
        return ReadRefId(wire.RefId, "store.refId");
    }

    public static byte[] EncodeEpochInput(DerivedRecapEpochInput input) {
        ValidateEpochInput(input);
        return Serialize(ToWire(input, includeHash: true));
    }

    public static DerivedRecapEpochInput DecodeEpochInput(
        ReadOnlySpan<byte> bytes
    ) {
        EpochInputWire wire = DeserializeCanonical<EpochInputWire>(bytes);
        DerivedRecapEpochInput input = FromWire(wire);
        ValidateEpochInput(input);
        return input;
    }

    public static byte[] EncodeManifest(DerivedRecapEpochManifest manifest) {
        ValidateManifest(manifest);
        return Serialize(ToWire(manifest, includeHash: true));
    }

    public static DerivedRecapEpochManifest DecodeManifest(
        ReadOnlySpan<byte> bytes
    ) {
        ManifestWire wire = DeserializeCanonical<ManifestWire>(bytes);
        DerivedRecapEpochManifest manifest = FromWire(wire);
        ValidateManifest(manifest);
        return manifest;
    }

    public static byte[] EncodeFinalBlock(DerivedRecapFinalBlock block) {
        ValidateFinalBlock(block);
        return Serialize(ToWire(block, includeHash: true));
    }

    public static DerivedRecapFinalBlock DecodeFinalBlock(
        ReadOnlySpan<byte> bytes
    ) {
        FinalBlockWire wire = DeserializeCanonical<FinalBlockWire>(bytes);
        DerivedRecapFinalBlock block = FromWire(wire);
        ValidateFinalBlock(block);
        return block;
    }

    public static byte[] EncodePublication(PublishedRecapEpoch publication) {
        ValidatePublication(publication);
        return Serialize(ToWire(publication, includeHash: true));
    }

    public static PublishedRecapEpoch DecodePublication(
        ReadOnlySpan<byte> bytes
    ) {
        PublicationWire wire = DeserializeCanonical<PublicationWire>(bytes);
        PublishedRecapEpoch publication = FromWire(wire);
        ValidatePublication(publication);
        return publication;
    }

    public static void ValidateEpochInput(DerivedRecapEpochInput input) {
        ValidateEpochInputShape(input, requireHash: true);
        RequireHashMatch(
            input.PayloadSha256,
            Serialize(ToWire(input, includeHash: false)),
            "epoch input"
        );
    }

    public static void ValidateManifest(DerivedRecapEpochManifest manifest) {
        ValidateManifestShape(manifest, requireHash: true);
        RequireHashMatch(
            manifest.ManifestPayloadSha256,
            Serialize(ToWire(manifest, includeHash: false)),
            "manifest"
        );
    }

    /// <summary>
    /// Validates the cross-component invariant that turns two independently
    /// authenticated files into one shared epoch.
    /// </summary>
    public static void ValidateEpochSet(
        DerivedRecapEpochManifest manifest,
        DerivedRecapEpochInput input
    ) {
        ValidateManifest(manifest);
        ValidateEpochInput(input);
        if (!string.Equals(
                manifest.EpochInputPayloadSha256,
                input.PayloadSha256,
                StringComparison.Ordinal
            )
            || manifest.AdmissionAnchor
                != input.AdmissionBoundary.Address) {
            throw new InvalidDataException(
                "Manifest and epoch input identify different shared epochs."
            );
        }
        for (int ordinal = 1; ordinal < manifest.Blocks.Count; ordinal++) {
            if (CompareTargets(
                    manifest.Blocks[ordinal - 1].Target,
                    manifest.Blocks[ordinal].Target
                ) >= 0) {
                throw new InvalidDataException(
                    "Manifest roster must use canonical target order."
                );
            }
        }
        if (input.Previous is not RecapEpochPrevious.Prior prior) {
            return;
        }
        if (prior.Pack.Source.RefId != manifest.RefId
            || prior.Pack.Source.AdmissionAnchor
                != input.StartBoundary.Address
            || prior.Pack.Blocks.Count != manifest.Blocks.Count) {
            throw new InvalidDataException(
                "Prior recap pack identity or roster differs from the shared epoch."
            );
        }
        for (int ordinal = 0; ordinal < manifest.Blocks.Count; ordinal++) {
            PriorRecapBlockSnapshot oldBlock = prior.Pack.Blocks[ordinal];
            RecapEpochBlockDefinition definition = manifest.Blocks[ordinal];
            if (oldBlock.RecapBlockId != definition.RecapBlockId
                || oldBlock.Target != definition.Target) {
                throw new InvalidDataException(
                    "Prior recap pack must exactly match manifest roster order and topology."
                );
            }
        }
    }

    public static void ValidateFinalBlock(DerivedRecapFinalBlock block) {
        ValidateFinalBlockShape(block, requireHash: true);
        RequireHashMatch(
            block.PayloadSha256,
            Serialize(ToWire(block, includeHash: false)),
            "final block"
        );
    }

    public static void ValidateFinalForManifest(
        DerivedRecapEpochManifest manifest,
        DerivedRecapFinalBlock block
    ) {
        ValidateManifest(manifest);
        ValidateFinalBlock(block);
        RecapEpochBlockDefinition definition = manifest.Blocks
            .SingleOrDefault(candidate =>
                candidate.RecapBlockId == block.RecapBlockId)
            ?? throw new InvalidDataException(
                $"Final block '{block.RecapBlockId}' is outside the manifest roster."
            );
        if (definition.Target != block.Target
            || !string.Equals(
                ComputeEpochBlockExecutionSha256(manifest, definition),
                block.EpochBlockExecutionSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Final block execution identity does not match the manifest epoch."
            );
        }
        if (StrictUtf8.GetByteCount(block.Content)
            > definition.MaxContentUtf8Bytes) {
            throw new InvalidDataException(
                $"Final block '{block.RecapBlockId}' exceeds its content ceiling."
            );
        }
    }

    public static void ValidatePublication(PublishedRecapEpoch publication) {
        ValidatePublicationShape(publication, requireHash: true);
        RequireHashMatch(
            publication.EnvelopeSha256,
            Serialize(ToWire(publication, includeHash: false)),
            "publication"
        );
    }

    private static void ValidateEpochInputShape(
        DerivedRecapEpochInput input,
        bool requireHash
    ) {
        ArgumentNullException.ThrowIfNull(input);
        RequireSchema(input.Schema, EpochInputSchema, "epoch input");
        ValidateBoundary(input.StartBoundary, "startBoundary");
        ValidateBoundary(input.AdmissionBoundary, "admissionBoundary");
        if (input.StartBoundary.Address == input.AdmissionBoundary.Address) {
            throw new InvalidDataException(
                "Epoch start and admission boundaries must differ."
            );
        }
        if (input.RawEventCount <= 0) {
            throw new InvalidDataException(
                "Epoch raw event count must be positive."
            );
        }
        RequireSha256(input.RawRangeCommitmentSha256, "raw range commitment");
        if (!string.Equals(
                input.HistoryProjectionSchema,
                HistoryProjectionSchema,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Epoch history projection schema is unsupported."
            );
        }
        ArgumentNullException.ThrowIfNull(input.HistoryMessages);
        if (input.HistoryMessages.Count is < 1
            or > MaximumFrozenHistoryMessageCount) {
            throw new InvalidDataException(
                "Epoch history projection message count is outside the protocol bounds."
            );
        }
        foreach (IHistoryMessage message in input.HistoryMessages) {
            _ = ToWire(message ?? throw new InvalidDataException(
                "History projection cannot contain null messages."
            ));
        }
        switch (input.Previous) {
            case RecapEpochPrevious.Empty:
                break;
            case RecapEpochPrevious.Prior prior:
                ValidatePriorPack(prior.Pack);
                break;
            default:
                throw new InvalidDataException(
                    "Epoch previous state is unsupported."
                );
        }
        if (requireHash) {
            RequireSha256(input.PayloadSha256, "epoch input payload");
        }
    }

    private static void ValidatePriorPack(PriorRecapPackSnapshot pack) {
        ValidatePriorPackShape(pack, requireHash: true);
        RequireHashMatch(
            pack.PayloadSha256,
            Serialize(ToWire(pack, includeHash: false)),
            "prior recap pack"
        );
    }

    private static void ValidatePriorPackShape(
        PriorRecapPackSnapshot pack,
        bool requireHash
    ) {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(pack.Source);
        if (pack.Source.RefId.IsDefault
            || pack.Source.AdmissionAnchor == default) {
            throw new InvalidDataException(
                "Prior recap source identity cannot be default."
            );
        }
        RequireSha256(pack.Source.EnvelopeSha256, "prior source envelope");
        if (!string.Equals(
                pack.ProjectionSchema,
                PriorPackProjectionSchema,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Prior recap pack projection schema is unsupported."
            );
        }
        ValidatePriorBlocks(pack.Blocks);
        if (requireHash) {
            RequireSha256(pack.PayloadSha256, "prior recap pack payload");
        }
    }

    private static void ValidatePriorBlocks(
        IReadOnlyList<PriorRecapBlockSnapshot> blocks
    ) {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count is < 1
            or > SessionContextContributionContract.MaxContributionCount) {
            throw new InvalidDataException(
                "Prior recap pack block count is outside the protocol bounds."
            );
        }
        var ids = new HashSet<RecapBlockId>();
        var targets = new HashSet<ContextHeaderBlockPath>();
        foreach (PriorRecapBlockSnapshot block in blocks) {
            ArgumentNullException.ThrowIfNull(block);
            ValidateTarget(block.Target);
            if (!ids.Add(block.RecapBlockId) || !targets.Add(block.Target)) {
                throw new InvalidDataException(
                    "Prior recap pack block IDs and targets must be unique."
                );
            }
            if (!string.Equals(
                    SessionContextContributionHasher.ComputeSha256(block.Content),
                    block.ContentSha256,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Prior block '{block.RecapBlockId}' content hash is invalid."
                );
            }
            RequireSha256(
                block.SourceEpochBlockExecutionSha256,
                $"prior block '{block.RecapBlockId}' source execution"
            );
            RequireSha256(
                block.SourcePayloadSha256,
                $"prior block '{block.RecapBlockId}' source payload"
            );
            ValidateFinalBlock(new DerivedRecapFinalBlock(
                FinalBlockSchema,
                block.RecapBlockId,
                block.Target,
                block.SourceEpochBlockExecutionSha256,
                block.Content,
                block.ContentSha256,
                block.SourcePayloadSha256
            ));
        }
    }

    private static void ValidateManifestShape(
        DerivedRecapEpochManifest manifest,
        bool requireHash
    ) {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireSchema(manifest.Schema, ManifestSchema, "manifest");
        if (manifest.RefId.IsDefault
            || manifest.AdmissionAnchor == default) {
            throw new InvalidDataException(
                "Manifest identity cannot be default."
            );
        }
        RequireSha256(manifest.EpochInputPayloadSha256, "epoch input payload");
        ArgumentNullException.ThrowIfNull(manifest.Blocks);
        if (manifest.Blocks.Count is < 1
            or > SessionContextContributionContract.MaxContributionCount) {
            throw new InvalidDataException(
                "Manifest roster count is outside the protocol bounds."
            );
        }
        var ids = new HashSet<RecapBlockId>();
        var targets = new HashSet<ContextHeaderBlockPath>();
        for (int ordinal = 0; ordinal < manifest.Blocks.Count; ordinal++) {
            RecapEpochBlockDefinition definition = manifest.Blocks[ordinal]
                ?? throw new InvalidDataException(
                    $"Manifest block at ordinal {ordinal} is null."
                );
            if (definition.Ordinal != ordinal) {
                throw new InvalidDataException(
                    "Manifest roster ordinals must be dense and ordered."
                );
            }
            ValidateTarget(definition.Target);
            if (!ids.Add(definition.RecapBlockId)
                || !targets.Add(definition.Target)) {
                throw new InvalidDataException(
                    "Manifest block IDs and targets must be unique."
                );
            }
            if (string.IsNullOrWhiteSpace(definition.MaintainerId)) {
                throw new InvalidDataException(
                    "Manifest maintainer ID cannot be empty."
                );
            }
            RequireUtf8(definition.MaintainerId, "manifest maintainer ID");
            RequireCapabilityFingerprint(
                definition.MaintainerCapabilityFingerprint
            );
            if (definition.MaxContentUtf8Bytes <= 0
                || definition.MaxContentUtf8Bytes
                    > SessionContextContributionContract
                        .MaxContributionUtf8Bytes) {
                throw new InvalidDataException(
                    "Manifest content ceiling is outside the protocol bounds."
                );
            }
        }
        if (requireHash) {
            RequireSha256(manifest.ManifestPayloadSha256, "manifest payload");
        }
    }

    private static void ValidateFinalBlockShape(
        DerivedRecapFinalBlock block,
        bool requireHash
    ) {
        ArgumentNullException.ThrowIfNull(block);
        RequireSchema(block.Schema, FinalBlockSchema, "final block");
        ValidateTarget(block.Target);
        RequireUtf8(block.Content, "final block content");
        RequireSha256(block.EpochBlockExecutionSha256, "block execution");
        if (!string.Equals(
                SessionContextContributionHasher.ComputeSha256(block.Content),
                block.ContentSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException("Final block content hash is invalid.");
        }
        if (requireHash) {
            RequireSha256(block.PayloadSha256, "final block payload");
        }
    }

    private static void ValidatePublicationShape(
        PublishedRecapEpoch publication,
        bool requireHash
    ) {
        ArgumentNullException.ThrowIfNull(publication);
        RequireSchema(publication.Schema, PublicationSchema, "publication");
        ValidateManifest(publication.FrozenManifest);
        if (publication.RefId != publication.FrozenManifest.RefId
            || publication.AdmissionAnchor
                != publication.FrozenManifest.AdmissionAnchor) {
            throw new InvalidDataException(
                "Publication identity differs from its frozen manifest."
            );
        }
        ArgumentNullException.ThrowIfNull(publication.BlockCommitments);
        if (publication.BlockCommitments.Count
            != publication.FrozenManifest.Blocks.Count) {
            throw new InvalidDataException(
                "Publication commitments must cover the complete roster."
            );
        }
        for (int ordinal = 0;
             ordinal < publication.BlockCommitments.Count;
             ordinal++) {
            RecapEpochBlockCommitment commitment =
                publication.BlockCommitments[ordinal]
                ?? throw new InvalidDataException(
                    $"Publication commitment at ordinal {ordinal} is null."
                );
            RecapEpochBlockDefinition definition =
                publication.FrozenManifest.Blocks[ordinal];
            if (commitment.Ordinal != ordinal
                || commitment.RecapBlockId != definition.RecapBlockId
                || commitment.Target != definition.Target
                || !string.Equals(
                    commitment.EpochBlockExecutionSha256,
                    ComputeEpochBlockExecutionSha256(
                        publication.FrozenManifest,
                        definition
                    ),
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    "Publication commitment is not bound to the manifest roster."
                );
            }
            RequireSha256(commitment.PayloadSha256, "committed final payload");
        }
        if (requireHash) {
            RequireSha256(publication.EnvelopeSha256, "publication envelope");
        }
    }

    private static void ValidateBoundary(
        RecapEpochBoundary boundary,
        string name
    ) {
        ArgumentNullException.ThrowIfNull(boundary);
        if (boundary.Address == default) {
            throw new InvalidDataException($"{name}.address cannot be default.");
        }
        ArgumentNullException.ThrowIfNull(boundary.Setups);
        ValidateSetup(boundary.Setups.RuntimeConfig, $"{name}.runtimeConfig");
        ValidateSetup(boundary.Setups.SystemPrompt, $"{name}.systemPrompt");
    }

    private static void ValidateSetup(
        SessionContextSetupReference setup,
        string name
    ) {
        ArgumentNullException.ThrowIfNull(setup);
        if (setup.Address == default || setup.BodySchemaVersion <= 0) {
            throw new InvalidDataException($"{name} is invalid.");
        }
        RequireSha256(setup.PayloadSha256, $"{name}.payloadSha256");
    }

    private static void ValidateTarget(ContextHeaderBlockPath target) {
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(target.Carrier)
            || string.IsNullOrWhiteSpace(target.BlockKey)
            || target.BlockKey.Length
                > SessionContextContributionContract.MaxBlockKeyLength
            || target.BlockKey.Contains('\0')) {
            throw new InvalidDataException("Recap block target is invalid.");
        }
        RequireUtf8(target.BlockKey, "recap block key");
    }

    private static int CompareTargets(
        ContextHeaderBlockPath left,
        ContextHeaderBlockPath right
    ) {
        int carrier = CarrierRank(left.Carrier).CompareTo(
            CarrierRank(right.Carrier)
        );
        return carrier != 0
            ? carrier
            : StringComparer.Ordinal.Compare(left.BlockKey, right.BlockKey);
    }

    private static int CarrierRank(ContextHeaderCarrier carrier)
        => carrier switch {
            ContextHeaderCarrier.System => 0,
            ContextHeaderCarrier.Observation => 1,
            ContextHeaderCarrier.Action => 2,
            _ => throw new InvalidDataException(
                "Recap target carrier is unsupported."
            )
        };

    private static void RequireHashMatch(
        string expected,
        byte[] payload,
        string name
    ) {
        string observed = Sha256Hex(payload);
        if (!string.Equals(expected, observed, StringComparison.Ordinal)) {
            throw new InvalidDataException($"{name} hash does not match payload.");
        }
    }

    private static void RequireSchema(
        string? observed,
        string expected,
        string name
    ) {
        if (!string.Equals(observed, expected, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"Unsupported {name} schema '{observed ?? "<null>"}'."
            );
        }
    }

    private static void RequireSha256(string? value, string name) {
        if (value is null
            || value.Length != 64
            || value.AsSpan().ContainsAnyExcept("0123456789abcdef")) {
            throw new InvalidDataException(
                $"{name} must be canonical lowercase SHA-256 hex."
            );
        }
    }

    private static void RequireCapabilityFingerprint(string? value) {
        const string Prefix = "sha256:";
        if (value is null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length != Prefix.Length + 64
            || value.AsSpan(Prefix.Length).ContainsAnyExcept(
                "0123456789abcdef"
            )) {
            throw new InvalidDataException(
                "Maintainer capability fingerprint is invalid."
            );
        }
    }

    private static void RequireUtf8(string? value, string name) {
        if (value is null) {
            return;
        }
        try {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidDataException(
                $"{name} is not valid UTF-8 text.",
                exception
            );
        }
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Serialize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static T DeserializeCanonical<T>(ReadOnlySpan<byte> bytes) {
        T value;
        try {
            value = JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new InvalidDataException(
                    $"{typeof(T).Name} decoded to null."
                );
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"Invalid {typeof(T).Name} JSON.",
                exception
            );
        }
        if (!bytes.SequenceEqual(Serialize(value))) {
            throw new InvalidDataException(
                $"{typeof(T).Name} bytes are not canonical."
            );
        }
        return value;
    }

    private static EpochInputWire ToWire(
        DerivedRecapEpochInput input,
        bool includeHash
    ) => new(
        input.Schema,
        ToWire(input.StartBoundary),
        ToWire(input.AdmissionBoundary),
        input.RawEventCount,
        input.RawRangeCommitmentSha256,
        input.HistoryProjectionSchema,
        [.. input.HistoryMessages.Select(ToWire)],
        ToWire(input.Previous),
        includeHash ? input.PayloadSha256 : null
    );

    private static DerivedRecapEpochInput FromWire(EpochInputWire wire)
        => new(
            wire.Schema,
            FromWire(wire.StartBoundary),
            FromWire(wire.AdmissionBoundary),
            wire.RawEventCount,
            wire.RawRangeCommitmentSha256,
            wire.HistoryProjectionSchema,
            Array.AsReadOnly(wire.HistoryMessages.Select(FromWire).ToArray()),
            FromWire(wire.Previous),
            wire.PayloadSha256 ?? string.Empty
        );

    private static ManifestWire ToWire(
        DerivedRecapEpochManifest manifest,
        bool includeHash
    ) => new(
        manifest.Schema,
        manifest.RefId.ToHexString(),
        EventAddressTextCodec.Format(manifest.AdmissionAnchor),
        manifest.EpochInputPayloadSha256,
        [.. manifest.Blocks.Select(ToWire)],
        includeHash ? manifest.ManifestPayloadSha256 : null
    );

    private static DerivedRecapEpochManifest FromWire(ManifestWire wire)
        => new(
            wire.Schema,
            ReadRefId(wire.RefId, "manifest.refId"),
            ReadAddress(wire.AdmissionAnchor, "manifest.admissionAnchor"),
            wire.EpochInputPayloadSha256,
            Array.AsReadOnly(wire.Blocks.Select(FromWire).ToArray()),
            wire.ManifestPayloadSha256 ?? string.Empty
        );

    private static FinalBlockWire ToWire(
        DerivedRecapFinalBlock block,
        bool includeHash
    ) => new(
        block.Schema,
        block.RecapBlockId.Value,
        ToWire(block.Target),
        block.EpochBlockExecutionSha256,
        block.Content,
        block.ContentSha256,
        includeHash ? block.PayloadSha256 : null
    );

    private static DerivedRecapFinalBlock FromWire(FinalBlockWire wire)
        => new(
            wire.Schema,
            new RecapBlockId(wire.RecapBlockId),
            FromWire(wire.Target),
            wire.EpochBlockExecutionSha256,
            wire.Content,
            wire.ContentSha256,
            wire.PayloadSha256 ?? string.Empty
        );

    private static PublicationWire ToWire(
        PublishedRecapEpoch publication,
        bool includeHash
    ) => new(
        publication.Schema,
        publication.RefId.ToHexString(),
        EventAddressTextCodec.Format(publication.AdmissionAnchor),
        ToWire(publication.FrozenManifest, includeHash: true),
        [.. publication.BlockCommitments.Select(ToWire)],
        includeHash ? publication.EnvelopeSha256 : null
    );

    private static PublishedRecapEpoch FromWire(PublicationWire wire)
        => new(
            wire.Schema,
            ReadRefId(wire.RefId, "publication.refId"),
            ReadAddress(wire.AdmissionAnchor, "publication.admissionAnchor"),
            FromWire(wire.FrozenManifest),
            Array.AsReadOnly(
                wire.BlockCommitments.Select(FromWire).ToArray()
            ),
            wire.EnvelopeSha256 ?? string.Empty
        );

    private static PreviousWire ToWire(RecapEpochPrevious previous)
        => previous switch {
            RecapEpochPrevious.Empty => new("empty", null),
            RecapEpochPrevious.Prior prior => new(
                "prior",
                ToWire(prior.Pack, includeHash: true)
            ),
            _ => throw new InvalidDataException(
                "Unsupported epoch previous state."
            )
        };

    private static RecapEpochPrevious FromWire(PreviousWire wire)
        => wire.Kind switch {
            "empty" when wire.Pack is null =>
                RecapEpochPrevious.Empty.Instance,
            "prior" when wire.Pack is not null =>
                new RecapEpochPrevious.Prior(FromWire(wire.Pack)),
            _ => throw new InvalidDataException(
                "Epoch previous state has an invalid shape."
            )
        };

    private static PriorPackWire ToWire(
        PriorRecapPackSnapshot pack,
        bool includeHash
    ) => new(
        new DescriptorWire(
            pack.Source.RefId.ToHexString(),
            EventAddressTextCodec.Format(pack.Source.AdmissionAnchor),
            pack.Source.EnvelopeSha256
        ),
        pack.ProjectionSchema,
        [.. pack.Blocks.Select(ToWire)],
        includeHash ? pack.PayloadSha256 : null
    );

    private static PriorRecapPackSnapshot FromWire(PriorPackWire wire)
        => new(
            new PublishedRecapEpochDescriptor(
                ReadRefId(wire.Source.RefId, "previous.source.refId"),
                ReadAddress(
                    wire.Source.AdmissionAnchor,
                    "previous.source.admissionAnchor"
                ),
                wire.Source.EnvelopeSha256
            ),
            wire.ProjectionSchema,
            Array.AsReadOnly(wire.Blocks.Select(FromWire).ToArray()),
            wire.PayloadSha256 ?? string.Empty
        );

    private static PriorBlockWire ToWire(PriorRecapBlockSnapshot block)
        => new(
            block.RecapBlockId.Value,
            ToWire(block.Target),
            block.Content,
            block.ContentSha256,
            block.SourceEpochBlockExecutionSha256,
            block.SourcePayloadSha256
        );

    private static PriorRecapBlockSnapshot FromWire(PriorBlockWire wire)
        => new(
            new RecapBlockId(wire.RecapBlockId),
            FromWire(wire.Target),
            wire.Content,
            wire.ContentSha256,
            wire.SourceEpochBlockExecutionSha256,
            wire.SourcePayloadSha256
        );

    private static BlockDefinitionWire ToWire(
        RecapEpochBlockDefinition definition
    ) => new(
        definition.RecapBlockId.Value,
        ToWire(definition.Target),
        definition.MaintainerId,
        definition.MaintainerCapabilityFingerprint,
        definition.MaxContentUtf8Bytes,
        definition.Ordinal
    );

    private static RecapEpochBlockDefinition FromWire(
        BlockDefinitionWire wire
    ) => new(
        new RecapBlockId(wire.RecapBlockId),
        FromWire(wire.Target),
        wire.MaintainerId,
        wire.MaintainerCapabilityFingerprint,
        wire.MaxContentUtf8Bytes,
        wire.Ordinal
    );

    private static CommitmentWire ToWire(
        RecapEpochBlockCommitment commitment
    ) => new(
        commitment.RecapBlockId.Value,
        ToWire(commitment.Target),
        commitment.Ordinal,
        commitment.EpochBlockExecutionSha256,
        commitment.PayloadSha256
    );

    private static RecapEpochBlockCommitment FromWire(
        CommitmentWire wire
    ) => new(
        new RecapBlockId(wire.RecapBlockId),
        FromWire(wire.Target),
        wire.Ordinal,
        wire.EpochBlockExecutionSha256,
        wire.PayloadSha256
    );

    private static BoundaryWire ToWire(RecapEpochBoundary boundary)
        => new(
            EventAddressTextCodec.Format(boundary.Address),
            ToWire(boundary.Setups)
        );

    private static RecapEpochBoundary FromWire(BoundaryWire wire)
        => new(
            ReadAddress(wire.Address, "boundary.address"),
            FromWire(wire.Setups)
        );

    private static SetupsWire ToWire(
        SessionContextAnchorSetupReferences setups
    ) => new(
        ToWire(setups.RuntimeConfig),
        ToWire(setups.SystemPrompt)
    );

    private static SessionContextAnchorSetupReferences FromWire(
        SetupsWire wire
    ) => new(
        FromWire(wire.RuntimeConfig),
        FromWire(wire.SystemPrompt)
    );

    private static SetupWire ToWire(SessionContextSetupReference setup)
        => new(
            EventAddressTextCodec.Format(setup.Address),
            setup.BodySchemaVersion,
            setup.PayloadSha256
        );

    private static SessionContextSetupReference FromWire(SetupWire wire)
        => new(
            ReadAddress(wire.Address, "setup.address"),
            wire.BodySchemaVersion,
            wire.PayloadSha256
        );

    private static TargetWire ToWire(ContextHeaderBlockPath target)
        => new(
            ContextHeaderCarrierTokens.ToStorageToken(target.Carrier),
            target.BlockKey
        );

    private static ContextHeaderBlockPath FromWire(TargetWire wire) {
        if (!ContextHeaderCarrierTokens.TryParseStorageToken(
                wire.Carrier,
                out ContextHeaderCarrier carrier
            )) {
            throw new InvalidDataException(
                $"Unsupported recap target carrier '{wire.Carrier}'."
            );
        }
        return new ContextHeaderBlockPath(carrier, wire.BlockKey);
    }

    private static HistoryMessageWire ToWire(IHistoryMessage message)
        => message switch {
            ObservationMessage observation
                when observation.GetType() == typeof(ObservationMessage)
                    && message.Kind == HistoryMessageKind.Observation =>
                ObservationWire(observation),
            ToolResultsMessage toolResults
                when message.Kind == HistoryMessageKind.ToolResults =>
                ToolResultsWire(toolResults),
            ActionMessage action
                when message.Kind == HistoryMessageKind.Action =>
                ToWire(action),
            _ => throw new InvalidDataException(
                $"Unsupported frozen history message '{message.GetType().FullName}'."
            )
        };

    private static HistoryMessageWire ToWire(ActionMessage action) {
        foreach (ActionBlock block in action.Blocks) {
            if (block is not (ActionBlock.Text or ActionBlock.ToolCall)) {
                throw new InvalidDataException(
                    "Frozen recap history action may contain only text and tool-call blocks."
                );
            }
            switch (block) {
                case ActionBlock.Text text:
                    RequireUtf8(text.Content, "history action text");
                    break;
                case ActionBlock.ToolCall toolCall:
                    RequireUtf8(toolCall.Call.ToolName, "history tool name");
                    RequireUtf8(toolCall.Call.ToolCallId, "history tool call ID");
                    RequireUtf8(
                        toolCall.Call.RawArgumentsJson,
                        "history tool arguments"
                    );
                    break;
            }
        }
        return new HistoryMessageWire(
            "action",
            null,
            ActionMessageSerialization.Serialize(action),
            null
        );
    }

    private static HistoryMessageWire ObservationWire(
        ObservationMessage observation
    ) {
        RequireUtf8(observation.Content, "history observation");
        return new HistoryMessageWire(
            "observation",
            observation.Content,
            null,
            null
        );
    }

    private static HistoryMessageWire ToolResultsWire(
        ToolResultsMessage toolResults
    ) {
        RequireUtf8(toolResults.Content, "history tool-results content");
        return new HistoryMessageWire(
            "tool-results",
            toolResults.Content,
            null,
            [.. toolResults.Results.Select(ToWire)]
        );
    }

    private static IHistoryMessage FromWire(HistoryMessageWire wire)
        => wire.Kind switch {
            "observation" when wire.ActionBlocksJson is null
                && wire.Results is null =>
                new ObservationMessage(wire.Content),
            "action" when wire.Content is null
                && wire.ActionBlocksJson is not null
                && wire.Results is null =>
                ActionMessageSerialization.Deserialize(wire.ActionBlocksJson),
            "tool-results" when wire.ActionBlocksJson is null
                && wire.Results is not null =>
                new ToolResultsMessage(
                    wire.Content,
                    wire.Results.Select(FromWire).ToArray()
                ),
            _ => throw new InvalidDataException(
                "Frozen history message has an invalid shape."
            )
        };

    private static ToolResultWire ToWire(ToolResult result)
    {
        RequireUtf8(result.ToolName, "tool result name");
        RequireUtf8(result.ToolCallId, "tool result call ID");
        foreach (ToolResultBlock block in result.Blocks) {
            if (block is ToolResultBlock.Text text) {
                RequireUtf8(text.Content, "tool result text");
            }
        }
        return new ToolResultWire(
            result.ToolName,
            result.ToolCallId,
            result.Status switch {
                ToolExecutionStatus.Success => "success",
                ToolExecutionStatus.Failed => "failed",
                ToolExecutionStatus.Skipped => "skipped",
                _ => throw new InvalidDataException(
                    "Unsupported tool result status."
                )
            },
            [.. result.Blocks.Select(block => block switch {
                ToolResultBlock.Text text => text.Content,
                _ => throw new InvalidDataException(
                    "Unsupported tool result block."
                )
            })]
        );
    }

    private static ToolResult FromWire(ToolResultWire wire)
        => new(
            wire.ToolName,
            wire.ToolCallId,
            wire.Status switch {
                "success" => ToolExecutionStatus.Success,
                "failed" => ToolExecutionStatus.Failed,
                "skipped" => ToolExecutionStatus.Skipped,
                _ => throw new InvalidDataException(
                    $"Unsupported tool result status '{wire.Status}'."
                )
            },
            wire.TextBlocks.Select(
                static content => (ToolResultBlock)
                    new ToolResultBlock.Text(content)
            ).ToArray()
        );

    private static EventAddress ReadAddress(string value, string name) {
        if (!EventAddressTextCodec.TryParse(value, out EventAddress address)) {
            throw new InvalidDataException($"{name} is invalid.");
        }
        return address;
    }

    private static RefId ReadRefId(string value, string name) {
        var result = RefId.ParseHex(value);
        if (result.IsFailure) {
            throw new InvalidDataException($"{name} is invalid.");
        }
        return result.Unwrap();
    }

    private sealed record StoreHeaderWire(string Schema, string RefId);
    private sealed record EpochInputWire(
        string Schema,
        BoundaryWire StartBoundary,
        BoundaryWire AdmissionBoundary,
        int RawEventCount,
        string RawRangeCommitmentSha256,
        string HistoryProjectionSchema,
        HistoryMessageWire[] HistoryMessages,
        PreviousWire Previous,
        string? PayloadSha256
    );
    private sealed record ManifestWire(
        string Schema,
        string RefId,
        string AdmissionAnchor,
        string EpochInputPayloadSha256,
        BlockDefinitionWire[] Blocks,
        string? ManifestPayloadSha256
    );
    private sealed record FinalBlockWire(
        string Schema,
        string RecapBlockId,
        TargetWire Target,
        string EpochBlockExecutionSha256,
        string Content,
        string ContentSha256,
        string? PayloadSha256
    );
    private sealed record PublicationWire(
        string Schema,
        string RefId,
        string AdmissionAnchor,
        ManifestWire FrozenManifest,
        CommitmentWire[] BlockCommitments,
        string? EnvelopeSha256
    );
    private sealed record ExecutionWire(
        string ManifestPayloadSha256,
        int Ordinal,
        BlockDefinitionWire Block
    );
    private sealed record PreviousWire(string Kind, PriorPackWire? Pack);
    private sealed record PriorPackWire(
        DescriptorWire Source,
        string ProjectionSchema,
        PriorBlockWire[] Blocks,
        string? PayloadSha256
    );
    private sealed record DescriptorWire(
        string RefId,
        string AdmissionAnchor,
        string EnvelopeSha256
    );
    private sealed record PriorBlockWire(
        string RecapBlockId,
        TargetWire Target,
        string Content,
        string ContentSha256,
        string SourceEpochBlockExecutionSha256,
        string SourcePayloadSha256
    );
    private sealed record BlockDefinitionWire(
        string RecapBlockId,
        TargetWire Target,
        string MaintainerId,
        string MaintainerCapabilityFingerprint,
        int MaxContentUtf8Bytes,
        int Ordinal
    );
    private sealed record CommitmentWire(
        string RecapBlockId,
        TargetWire Target,
        int Ordinal,
        string EpochBlockExecutionSha256,
        string PayloadSha256
    );
    private sealed record BoundaryWire(string Address, SetupsWire Setups);
    private sealed record SetupsWire(
        SetupWire RuntimeConfig,
        SetupWire SystemPrompt
    );
    private sealed record SetupWire(
        string Address,
        int BodySchemaVersion,
        string PayloadSha256
    );
    private sealed record TargetWire(string Carrier, string BlockKey);
    private sealed record HistoryMessageWire(
        string Kind,
        string? Content,
        string? ActionBlocksJson,
        ToolResultWire[]? Results
    );
    private sealed record ToolResultWire(
        string ToolName,
        string ToolCallId,
        string Status,
        string[] TextBlocks
    );
}
