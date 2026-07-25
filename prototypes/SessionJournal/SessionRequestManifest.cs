using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Identity and replacement relationship of one durable completion attempt.
/// </summary>
internal sealed record SessionRequestAttempt(
    string AttemptId,
    string CorrelationId,
    string Reason,
    string? ReplacesAttemptId
);

/// <summary>
/// Provider-neutral selection plan. The containing event header Parent is the authoritative
/// raw end/based-on head; it is intentionally not repeated in this body.
/// </summary>
internal sealed record SessionContextPlan(
    string SelectionPolicyId,
    string PlannerFingerprint,
    EventAddress? RawStartExclusive,
    string RawRangeSha256,
    ImmutableArray<SessionRequestArtifactInput> ArtifactInputs,
    ImmutableArray<SessionRequestRecalledInput> RecalledInputs,
    string RenderingProfileId,
    string ModelProfileId,
    int EstimatedInputTokens,
    string Reason
);

internal sealed record SessionRequestArtifactInput(
    string ArtifactId,
    string ArtifactKind,
    string ContentSha256
);

internal sealed record SessionRequestRecalledInput(
    string SourceId,
    string ContentSha256
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
/// Complete inline tool-definition snapshot. There is no durable tool catalog in v1.
/// </summary>
internal sealed record SessionRequestToolSet(
    string CodecId,
    string Sha256,
    ImmutableArray<ToolDefinition> Definitions
);

internal sealed record SessionRequestRendering(
    string ContextRendererId,
    string ContextRendererFingerprint,
    string CanonicalRequestCodecId,
    string ToolCodecId,
    string ReasoningCodecSetFingerprint
);

internal sealed record SessionRequestTarget(
    SessionCompletionTargetIdentity Connection,
    string CompletionSurfaceId,
    string ClientName,
    string ApiSpecId
);

internal sealed record SessionRequestCommitment(
    string Algorithm,
    int ByteLength,
    string Sha256
);

internal static class SessionRequestManifestDefaults {
    public const string FullRawSelectionPolicyId = "full-raw";
    public const string FullRawPlannerFingerprint = "atelia.session-journal.full-raw-planner.v1";
    public const string FullRawRenderingProfileId = "atelia.session-journal.full-raw-rendering.v1";
    public const string FullRawContextRendererId = "atelia.session-journal.full-raw.v1";
    public const string FullRawContextRendererFingerprint = "atelia.session-journal.full-raw.v1";

    public const string RecapTailSelectionPolicyId = "atelia.session-journal.recap-tail.v1";
    public const string RecapTailPlannerFingerprint = "atelia.session-journal.recap-tail-planner.v1";
    public const string RecapTailRenderingProfileId = "atelia.session-journal.recap-tail-rendering.v1";
    public const string RecapTailContextRendererId = "atelia.session-journal.recap-tail.v1";
    public const string RecapTailContextRendererFingerprint = "atelia.session-journal.recap-tail.v1";

    // Existing aliases remain the full-raw defaults so current callers and wire bytes do not drift.
    public const string SelectionPolicyId = FullRawSelectionPolicyId;
    public const string PlannerFingerprint = FullRawPlannerFingerprint;
    public const string RenderingProfileId = FullRawRenderingProfileId;
    public const string ContextRendererId = FullRawContextRendererId;
    public const string ContextRendererFingerprint = FullRawContextRendererFingerprint;
    public const string ReasoningCodecSetFingerprint = "atelia.reasoning-codec-set.v1";
    public const string CanonicalRequestCodecId = "atelia.completion-request.canonical-json.v1";
    public const string ToolCodecId = "atelia.tool-definition.canonical-json.v1";
    public const string CommitmentAlgorithm = "sha-256";
}
