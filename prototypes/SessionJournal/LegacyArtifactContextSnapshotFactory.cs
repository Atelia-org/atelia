using Atelia.SessionJournal.Derived;

namespace Atelia.SessionJournal;

/// <summary>
/// Legacy v3/kind-12 snapshot bridge used by raw activation commit and offline validation.
/// It survives the DM-3 planning-adapter deletion, then is deleted with raw ArtifactSetCommitted in DM-4.
/// </summary>
internal static class LegacyArtifactContextSnapshotFactory {
    public static SessionRequestArtifactInput CreateLegacyArtifactInput(
        DerivedRecapArtifact artifact
    ) {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!artifact.MemoryPack.TryGetBlock(artifact.Target, out MemoryPackBlock block)
            || !string.Equals(block.Text, artifact.Content, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"Recap artifact '{artifact.ArtifactId}' is missing its exact target block content."
            );
        }
        SessionRequestArtifactContextSnapshot snapshot =
            SessionCoherentRequestRecipe.CreateOneHotSnapshot(artifact.Target, block.Text);
        return new SessionRequestArtifactInput(
            artifact.ArtifactId,
            artifact.ArtifactKind,
            SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
            snapshot
        );
    }
}
