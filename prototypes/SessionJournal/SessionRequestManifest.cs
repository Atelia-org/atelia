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
    SessionGoverningSetupReferences RawStartSetups,
    ImmutableArray<SessionRequestContextInput> ExactContextInputs
);

/// <summary>
/// An exact, already-rendered contribution to a prepared provider request.  This is an execution
/// fact, not an artifact identity: it intentionally carries no derived-store, epoch, target, or
/// renderer input provenance.
/// </summary>
internal sealed record SessionRequestContextInput(
    string ContentSha256,
    SessionRequestArtifactContextSnapshot ContextSnapshot
);

internal sealed record SessionRequestArtifactContextSnapshot(
    string SystemPromptFragment,
    string ObservationMessage,
    string ActionMessage
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

/// <summary>
/// Current Prepared v7 provider-neutral request parameters. The absence of an output ceiling is
/// intentional: optional provider fields are omitted and required fields use the model maximum
/// inside the concrete provider client, never in SessionJournal durable state.
/// </summary>
internal sealed record SessionRequestParameters(
    string ModelId
);

/// <summary>
/// Historical Prepared v5 request parameters. <see cref="LegacyMaxTokens"/> exists only so the
/// old canonical-json-v1 commitment can be verified; it is never normalized into a current
/// request or passed to a completion provider.
/// </summary>
internal sealed record HistoricalSessionRequestParametersV5(
    string ModelId,
    int? LegacyMaxTokens
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
    public const int CurrentBodySchemaVersion = 7;
    public const int HistoricalBodySchemaVersionV5 = 5;
    public const string RecipeId =
        "atelia.session-journal.coherent-artifact-tail.recipe.v1";
    public const string CanonicalRequestCodecId = "atelia.completion-request.canonical-json.v2";
    public const string HistoricalCanonicalRequestCodecIdV1 =
        "atelia.completion-request.canonical-json.v1";
    public const string ToolCodecId = "atelia.tool-definition.canonical-json.v1";
}

/// <summary>
/// Read-only representation of a historical Prepared v5 body. Its legacy output ceiling is
/// retained solely for byte-exact commitment verification and has no current execution meaning.
/// </summary>
internal sealed record HistoricalCompletionRequestPreparedV5Body(
    SessionRequestOrigin Origin,
    SessionExecutionCheckpoint Execution,
    SessionContextPlan Plan,
    SessionGoverningSetupReferences Setups,
    HistoricalSessionRequestParametersV5 Parameters,
    SessionRequestToolSet ToolSet,
    SessionRequestRecipe Recipe,
    SessionRequestTarget Target,
    SessionRequestCommitment Commitment
);

/// <summary>
/// Ceiling-free manifest facts shared by lineage, state-machine, and setup validation. This view
/// is not a dispatchable request and deliberately cannot expose a historical v5 output limit.
/// </summary>
internal sealed record SessionPreparedManifestView(
    int BodySchemaVersion,
    SessionRequestOrigin Origin,
    SessionExecutionCheckpoint Execution,
    SessionContextPlan Plan,
    SessionGoverningSetupReferences Setups,
    string ModelId,
    SessionRequestToolSet ToolSet,
    SessionRequestRecipe Recipe,
    SessionRequestTarget Target,
    SessionRequestCommitment Commitment
) {
    public static SessionPreparedManifestView FromDecoded(
        int bodySchemaVersion,
        object body
    ) => (bodySchemaVersion, body) switch {
        (SessionRequestManifestDefaults.CurrentBodySchemaVersion,
            CompletionRequestPreparedBody current) => new(
                bodySchemaVersion,
                current.Origin,
                current.Execution,
                current.Plan,
                current.Setups,
                current.Parameters.ModelId,
                current.ToolSet,
                current.Recipe,
                current.Target,
                current.Commitment
            ),
        (SessionRequestManifestDefaults.HistoricalBodySchemaVersionV5,
            HistoricalCompletionRequestPreparedV5Body historical) => new(
                bodySchemaVersion,
                historical.Origin,
                historical.Execution,
                historical.Plan,
                historical.Setups,
                historical.Parameters.ModelId,
                historical.ToolSet,
                historical.Recipe,
                historical.Target,
                historical.Commitment
            ),
        _ => throw new InvalidDataException(
            "CompletionRequestPrepared body type does not match its schema version."
        )
    };
}
