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
    internal const int MaximumExactTextUtf8Bytes = 64 * 1024;
    internal const int MaximumEvidenceQuoteUtf8Bytes = 8 * 1024;
    internal const int MaximumIntentCount = 16;
    internal const int MaximumTotalExactTextUtf8Bytes = 256 * 1024;
}

[Description(
    "One long-term note that the configured story character actually finished recording in their own autonomous memory."
)]
internal sealed record CharacterNoteIntent(
    [property: Required, Description(
        "The complete note text copied exactly from the target Action. Never invent, complete, rewrite, summarize, or polish it."
    ), JsonPropertyName("exactText")]
    string ExactText,
    [property: Required, Description(
        "An exact quote from the target Action proving that the configured story character completed recording this note in their own long-term memory."
    ), JsonPropertyName("evidenceQuote")]
    string EvidenceQuote
);

internal interface ICharacterNoteExtractor {
    string ContractId { get; }

    ValueTask<IReadOnlyList<CharacterNoteIntent>> ExtractAsync(
        string visibleActionText,
        CancellationToken cancellationToken
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
        CancellationToken cancellationToken
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
        "atelia.galatea.character-note-extractor.semantic.v1";
    private const string ToolContractVersion =
        "emit-character-note-intent.v1";
    private const string VisibleActionRendererVersion =
        "atelia.galatea.visible-action-text-renderer.v1";
    internal const string ToolName = "emit_character_note_intent";

    private const string SystemPromptTemplate = """
You extract completed long-term note records from a narrative Action produced by a role-playing model.

The provider Action is a composite GM carrier, not automatically ${characterName}'s own voice.
- A [${characterName}] passage can establish ${characterName}'s first-person intent and action.
- A [旁白] passage can establish only an observable act actually performed by ${characterName}.
- [状态摘要] cannot establish a new note-recording act.
- Never attribute the player's request, another character's act, quoted text, recalled memory, existing notes, or inbound information to ${characterName}.

Emit one tool call per note, in narrative order, only when ${characterName} actually finishes recording it as their own long-term Note, Memo, or autonomous memory and the complete note text appears in this Action.

Ordinary thoughts, discoveries, conclusions, dialogue, wishes or decisions to remember, plans, suggestions, drafts, composing, opening an interface, preparing to save, and incomplete writes are not completed note records. Ordinary diaries, sticky notes, graffiti, mail, and other story-world writing are not long-term Notes unless the Action explicitly says so. Reading, quoting, or recalling an existing Note is not a new record. A reference such as "remember the content above" is insufficient when the complete note text is absent from this Action.

Copy exactText and evidenceQuote verbatim from the Action. Never invent, complete, rewrite, summarize, or polish them. evidenceQuote must prove both actor ownership and completed recording. If the long-term-memory target, complete text, actor ownership, or completed action is missing or ambiguous, emit nothing for that candidate.

Ordinary response text is diagnostic only. Use emit_character_note_intent for artifacts.
""";

    private const string UserPromptTemplate = """
Extract zero or more long-term Notes that ${characterName} actually finished recording in this Action. Preserve narrative order. Be conservative: thoughts, plans, drafts, ordinary writing, quoted or existing Notes, and incomplete records produce no artifact.
""";

    private readonly TextExtractor _inner;
    private readonly string _userPrompt;

    internal CharacterNoteExtractor(
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
        CancellationToken cancellationToken
    ) {
        TextExtractionResult result = await _inner.ExtractAsync(
                visibleActionText,
                _userPrompt,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (result.Artifacts.Count > CharacterNoteBounds.MaximumIntentCount) {
            throw Invalid(
                "Character note extraction emitted too many intents."
            );
        }

        var intents = new List<CharacterNoteIntent>(
            result.Artifacts.Count
        );
        int totalExactTextUtf8Bytes = 0;
        foreach (ITextExtractionArtifact artifact in result.Artifacts) {
            if (artifact is not TextExtractionArtifact<
                    CharacterNoteIntent> typed) {
                throw new TextExtractionException(
                    TextExtractionFailureKind.ArtifactCaptureMismatch,
                    "Character note extractor captured an unexpected artifact type."
                );
            }
            CharacterNoteIntent intent = typed.Value;
            int exactTextUtf8Bytes = RequireText(
                intent.ExactText,
                CharacterNoteBounds.MaximumExactTextUtf8Bytes,
                "exactText"
            );
            _ = RequireText(
                intent.EvidenceQuote,
                CharacterNoteBounds.MaximumEvidenceQuoteUtf8Bytes,
                "evidenceQuote"
            );
            RequireSourceGrounding(
                visibleActionText,
                intent.ExactText,
                "exactText"
            );
            RequireSourceGrounding(
                visibleActionText,
                intent.EvidenceQuote,
                "evidenceQuote"
            );
            totalExactTextUtf8Bytes = checked(
                totalExactTextUtf8Bytes + exactTextUtf8Bytes
            );
            if (totalExactTextUtf8Bytes
                    > CharacterNoteBounds
                        .MaximumTotalExactTextUtf8Bytes) {
                throw Invalid(
                    "Character note exactText values exceed their total UTF-8 byte limit."
                );
            }
            intents.Add(intent);
        }
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
                    $"Character note {field} must not be blank."
                );
            }
            int byteCount = TextExtractorUtf8.GetByteCount(value);
            if (byteCount > maximumBytes) {
                throw Invalid(
                    $"Character note {field} exceeds its UTF-8 byte limit."
                );
            }
            return byteCount;
        }
        catch (EncoderFallbackException exception) {
            throw new TextExtractionException(
                TextExtractionFailureKind.ToolExecutionFailed,
                $"Character note {field} is not strict UTF-8 text.",
                innerException: exception
            );
        }
    }

    private static void RequireSourceGrounding(
        string visibleActionText,
        string value,
        string field
    ) {
        if (!visibleActionText.Contains(
                value,
                StringComparison.Ordinal)) {
            throw Invalid(
                $"Character note {field} is not an ordinal substring of the visible Action."
            );
        }
    }

    private static TextExtractionException Invalid(string message) => new(
        TextExtractionFailureKind.ToolExecutionFailed,
        message
    );
}
