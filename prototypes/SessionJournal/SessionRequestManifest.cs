using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Stable origin metadata for one prepared request. Provider-attempt identity is
/// the address of the CompletionAttemptStarted event and is deliberately absent here.
/// </summary>
internal sealed record SessionRequestOrigin(
    string CorrelationId,
    string Reason
);

/// <summary>
/// Provider-neutral selection plan. The containing event header Parent is the authoritative
/// raw end/based-on head; it is intentionally not repeated in this body.
/// </summary>
internal sealed record SessionContextPlan(
    EventAddress RawStartExclusive,
    string RawRangeSha256,
    ImmutableArray<SessionRequestArtifactInput> ArtifactInputs,
    SessionArtifactSetReference ActiveArtifactSet
);

internal sealed record SessionRequestArtifactInput(
    string ArtifactId,
    string ArtifactKind,
    string ContentSha256,
    SessionRequestArtifactContextSnapshot ContextSnapshot
);

internal sealed record SessionRequestArtifactContextSnapshot(
    string SystemPromptFragment,
    string ObservationMessage,
    string ActionMessage
);

internal sealed record SessionArtifactSetReference(
    EventAddress Address,
    int BodySchemaVersion,
    string PayloadSha256
);

/// <summary>
/// A direct, independently verifiable pointer to a governing setup payload.
/// </summary>
internal sealed record SessionSetupReference(
    EventAddress Address,
    int BodySchemaVersion,
    string PayloadSha256
);

internal sealed record SessionGoverningSetupReferences(
    SessionSetupReference RuntimeConfig,
    SessionSetupReference SystemPrompt
);

internal sealed record SessionRequestParameters(
    string ModelId,
    int? MaxTokens
);

/// <summary>
/// Minimal operational checkpoint needed to allocate the next durable tool execution
/// sequence without replaying the raw prefix.
/// </summary>
internal sealed record SessionExecutionCheckpoint(
    long LastIssuedToolExecutionSequence
);

/// <summary>
/// Complete inline tool-definition snapshot. There is no durable tool catalog in v1.
/// </summary>
internal sealed record SessionRequestToolSet(
    string CodecId,
    string Sha256,
    ImmutableArray<ToolDefinition> Definitions,
    SessionToolRuntimeIdentity? RuntimeIdentity
);

internal sealed record SessionRequestRecipe(
    string RecipeId,
    string CanonicalRequestCodecId
);

internal sealed record SessionRequestTarget(
    SessionCompletionTargetIdentity Connection,
    string ClientName,
    string ApiSpecId
);

internal sealed record SessionRequestCommitment(
    int ByteLength,
    string Sha256
);

internal static class SessionRequestManifestDefaults {
    public const string ActiveArtifactSetPolicyId =
        "atelia.session-journal.active-artifact-set.v1";
    public const string ActiveArtifactSetPolicyFingerprint =
        "atelia.session-journal.active-artifact-set.v1";
    public const string RecipeId =
        "atelia.session-journal.coherent-artifact-tail.recipe.v1";
    public const string CanonicalRequestCodecId = "atelia.completion-request.canonical-json.v1";
    public const string ToolCodecId = "atelia.tool-definition.canonical-json.v1";
}
