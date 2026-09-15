using System.Text;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.Tests;

/// <summary>Test-only legacy encoding; production writes only the current layout.</summary>
internal static class LegacyPreparedV7TestFixture {
    /// <summary>Explicitly freezes a current semantic plan into the historical exact contract.</summary>
    public static CompletionRequestPreparedBody FreezeCurrent(CompletionRequestPreparedBody current, CompletionRequest request) {
        if (current.Commitment is not null) { return current; }
        var snapshots = current.Plan.SemanticContributions.Select(c => SessionContextContributionRenderer.RenderOneHot(c.Target, c.ExactText));
        return current with {
            Plan = current.Plan with {
                SemanticContributions = [],
                ExactContextInputs = [.. snapshots.Select(snapshot => new SessionRequestContextInput(SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot), snapshot))]
            },
            Recipe = current.Recipe with { RecipeId = SessionRequestManifestDefaults.RecipeId },
            Commitment = SessionRequestCanonicalizer.CreateCommitment(request)
        };
    }

    public static CompletionAttemptStartedBody StartedFor(EventJournal.EventJournal journal, EventAddress prepared) {
        var reconstruction = SessionPreparedRequestReconstructor.Reconstruct(journal, prepared);
        return reconstruction.Manifest.Commitment is null
            ? new(SessionRequestManifestDefaults.CanonicalRequestCodecId, SessionRequestCanonicalizer.CreateCommitment(reconstruction.Request))
            : new();
    }
    public static byte[] Encode(
        CompletionRequestPreparedBody manifest,
        string adapterLabel = "historical-adapter-label"
    ) {
        JsonNode envelope = JsonNode.Parse(SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            manifest
        ))!;
        envelope["v"] = SessionRequestManifestDefaults.LegacyBodySchemaVersionV7;
        envelope["body"]!["target"]!["connection"]!["requestAdapterFingerprint"] =
            adapterLabel;
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }
}
