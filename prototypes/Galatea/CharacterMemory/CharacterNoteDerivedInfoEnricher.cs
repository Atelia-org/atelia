using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.Galatea.Prompts;
using Atelia.MemoPod;

namespace Atelia.Galatea.Server.CharacterMemory;

internal static class CharacterNoteDerivedInfoEnricherBounds {
    internal const int MaximumTargetCount =
        CharacterNoteBounds.MaximumIntentCount;
}

internal sealed record CharacterNoteDerivedInfoTarget(
    int ArtifactOrdinal,
    string ExactText
);

internal sealed record CharacterNoteDerivedInfoEnrichmentRequest {
    internal CharacterNoteDerivedInfoEnrichmentRequest(
        string observationContent,
        string visibleActionText,
        IReadOnlyList<CharacterNoteDerivedInfoTarget> targets
    ) {
        ArgumentNullException.ThrowIfNull(observationContent);
        ArgumentNullException.ThrowIfNull(visibleActionText);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Any(static target => target is null)) {
            throw new ArgumentException(
                "Character note derived-info targets must not contain null items.",
                nameof(targets)
            );
        }

        ObservationContent = observationContent;
        VisibleActionText = visibleActionText;
        Targets = Array.AsReadOnly(targets.ToArray());
    }

    internal string ObservationContent { get; }

    internal string VisibleActionText { get; }

    internal IReadOnlyList<CharacterNoteDerivedInfoTarget> Targets {
        get;
    }
}

[Description(
    "Rebuildable derived information for one long-term Note, identified by its source Action artifact ordinal."
)]
internal sealed record CharacterNoteDerivedInfo(
    [property: Range(0, CharacterNoteBounds.MaximumIntentCount - 1),
        Description(
            "The artifactOrdinal of the corresponding target. Preserve the exact input ordinal and target order."
        ),
        JsonPropertyName("artifactOrdinal")]
    int ArtifactOrdinal,
    [property: Required, Description(
        "A concise narrative catalogue title grounded in the Note and supplied turn context. It must be nonblank, already trimmed, contain no control characters, and fit within 512 UTF-8 bytes."
    ), JsonPropertyName("title")]
    string Title,
    [property: Required, Description(
        "A one-sentence impression of the Note, grounded in the Note and supplied turn context. It must be nonblank, already trimmed, contain no control characters, and fit within 2048 UTF-8 bytes."
    ), JsonPropertyName("gist")]
    string Gist,
    [property: Required, Description(
        "A main-idea summary of the Note, grounded in the Note and supplied turn context. It must be nonblank, already trimmed, contain no control characters, and fit within 8192 UTF-8 bytes."
    ), JsonPropertyName("summary")]
    string Summary
);

[Description(
    "The complete derived-info result for one ordered batch of long-term Notes. Emit this tool exactly once."
)]
internal sealed record CharacterNoteDerivedInfoBatch(
    [property: Required, Description(
        "Exactly one item for every input target, in the same order, with each input artifactOrdinal represented exactly once."
    ), JsonPropertyName("items")]
    IReadOnlyList<CharacterNoteDerivedInfo> Items
);

internal interface ICharacterNoteDerivedInfoEnricher {
    string ContractId { get; }

    ValueTask<IReadOnlyList<CharacterNoteDerivedInfo>> EnrichAsync(
        CharacterNoteDerivedInfoEnrichmentRequest request,
        CancellationToken cancellationToken
    );
}

internal static class CharacterNoteDerivedInfoTargetRenderer {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal const string ContractId =
        "atelia.galatea.character-note-derived-info-target-renderer.v1";
    internal const string SchemaId =
        "atelia.galatea.character-note-derived-info-target.v1";

    internal static string Render(
        CharacterNoteDerivedInfoEnrichmentRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        int observationBytes = RequireText(
            request.ObservationContent,
            TextExtractorBounds.MaximumTargetTextUtf8Bytes,
            "observationContent"
        );
        int actionBytes = RequireText(
            request.VisibleActionText,
            TextExtractorBounds.MaximumTargetTextUtf8Bytes,
            "visibleActionText"
        );
        if (request.Targets.Count is < 1
            or > CharacterNoteDerivedInfoEnricherBounds
                .MaximumTargetCount) {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Targets.Count,
                "Character note derived-info target count must be between "
                    + $"1 and {CharacterNoteDerivedInfoEnricherBounds.MaximumTargetCount}."
            );
        }

        long minimumTargetBytes = (long)observationBytes + actionBytes;
        int previousOrdinal = -1;
        foreach (CharacterNoteDerivedInfoTarget target in request.Targets) {
            if (target.ArtifactOrdinal is < 0
                or >= CharacterNoteBounds.MaximumIntentCount) {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    target.ArtifactOrdinal,
                    "Character note derived-info target artifactOrdinal is outside the source Action artifact range."
                );
            }
            if (target.ArtifactOrdinal <= previousOrdinal) {
                throw new ArgumentException(
                    "Character note derived-info targets must have strictly increasing artifactOrdinal values.",
                    nameof(request)
                );
            }
            previousOrdinal = target.ArtifactOrdinal;
            minimumTargetBytes += RequireText(
                target.ExactText,
                MemoPodLimits.MaximumMemoExactTextUtf8Bytes,
                "target exactText"
            );
            if (minimumTargetBytes
                    > TextExtractorBounds.MaximumTargetTextUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    "Character note derived-info target exceeds the TextExtractor UTF-8 byte limit."
                );
            }
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new() {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        })) {
            writer.WriteStartObject();
            writer.WriteString("schema", SchemaId);
            writer.WriteString(
                "observationContent",
                request.ObservationContent
            );
            writer.WriteString(
                "visibleActionText",
                request.VisibleActionText
            );
            writer.WriteStartArray("targets");
            foreach (CharacterNoteDerivedInfoTarget target
                    in request.Targets) {
                writer.WriteStartObject();
                writer.WriteNumber(
                    "artifactOrdinal",
                    target.ArtifactOrdinal
                );
                writer.WriteString("exactText", target.ExactText);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount
                > TextExtractorBounds.MaximumTargetTextUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Character note derived-info rendered target exceeds the TextExtractor UTF-8 byte limit."
            );
        }
        return StrictUtf8.GetString(buffer.WrittenSpan);
    }

    private static int RequireText(
        string? value,
        int maximumUtf8Bytes,
        string field
    ) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                $"Character note derived-info {field} must not be blank.",
                field
            );
        }
        try {
            int byteCount = StrictUtf8.GetByteCount(value);
            if (byteCount > maximumUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    field,
                    $"Character note derived-info {field} exceeds its UTF-8 byte limit."
                );
            }
            return byteCount;
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                $"Character note derived-info {field} is not strict UTF-8 text.",
                field,
                exception
            );
        }
    }
}

internal sealed class CharacterNoteDerivedInfoEnricher
    : ICharacterNoteDerivedInfoEnricher {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private const string ContractIdPrefix =
        "atelia.galatea.character-note-derived-info-enricher.v1.";
    private const string SemanticContractVersion =
        "atelia.galatea.character-note-derived-info-enricher.semantic.v1";
    private const string ToolContractVersion =
        "emit-character-note-derived-info-batch.v1";
    internal const string ToolName =
        "emit_character_note_derived_info_batch";

    private const string SystemPromptTemplate = """
You generate rebuildable derived information for an ordered batch of long-term Notes belonging to ${characterName}.

The target data contains exactly three context components: the raw ObservationContent from the completed turn, the visible provider Action from that turn, and ordered Note targets containing artifactOrdinal plus authoritative exactText. Use no other context. Treat each Note's exactText as the factual authority; ObservationContent and visibleActionText may clarify references but must not introduce unsupported claims.

For every target, produce:
- title: a concise narrative catalogue title.
- gist: a one-sentence impression of the Note.
- summary: a compact main-idea summary that is more detailed than the gist.

Every title, gist, and summary must be non-empty, already trimmed, contain no control characters, and be strict UTF-8 text. Keep title within 512 UTF-8 bytes, gist within 2048 UTF-8 bytes, and summary within 8192 UTF-8 bytes. Runtime validation is authoritative. "One sentence" and "main-idea summary" are semantic requirements; do not distort useful text merely to satisfy punctuation heuristics.

Call emit_character_note_derived_info_batch exactly once. Its items must cover every input target exactly once, preserve input order, and copy each artifactOrdinal exactly. Never omit, add, duplicate, or reorder items. If a faithful batch cannot be produced, emit no fabricated data.

Ordinary response text is diagnostic only. Use emit_character_note_derived_info_batch for the batch artifact.
""";

    private const string UserPromptTemplate = """
Generate title, one-sentence gist, and main-idea summary for every ${characterName} Note target. Emit exactly one complete batch in input order, preserving each artifactOrdinal exactly.
""";

    private readonly TextExtractor _inner;
    private readonly string _userPrompt;

    internal CharacterNoteDerivedInfoEnricher(
        GalateaCharacterName characterName,
        CompletionConnectionConfig connection,
        Func<ICompletionClient> getClient
    ) {
        ArgumentNullException.ThrowIfNull(characterName);
        string systemPrompt = GalateaPromptTemplate.Render(
            SystemPromptTemplate,
            characterName,
            TextExtractorBounds.MaximumSystemPromptUtf8Bytes
        );
        _userPrompt = GalateaPromptTemplate.Render(
            UserPromptTemplate,
            characterName,
            TextExtractorBounds.MaximumUserPromptUtf8Bytes
        );
        ContractId = CreateContractId(systemPrompt, _userPrompt);
        var tool = TextExtractorArtifactTool.Create<
            CharacterNoteDerivedInfoBatch>(
                ToolName,
                ValidateBatch
            );
        _inner = new TextExtractor(
            systemPrompt,
            TextExtractorToolSet.Create(tool),
            connection,
            getClient
        );
    }

    public string ContractId { get; }

    public async ValueTask<IReadOnlyList<CharacterNoteDerivedInfo>>
        EnrichAsync(
            CharacterNoteDerivedInfoEnrichmentRequest request,
            CancellationToken cancellationToken
        ) {
        cancellationToken.ThrowIfCancellationRequested();
        string targetText = CharacterNoteDerivedInfoTargetRenderer.Render(
            request
        );
        TextExtractionResult extraction = await _inner.ExtractAsync(
                targetText,
                _userPrompt,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (extraction.Artifacts.Count != 1) {
            throw InvalidCapture(
                "Character note derived-info enrichment must emit exactly one batch artifact."
            );
        }
        if (extraction.Artifacts[0] is not TextExtractionArtifact<
                CharacterNoteDerivedInfoBatch> typed) {
            throw InvalidCapture(
                "Character note derived-info enrichment captured an unexpected artifact type."
            );
        }

        IReadOnlyList<CharacterNoteDerivedInfo> items = typed.Value.Items;
        ValidateExactMapping(request.Targets, items);
        return Array.AsReadOnly(items.ToArray());
    }

    private static ValidateResult ValidateBatch(
        CharacterNoteDerivedInfoBatch batch,
        ToolExecutionContext context
    ) {
        _ = context;
        if (batch.Items is null
            || batch.Items.Count is < 1
                or > CharacterNoteDerivedInfoEnricherBounds
                    .MaximumTargetCount) {
            return new ValidateResult(
                false,
                "Batch items must contain between 1 and "
                    + $"{CharacterNoteDerivedInfoEnricherBounds.MaximumTargetCount} entries."
            );
        }
        foreach (CharacterNoteDerivedInfo? item in batch.Items) {
            if (item is null) {
                return new ValidateResult(
                    false,
                    "Batch items must not contain null entries."
                );
            }
            ValidateResult title = ValidateDerivedText(
                item.Title,
                MemoPodLimits.MaximumMemoTitleUtf8Bytes,
                "title"
            );
            if (!title.IsValid) { return title; }
            ValidateResult gist = ValidateDerivedText(
                item.Gist,
                MemoPodLimits.MaximumMemoGistUtf8Bytes,
                "gist"
            );
            if (!gist.IsValid) { return gist; }
            ValidateResult summary = ValidateDerivedText(
                item.Summary,
                MemoPodLimits.MaximumMemoSummaryUtf8Bytes,
                "summary"
            );
            if (!summary.IsValid) { return summary; }
        }
        return new ValidateResult(true, message: null);
    }

    private static ValidateResult ValidateDerivedText(
        string? value,
        int maximumUtf8Bytes,
        string field
    ) {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl)) {
            return new ValidateResult(
                false,
                $"Derived-info {field} must be nonblank, already trimmed, and contain no control characters."
            );
        }
        try {
            if (StrictUtf8.GetByteCount(value) > maximumUtf8Bytes) {
                return new ValidateResult(
                    false,
                    $"Derived-info {field} exceeds its UTF-8 byte limit."
                );
            }
        }
        catch (EncoderFallbackException) {
            return new ValidateResult(
                false,
                $"Derived-info {field} is not strict UTF-8 text."
            );
        }
        return new ValidateResult(true, message: null);
    }

    private static void ValidateExactMapping(
        IReadOnlyList<CharacterNoteDerivedInfoTarget> targets,
        IReadOnlyList<CharacterNoteDerivedInfo> items
    ) {
        if (items.Count != targets.Count) {
            throw InvalidOutput(
                "Character note derived-info batch does not cover every input target."
            );
        }

        var expectedOrdinals = targets
            .Select(static target => target.ArtifactOrdinal)
            .ToHashSet();
        var actualOrdinals = new HashSet<int>();
        for (int index = 0; index < items.Count; index++) {
            CharacterNoteDerivedInfo item = items[index];
            if (!actualOrdinals.Add(item.ArtifactOrdinal)) {
                throw InvalidOutput(
                    "Character note derived-info batch contains a duplicate artifactOrdinal."
                );
            }
            if (!expectedOrdinals.Contains(item.ArtifactOrdinal)) {
                throw InvalidOutput(
                    "Character note derived-info batch contains an unknown artifactOrdinal."
                );
            }
            if (item.ArtifactOrdinal != targets[index].ArtifactOrdinal) {
                throw InvalidOutput(
                    "Character note derived-info batch does not preserve input target order."
                );
            }
        }
    }

    private static string CreateContractId(
        string systemPrompt,
        string userPrompt
    ) {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        AppendContractPart(hash, SemanticContractVersion);
        AppendContractPart(
            hash,
            CharacterNoteDerivedInfoTargetRenderer.ContractId
        );
        AppendContractPart(
            hash,
            CharacterNoteDerivedInfoTargetRenderer.SchemaId
        );
        AppendContractPart(hash, ToolContractVersion);
        AppendContractPart(hash, ToolName);
        AppendContractPart(hash, systemPrompt);
        AppendContractPart(hash, userPrompt);
        return ContractIdPrefix
            + Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
    }

    private static void AppendContractPart(
        IncrementalHash hash,
        string value
    ) {
        byte[] utf8 = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, utf8.Length);
        hash.AppendData(length);
        hash.AppendData(utf8);
    }

    private static TextExtractionException InvalidCapture(
        string message
    ) => new(
        TextExtractionFailureKind.ArtifactCaptureMismatch,
        message
    );

    private static TextExtractionException InvalidOutput(
        string message
    ) => new(
        TextExtractionFailureKind.ToolExecutionFailed,
        message
    );
}
