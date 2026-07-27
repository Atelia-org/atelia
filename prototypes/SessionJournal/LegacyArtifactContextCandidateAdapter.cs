using System.Collections.Immutable;
using Atelia.EventJournal;
using Atelia.SessionJournal.Derived;

namespace Atelia.SessionJournal;

/// <summary>
/// Transitional bridge from the v3 raw ArtifactSetCommitted + DerivedRecapStore shape to the neutral
/// candidate contract. Delete with the concrete provider cutover in DM-3; it is not a second policy.
/// </summary>
internal sealed class LegacyArtifactContextCandidateAdapter {
    private readonly ImmutableDictionary<MemoryPackBlockPath, LegacyArtifactIdentity> _identities;

    private LegacyArtifactContextCandidateAdapter(
        SessionContextCandidate candidate,
        ImmutableDictionary<MemoryPackBlockPath, LegacyArtifactIdentity> identities
    ) {
        Candidate = candidate;
        _identities = identities;
    }

    public SessionContextCandidate Candidate { get; }

    public static LegacyArtifactContextCandidateAdapter Create(
        SessionActiveArtifactSet active,
        ImmutableArray<DerivedRecapArtifact> artifacts,
        IReadOnlySet<EventAddress> allowedSourceHeads
    ) {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(allowedSourceHeads);
        if (artifacts.Length != active.Body.Members.Length) {
            throw new InvalidDataException("Active artifact-set members do not match the exact loaded artifact count.");
        }

        var artifactsById = new Dictionary<string, DerivedRecapArtifact>(StringComparer.Ordinal);
        foreach (DerivedRecapArtifact artifact in artifacts) {
            ArgumentNullException.ThrowIfNull(artifact);
            if (string.IsNullOrWhiteSpace(artifact.ArtifactId)
                || !artifactsById.TryAdd(artifact.ArtifactId, artifact)) {
                throw new InvalidDataException("Active artifact-set artifacts must have distinct non-empty ids.");
            }
        }

        var contributions = ImmutableArray.CreateBuilder<SessionContextContribution>(artifacts.Length);
        var identities = ImmutableDictionary.CreateBuilder<MemoryPackBlockPath, LegacyArtifactIdentity>();
        foreach (SessionArtifactSetMember member in active.Body.Members) {
            if (!artifactsById.TryGetValue(member.ArtifactId, out DerivedRecapArtifact? artifact)) {
                throw new LegacyArtifactContextCandidateMismatchException(
                    member.ArtifactId,
                    $"Active artifact-set member '{member.ArtifactId}' is missing its exact artifact."
                );
            }
            MemoryPackBlock block;
            try {
                ValidateLegacyArtifact(active, member, artifact, allowedSourceHeads);
                SessionRequestArtifactInput legacyInput =
                    LegacyArtifactContextSnapshotFactory.CreateLegacyArtifactInput(artifact);
                if (!string.Equals(
                        legacyInput.ContentSha256,
                        member.ContentSha256,
                        StringComparison.Ordinal
                    )) {
                    throw new InvalidDataException(
                        $"Active artifact-set member '{artifact.ArtifactId}' does not match its committed context snapshot."
                    );
                }
                if (!artifact.MemoryPack.TryGetBlock(artifact.Target, out block)) {
                    throw new InvalidDataException(
                        $"Active artifact-set member '{artifact.ArtifactId}' is missing its exact target block content."
                    );
                }
            }
            catch (InvalidDataException exception) {
                throw new LegacyArtifactContextCandidateMismatchException(
                    member.ArtifactId,
                    exception.Message,
                    exception
                );
            }
            if (!identities.TryAdd(
                    artifact.Target,
                    new LegacyArtifactIdentity(
                        artifact.ArtifactId,
                        artifact.ArtifactKind,
                        member.ContentSha256
                    )
                )) {
                throw new InvalidDataException("Active artifact-set members must have unique target blocks.");
            }
            contributions.Add(new SessionContextContribution(
                artifact.Target,
                block.Text,
                SessionContextContributionHasher.CodecId,
                SessionContextContributionHasher.ComputeSha256(block.Text),
                artifact.SourceRawHead
            ));
        }

        var candidate = new SessionContextCandidate(
            active.Body.CommonAnchor,
            new SessionContextAnchorSetupReferences(
                ToCandidateSetupReference(active.Body.CoverageSetups.RuntimeConfig),
                ToCandidateSetupReference(active.Body.CoverageSetups.SystemPrompt)
            ),
            contributions.ToImmutable()
        );
        return new LegacyArtifactContextCandidateAdapter(candidate, identities.ToImmutable());
    }

    public ImmutableArray<SessionRequestArtifactInput> CreateV3ArtifactInputs(
        ImmutableArray<SessionContextContribution> canonicalContributions,
        ImmutableArray<SessionRequestArtifactContextSnapshot> contextSnapshots
    ) {
        if (canonicalContributions.Length != contextSnapshots.Length
            || canonicalContributions.Length != _identities.Count) {
            throw new InvalidDataException("Legacy artifact identity mapping does not match materialized candidate contributions.");
        }

        var inputs = ImmutableArray.CreateBuilder<SessionRequestArtifactInput>(contextSnapshots.Length);
        for (int i = 0; i < contextSnapshots.Length; i++) {
            SessionContextContribution contribution = canonicalContributions[i];
            if (!_identities.TryGetValue(contribution.Target, out LegacyArtifactIdentity? identity)) {
                throw new InvalidDataException("Materialized candidate target is not part of the legacy active artifact set.");
            }
            SessionRequestArtifactContextSnapshot snapshot = contextSnapshots[i];
            string snapshotHash = SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot);
            if (!string.Equals(snapshotHash, identity.CommittedSnapshotSha256, StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    $"Legacy artifact '{identity.ArtifactId}' does not match its committed context snapshot."
                );
            }
            inputs.Add(new SessionRequestArtifactInput(
                identity.ArtifactId,
                identity.ArtifactKind,
                snapshotHash,
                snapshot
            ));
        }
        return inputs.MoveToImmutable();
    }

    private static void ValidateLegacyArtifact(
        SessionActiveArtifactSet active,
        SessionArtifactSetMember member,
        DerivedRecapArtifact artifact,
        IReadOnlySet<EventAddress> allowedSourceHeads
    ) {
        if (!string.Equals(artifact.Status, DerivedRecapArtifactStatus.Produced, StringComparison.Ordinal)
            || !string.Equals(artifact.ArtifactId, member.ArtifactId, StringComparison.Ordinal)
            || !string.Equals(artifact.ArtifactKind, member.ArtifactKind, StringComparison.Ordinal)
            || artifact.Target != member.Target
            || artifact.AnchorRawEvent != active.Body.CommonAnchor
            || artifact.SourceEndInclusive != active.Body.CommonAnchor
            || artifact.GoverningRuntimeConfigSetup != active.Body.CoverageSetups.RuntimeConfig.Address
            || artifact.GoverningSystemPromptSetup != active.Body.CoverageSetups.SystemPrompt.Address
            || !allowedSourceHeads.Contains(artifact.SourceRawHead)) {
            throw new InvalidDataException(
                $"Active artifact-set member '{member.ArtifactId}' does not match its committed identity, coverage, or context contribution."
            );
        }
    }

    private static SessionContextSetupReference ToCandidateSetupReference(
        SessionSetupReference reference
    ) => new(reference.Address, reference.BodySchemaVersion, reference.PayloadSha256);

    private sealed record LegacyArtifactIdentity(
        string ArtifactId,
        string ArtifactKind,
        string CommittedSnapshotSha256
    );
}

internal sealed class LegacyArtifactContextCandidateMismatchException : IOException {
    public LegacyArtifactContextCandidateMismatchException(
        string artifactId,
        string message,
        Exception? innerException = null
    ) : base(message, innerException) {
        ArtifactId = artifactId;
    }

    public string ArtifactId { get; }
}
