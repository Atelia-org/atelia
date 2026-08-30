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
    "One long-term Note save request that the configured story character actually finished submitting to runtime."
)]
internal sealed record CharacterNoteIntent(
    [property: Required, Description(
        "The complete requested Note text copied exactly from the target Action, up to 64 KiB of UTF-8 text. Never invent, complete, rewrite, summarize, polish, or truncate it."
    ), JsonPropertyName("exactText")]
    string ExactText,
    [property: Required, Description(
        "An exact quote from the target Action, up to 8 KiB of UTF-8 text, proving that the configured story character completed submitting this long-term Note save request to runtime."
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
        "atelia.galatea.character-note-extractor.semantic.v4";
    private const string ToolContractVersion =
        "emit-character-note-intent.v1";
    private const string VisibleActionRendererVersion =
        "atelia.galatea.visible-action-text-renderer.v1";
    internal const string ToolName = "emit_character_note_intent";

    private const string SystemPromptTemplate = """
You extract completed long-term Note save-request submissions from a narrative Action produced by a role-playing model.

The provider Action is a composite GM carrier, not automatically ${characterName}'s own voice.
- A [${characterName}] passage can establish ${characterName}'s first-person intent and action.
- A [旁白] passage can establish only an observable act actually performed by ${characterName}.
- [状态摘要] cannot establish a new Note request-submission act.
- Never attribute the player's request, another character's act, quoted text, recalled memory, existing notes, or inbound information to ${characterName}.

Emit one tool call per request, in narrative order, only when ${characterName} actually finishes submitting a long-term Note save request to runtime and the complete requested Note text appears in this Action.

Emit at most 16 tool calls. Consider qualifying candidates in narrative order and emit the earliest qualifying candidates first. Stop once 16 have been emitted. Never truncate or rewrite a candidate to fit a limit.

Do not emit a candidate whose exactText is clearly over 64 KiB of UTF-8 text or whose evidenceQuote is clearly over 8 KiB of UTF-8 text. If adding a candidate's exactText would clearly make the combined emitted exactText exceed 256 KiB, stop before that candidate and emit no later candidates. Runtime validation is authoritative. You need not perform exact UTF-8 byte arithmetic, but do not knowingly exceed these limits.

Ordinary thoughts, discoveries, conclusions, dialogue, wishes or decisions to remember, plans, suggestions, drafts, composing, opening an interface, preparing to submit, and incomplete submissions are not completed Note request submissions. Ordinary diaries, sticky notes, graffiti, mail, and other story-world writing are not submissions. Reading, quoting, or recalling an existing Note is not a new submission. Merely claiming that a Note is already recorded, stored, or saved is not a request submission. A reference such as "remember the content above" is insufficient when the complete requested Note text is absent from this Action.

Copy exactText and evidenceQuote verbatim from the Action. Never invent, complete, rewrite, summarize, or polish them. evidenceQuote must prove both actor ownership and completed request submission. If the long-term Note save-request target, complete requested text, actor ownership, or completed submission is missing or ambiguous, emit nothing for that candidate.

Ordinary response text is diagnostic only. Use emit_character_note_intent for artifacts.
""";

    private const string UserPromptTemplate = """
Extract up to 16 earliest qualifying long-term Note save requests that ${characterName} actually finished submitting in this Action. Preserve narrative order. Be conservative: thoughts, plans, drafts, ordinary writing, quoted or existing Notes, claims of prior saving, and incomplete submissions produce no artifact.
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
