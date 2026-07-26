using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.Tests;

internal static class PreparedV2Fixture {
    public static CompletionRequestPreparedBody Create(
        string attemptId,
        string correlationId,
        string reason,
        EventAddress rawStartExclusive,
        EventAddress activeArtifactSetAddress,
        EventAddress runtimeSetup,
        EventAddress promptSetup,
        string modelId,
        ImmutableArray<ToolDefinition> tools,
        SessionToolRuntimeIdentity? toolRuntimeIdentity,
        long checkpoint = 0
    ) {
        SessionRequestArtifactInput system = Artifact(
            "fixture-system",
            "fixture",
            new SessionRequestArtifactContextSnapshot("fixture system", "", "")
        );
        SessionRequestArtifactInput observation = Artifact(
            "fixture-observation",
            "fixture",
            new SessionRequestArtifactContextSnapshot("", "fixture observation", "")
        );
        return new CompletionRequestPreparedBody(
            new SessionRequestAttempt(attemptId, correlationId, reason),
            new SessionExecutionCheckpoint(checkpoint),
            new SessionContextPlan(
                rawStartExclusive,
                new string('a', 64),
                [system, observation],
                new SessionArtifactSetReference(
                    activeArtifactSetAddress,
                    1,
                    new string('e', 64)
                )
            ),
            new SessionGoverningSetupReferences(
                new SessionSetupReference(runtimeSetup, 1, new string('b', 64)),
                new SessionSetupReference(promptSetup, 1, new string('c', 64))
            ),
            new SessionRequestParameters(modelId, MaxTokens: null),
            new SessionRequestToolSet(
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestCanonicalizer.ComputeToolSetSha256(tools),
                tools,
                toolRuntimeIdentity
            ),
            new SessionRequestRecipe(
                SessionRequestManifestDefaults.RecipeId,
                SessionRequestManifestDefaults.CanonicalRequestCodecId
            ),
            new SessionRequestTarget(
                new SessionCompletionTargetIdentity(
                    "connection",
                    "test",
                    "connection-fingerprint",
                    "adapter-fingerprint"
                ),
                "scripted",
                "test-api-v1"
            ),
            new SessionRequestCommitment(1, new string('d', 64))
        );
    }

    private static SessionRequestArtifactInput Artifact(
        string artifactId,
        string artifactKind,
        SessionRequestArtifactContextSnapshot snapshot
    ) => new(
        artifactId,
        artifactKind,
        SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
        snapshot
    );
}
