using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.Tests;

internal static class PreparedV5Fixture {
    public static CompletionRequestPreparedBody Create(
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
        _ = activeArtifactSetAddress;
        SessionRequestContextInput system = ContextInput(
            new SessionRequestArtifactContextSnapshot("fixture system", "", "")
        );
        SessionRequestContextInput observation = ContextInput(
            new SessionRequestArtifactContextSnapshot("", "fixture observation", "")
        );
        return new CompletionRequestPreparedBody(
            new SessionRequestOrigin(correlationId, reason),
            new SessionExecutionCheckpoint(checkpoint),
            new SessionContextPlan(
                rawStartExclusive,
                new string('a', 64),
                new SessionGoverningSetupReferences(
                    new SessionSetupReference(runtimeSetup, 1, new string('b', 64)),
                    new SessionSetupReference(promptSetup, 1, new string('c', 64))
                ),
                [system, observation]
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

    private static SessionRequestContextInput ContextInput(
        SessionRequestArtifactContextSnapshot snapshot
    ) => new(
        SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
        snapshot
    );
}
