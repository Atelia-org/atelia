using System.Buffers.Binary;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Prompts;

namespace Atelia.Galatea.Server.CharacterMemory;

internal static class CharacterNoteBounds {
    internal const int MaximumExactTextUtf8Bytes = Atelia.Galatea.Input.GalateaObservationLimits.MaximumNoteExactTextUtf8Bytes;
    internal const int MaximumIntentCount = Atelia.Galatea.Input.GalateaObservationLimits.MaximumNoteIntentCount;
    internal const int MaximumTotalExactTextUtf8Bytes = Atelia.Galatea.Input.GalateaObservationLimits.MaximumNoteTotalExactTextUtf8Bytes;
}

[Description(
    "One long-term Note that the configured story character explicitly asks runtime to save now. A single save request may contain several Notes; emit each Note separately."
)]
internal sealed record CharacterNoteIntent(
    [property: Required, Description(
        "The complete content of one requested Note, faithfully transcribed from this Action, up to 64 KiB of UTF-8 text. Preserve meaning and literals; never add facts or omit requested content."
    ), JsonPropertyName("text")]
    string Text
);

internal interface ICharacterNoteExtractor {
    string ContractId { get; }

    ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
        string visibleActionText,
        CancellationToken cancellationToken,
        TextExtractionSource? source = null
    );
}

internal sealed class DisabledCharacterNoteExtractor
    : ICharacterNoteExtractor {
    internal const string DisabledContractId =
        "atelia.galatea.character-note-extractor.disabled.v1";

    internal static DisabledCharacterNoteExtractor Instance { get; } = new();

    private DisabledCharacterNoteExtractor() { }

    public string ContractId => DisabledContractId;

    public ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
        string visibleActionText,
        CancellationToken cancellationToken,
        TextExtractionSource? source = null
    ) {
        ArgumentNullException.ThrowIfNull(visibleActionText);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<CharacterNoteIntent>>(
            Array.Empty<CharacterNoteIntent>()
        );
    }
}

internal sealed class CharacterNoteExtractor : ICharacterNoteExtractor {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private const string ContractIdPrefix =
        "atelia.galatea.character-note-extractor.v1.";
    private const string SemanticContractVersion =
        "atelia.galatea.character-note-extractor.semantic.v5";
    private const string ToolContractVersion =
        "emit-character-note-intent.v2";
    private const string VisibleActionRendererVersion =
        "atelia.galatea.visible-action-text-renderer.v1";
    internal const string ToolName = "emit_character_note_intent";

    private const string SystemPromptTemplate = """
You transcribe the long-term Notes covered by the configured character's explicit current save requests in a narrative Action produced by a role-playing model.

The provider Action is a composite GM carrier, not automatically ${characterName}'s own voice.
- A [${characterName}] passage can establish ${characterName}'s first-person intent and action.
- A [旁白] passage can establish only an observable act actually performed by ${characterName}.
- [状态摘要] cannot establish a new Note save request.
- Do not treat the player's request, another character's act, or a quoted or recalled request as ${characterName}'s current request to save.

One current save request may contain several Notes. First identify the current request and all Notes it covers; then emit one tool call for each requested Note, in narrative order, when its complete content is available in this Action. A request to save two Notes requires two tool calls, not one call for the request. Keep different Notes separate, while joining paragraphs that belong to the same Note.
An explicit present request to save is sufficient; no fixed phrase, tool syntax or ceremonial submission action is required.

Emit at most 16 tool calls. Consider requested Notes in narrative order and emit the earliest qualifying Notes first. Stop once 16 have been emitted. Never truncate or summarize a Note to fit a limit.

Do not emit a candidate whose text is clearly over 64 KiB of UTF-8 text. If adding a candidate's text would clearly make the combined emitted text exceed 256 KiB, stop before that candidate and emit no later candidates. Runtime validation is authoritative for structure and size. You need not perform exact UTF-8 byte arithmetic, but do not knowingly exceed these limits.

Determine whether a current save request exists separately from transcribing its requested content. Ordinary thoughts, discoveries, conclusions, dialogue, wishes or decisions to remember, plans, suggestions, drafts, composing, opening an interface, or preparing to submit do not by themselves establish a current request to save. Ordinary diaries, sticky notes, graffiti, mail, and other story-world writing are not by themselves runtime Note save requests. Reading, quoting, or recalling an existing Note is not by itself a new save request. Merely claiming that a Note is already recorded, stored, or saved does not establish a current save request.

These exclusions apply only to recognizing the current request; never use them to exclude the content that the character explicitly asks to save. A requested Note may describe plans, memories, prior writing, previous Notes, quoted information or earlier save results. Once a current request covers that Note, faithfully transcribe its complete content. A reference such as "remember the content above" is insufficient only when the requested content is absent from this Action.

Act as a faithful scribe. You may remove narrative or Markdown wrappers, organize paragraphs belonging to the same Note, and faithfully rephrase them without losing requested content. Preserve the actor, names, numbers, times, negation, conditions and uncertainty. Preserve code, paths, identifiers and other literal text exactly. Never add facts, fill gaps from other turns, summarize away details, or turn a plan into an accomplished fact. If the long-term Note save-request target, complete requested content, actor ownership, or present save request is missing or ambiguous, emit nothing for that candidate.

The target is carried as XML text: read its text value, not the envelope's escaped spelling. For example, &gt; in the envelope represents > in the Action, while &amp;gt; represents the literal text &gt;. Preserve such literals when they belong to the requested Note; do not repeatedly decode them.

Ordinary response text is diagnostic only. Use emit_character_note_intent for artifacts.
""";

    private const string UserPromptTemplate = """
Identify ${characterName}'s explicit current save requests and all Notes they cover. Transcribe up to 16 earliest qualifying Notes in narrative order, one tool call per Note even when one request covers several Notes. Do not discard requested Note content because it discusses plans, memories, quoted information, previous Notes or earlier save results. Without a current save request, emit no artifacts. Each Note's complete requested content must be available in this Action.
""";

    private readonly TextExtractor _inner;
    private readonly string _userPrompt;
    private readonly string _characterName;

    internal Action<string>? DiagnosticSinkForTest { get; set; }

    internal CharacterNoteExtractor(
        GalateaCharacterName characterName,
        CompletionConnectionConfig connection,
        Func<ICompletionClient> getClient
    ) {
        ArgumentNullException.ThrowIfNull(characterName);
        _characterName = characterName.Value;
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
        var tool = TextExtractorArtifactTool.Create<CharacterNoteIntent>(
            ToolName
        );
        _inner = new TextExtractor(
            systemPrompt,
            TextExtractorToolSet.Create(tool),
            connection,
            getClient
        );
    }

    public string ContractId { get; }

    public async ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
        string visibleActionText,
        CancellationToken cancellationToken,
        TextExtractionSource? source = null
    ) {
        var trace = TextExtractionTrace.Create(
            "character-note", ContractId, _characterName, source,
            visibleActionText, DiagnosticSinkForTest
        );
        TextExtractionResult result = await _inner.ExtractAsync(
                visibleActionText,
                _userPrompt,
                cancellationToken,
                trace
            )
            .ConfigureAwait(false);
        if (result.Artifacts.Count > CharacterNoteBounds.MaximumIntentCount) {
            trace.Emit("text-extraction-business-finished", new {
                outcome = "rejected",
                rawArtifactCount = result.Artifacts.Count,
                acceptedCount = 0,
                reasonCode = "note-too-many-intents",
            });
            throw Invalid(
                "Character note extraction emitted too many intents.",
                "note-too-many-intents"
            );
        }

        var intents = new List<CharacterNoteIntent>(
            result.Artifacts.Count
        );
        int totalTextUtf8Bytes = 0;
        for (int ordinal = 0; ordinal < result.Artifacts.Count; ordinal++) {
            ITextExtractionArtifact artifact = result.Artifacts[ordinal];
            try {
                if (artifact is not TextExtractionArtifact<
                        CharacterNoteIntent> typed) {
                    throw new TextExtractionException(
                        TextExtractionFailureKind.ArtifactCaptureMismatch,
                        "Character note extractor captured an unexpected artifact type.",
                        diagnosticReasonCode: "note-artifact-type-mismatch"
                    );
                }
                CharacterNoteIntent intent = typed.Value;
                int textUtf8Bytes = RequireText(
                    intent.Text,
                    CharacterNoteBounds.MaximumExactTextUtf8Bytes,
                    "text"
                );
                totalTextUtf8Bytes = checked(
                    totalTextUtf8Bytes + textUtf8Bytes
                );
                if (totalTextUtf8Bytes
                        > CharacterNoteBounds
                            .MaximumTotalExactTextUtf8Bytes) {
                    throw Invalid(
                        "Character note text values exceed their total UTF-8 byte limit.",
                        "note-total-text-too-long"
                    );
                }
                intents.Add(intent);
                trace.Emit("text-extraction-business-candidate", new {
                    artifactOrdinal = ordinal,
                    outcome = "accepted",
                    reasonCode = (string?)null,
                    textUtf8Bytes,
                });
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) {
                string reasonCode = exception is TextExtractionException extraction
                    ? extraction.DiagnosticReasonCode
                        ?? extraction.Kind.ToString()
                    : "exception";
                trace.Emit("text-extraction-business-candidate", new {
                    artifactOrdinal = ordinal,
                    outcome = "rejected",
                    reasonCode,
                    exceptionType = exception.GetType().FullName,
                });
                trace.Emit("text-extraction-business-finished", new {
                    outcome = "rejected",
                    rawArtifactCount = result.Artifacts.Count,
                    acceptedCount = intents.Count,
                    rejectedOrdinal = ordinal,
                    reasonCode,
                });
                throw;
            }
        }
        trace.Emit("text-extraction-business-finished", new {
            outcome = "accepted",
            rawArtifactCount = result.Artifacts.Count,
            acceptedCount = intents.Count,
        });
        return Array.AsReadOnly(intents.ToArray());
    }

    private static string CreateContractId(
        string systemPrompt,
        string userPrompt
    ) {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        AppendContractPart(hash, SemanticContractVersion);
        AppendContractPart(hash, VisibleActionRendererVersion);
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

    private static int RequireText(
        string? value,
        int maximumBytes,
        string field
    ) {
        try {
            if (string.IsNullOrWhiteSpace(value)) {
                throw Invalid(
                    $"Character note {field} must not be blank.",
                    $"note-{field}-blank"
                );
            }
            int byteCount = TextExtractorUtf8.GetByteCount(value);
            if (byteCount > maximumBytes) {
                throw Invalid(
                    $"Character note {field} exceeds its UTF-8 byte limit.",
                    $"note-{field}-too-long"
                );
            }
            return byteCount;
        }
        catch (EncoderFallbackException exception) {
            throw new TextExtractionException(
                TextExtractionFailureKind.ToolExecutionFailed,
                $"Character note {field} is not strict UTF-8 text.",
                innerException: exception,
                diagnosticReasonCode: $"note-{field}-invalid-utf8"
            );
        }
    }

    private static TextExtractionException Invalid(
        string message,
        string reasonCode
    ) => new(
        TextExtractionFailureKind.ToolExecutionFailed,
        message,
        diagnosticReasonCode: reasonCode
    );
}
