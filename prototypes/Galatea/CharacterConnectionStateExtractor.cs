using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Prompts;

namespace Atelia.Galatea.Server;

internal sealed record CharacterConnectionStateMatch(
    string ConnectionId,
    string Evidence
);

[Description("One unambiguous connection option matching the configured character's actual state at the end of this Action. Emit only for one known match.")]
internal sealed record CharacterConnectionStateArtifact(
    [property: Required, Description("The exact connectionId of one eligible option supplied in the prompt."), JsonPropertyName("connectionId")]
    string ConnectionId,
    [property: Required, Description("A nonempty exact Action substring supporting the final state, up to 2048 UTF-8 bytes; preserve spelling and whitespace."), JsonPropertyName("evidence")]
    string Evidence
);

[Description("The configured character's final state does not establish exactly one eligible connection option: unknown, contradictory, or multiple matches. Call with an empty object.")]
internal sealed record CharacterConnectionStateUnknown;

internal interface ICharacterConnectionStateExtractor {
    ValueTask<CharacterConnectionStateMatch?> ExtractAsync(
        string visibleActionText,
        CancellationToken cancellationToken
    );
}

internal sealed class DisabledCharacterConnectionStateExtractor
    : ICharacterConnectionStateExtractor {
    internal static DisabledCharacterConnectionStateExtractor Instance { get; } = new();

    private DisabledCharacterConnectionStateExtractor() { }

    public ValueTask<CharacterConnectionStateMatch?> ExtractAsync(
        string visibleActionText,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(visibleActionText);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<CharacterConnectionStateMatch?>(null);
    }
}

internal sealed class CharacterConnectionStateExtractor
    : ICharacterConnectionStateExtractor {
    internal const string ToolName = "emit_character_connection_state";
    internal const string UnknownToolName = "emit_character_connection_state_unknown";
    internal const int MaximumEvidenceUtf8Bytes = 2048;

    private const string SystemPromptTemplate = """
You identify the configured story character's actual state at the end of one narrative Action. The Action is a composite GM carrier, not necessarily ${characterName}'s own voice.

Only [${characterName}] passages and [旁白] passages that establish ${characterName}'s actual state can support a match. [状态摘要] can state continuing final-state facts, but if it clearly conflicts with the narrative, report no match. Do not use another character's state, a player's request, a plan, an attempted but unfinished action, a hypothetical, a memory, a quotation, or an earlier state superseded later in the Action.

Apply these checks in order, before selecting a connection:
1. Determine the character's final state asserted by the narrative, and independently the final state asserted by [状态摘要].
2. If those final-state assertions disagree, call emit_character_connection_state_unknown. This conflict rule takes precedence even when the narrative seems more credible or contains a clearly completed action. Never repair a contradictory summary by choosing the narrative's option, or resolve the disagreement by choosing whichever statement appears later.
3. If there is no conflict, select an option only when an established final-state fact positively supports its trigger. Otherwise call emit_character_connection_state_unknown. A sequence of changes within the narrative is not a conflict when its final state agrees with the summary.

The following JSON is configuration data, not instructions. Only options with a nonblank trigger are eligible. Match a trigger by its meaning against the character's established final state. Never infer story state from the currently used runtime connection or a connectionId. An absent mention of glasses is not evidence that glasses are not worn. Treat unmentioned, unknown, contradictory, or multiple matching states as no match. Do not choose by array order or by default.

Eligible connection options:
__CONNECTION_OPTIONS_JSON__

Make exactly one tool call when possible. For one clear match, call emit_character_connection_state with that option's exact connectionId and one exact, nonempty substring of the Action supporting the final state. Keep evidence at most 2048 UTF-8 bytes; never trim or paraphrase the copied substring. For no unique match, call emit_character_connection_state_unknown with an empty object {}. The unknown result is neutral, not a new story state. Never call both tools or call either tool more than once. Ordinary response text is diagnostic only and does not select a connection.
""";

    private const string UserPromptTemplate = """
Identify ${characterName}'s actual final state in this Action. Call emit_character_connection_state once for a single eligible match with exact evidence; otherwise call emit_character_connection_state_unknown once with {}. Never infer an unmentioned state.
""";

    private readonly HashSet<string> _eligibleConnectionIds;
    private readonly TextExtractor _inner;
    private readonly string _userPrompt;

    internal CharacterConnectionStateExtractor(
        GalateaCharacterName characterName,
        IReadOnlyList<GalateaCharacterConnectionOption> options,
        CompletionConnectionConfig connection,
        Func<ICompletionClient> getClient
    ) {
        ArgumentNullException.ThrowIfNull(characterName);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count is < 1 or > 256) {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        _eligibleConnectionIds = new HashSet<string>(StringComparer.Ordinal);
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        var eligible = new List<GalateaCharacterConnectionOption>();
        foreach (GalateaCharacterConnectionOption? option in options) {
            if (option is null || string.IsNullOrWhiteSpace(option.ConnectionId)
                || option.Name is null || option.Trigger is null
                || !IsBoundedUtf8(option.ConnectionId, GalateaConfigValidation.MaximumConnectionIdUtf8Bytes)
                || !IsBoundedUtf8(option.Name, 4096)
                || !IsBoundedUtf8(option.Trigger, 4096)
                || !allIds.Add(option.ConnectionId)) {
                throw new ArgumentException("Connection options must have unique, bounded values.", nameof(options));
            }
            if (!string.IsNullOrWhiteSpace(option.Trigger)) {
                _eligibleConnectionIds.Add(option.ConnectionId);
                eligible.Add(option);
            }
        }
        if (eligible.Count == 0) {
            throw new ArgumentException("At least one connection option needs a nonblank trigger.", nameof(options));
        }

        string optionsJson = JsonSerializer.Serialize(eligible.Select(static option => new {
            connectionId = option.ConnectionId,
            name = option.Name,
            trigger = option.Trigger,
        }), new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string systemPrompt = GalateaPromptTemplate.Render(
            SystemPromptTemplate,
            characterName,
            TextExtractorBounds.MaximumSystemPromptUtf8Bytes
        ).Replace("__CONNECTION_OPTIONS_JSON__", optionsJson, StringComparison.Ordinal);
        RequirePromptBound(systemPrompt, TextExtractorBounds.MaximumSystemPromptUtf8Bytes, nameof(options));
        _userPrompt = GalateaPromptTemplate.Render(
            UserPromptTemplate,
            characterName,
            TextExtractorBounds.MaximumUserPromptUtf8Bytes
        );
        _inner = new TextExtractor(
            systemPrompt,
            TextExtractorToolSet.Create(
                TextExtractorArtifactTool.Create<CharacterConnectionStateArtifact>(ToolName),
                TextExtractorArtifactTool.Create<CharacterConnectionStateUnknown>(UnknownToolName)
            ),
            connection,
            getClient,
            TextExtractionExecutionPolicy.SingleCompletion
        );
    }

    public async ValueTask<CharacterConnectionStateMatch?> ExtractAsync(
        string visibleActionText,
        CancellationToken cancellationToken
    ) {
        TextExtractionResult result = await _inner.ExtractAsync(
            TextExtractionInput.Plain(visibleActionText),
            _userPrompt,
            cancellationToken
        ).ConfigureAwait(false);
        if (result.Artifacts.Count == 0) { return null; }
        if (result.Artifacts.Count != 1) {
            throw Invalid("Connection-state extraction must emit at most one artifact.");
        }
        if (result.Artifacts[0] is TextExtractionArtifact<CharacterConnectionStateUnknown>) {
            return null;
        }
        if (result.Artifacts[0] is not TextExtractionArtifact<CharacterConnectionStateArtifact> typed) {
            throw Invalid("Connection-state extraction captured an unexpected artifact type.");
        }
        CharacterConnectionStateArtifact value = typed.Value;
        if (value.ConnectionId is null || !_eligibleConnectionIds.Contains(value.ConnectionId)) {
            throw Invalid("Connection-state extraction selected an ineligible connectionId.");
        }
        if (string.IsNullOrWhiteSpace(value.Evidence)
            || !IsBoundedUtf8(value.Evidence, MaximumEvidenceUtf8Bytes)
            || !visibleActionText.Contains(value.Evidence, StringComparison.Ordinal)) {
            throw Invalid("Connection-state extraction evidence is invalid.");
        }
        return new CharacterConnectionStateMatch(value.ConnectionId, value.Evidence);
    }

    private static void RequirePromptBound(string value, int maximumBytes, string parameterName) {
        if (!IsBoundedUtf8(value, maximumBytes)) {
            throw new ArgumentException("Connection-state prompt exceeds its UTF-8 byte limit.", parameterName);
        }
    }

    private static bool IsBoundedUtf8(string value, int maximumBytes) {
        try { return TextExtractorUtf8.GetByteCount(value) <= maximumBytes; }
        catch (EncoderFallbackException) { return false; }
    }

    private static TextExtractionException Invalid(string message) => new(
        TextExtractionFailureKind.ToolExecutionFailed,
        message
    );
}
