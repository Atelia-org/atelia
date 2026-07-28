namespace Atelia.SessionJournal.DerivedMemory;

/// <summary>
/// Concrete Latest provider over one derived-only ArtifactSet lineage. It never opens or reads the
/// raw SessionJournal; raw ancestry and setup assertions remain authoritative core validations.
/// In DM-3B, <see cref="SessionContextSelectionRequest.RawSuffixTokenBudget"/> is a non-binding
/// planning hint: Latest selection does not search older sets or guarantee that budget.
/// </summary>
public sealed class DerivedArtifactSetContextCandidateSource
    : ICoherentContextCandidateSource {
    private readonly DerivedMemoryRepository _repository;
    private readonly DerivedArtifactSetPolicy _policy;
    private readonly string _lineageKey;

    public DerivedArtifactSetContextCandidateSource(
        DerivedMemoryRepository repository,
        DerivedArtifactSetPolicy policy,
        string lineageKey
    ) {
        _repository = repository
            ?? throw new ArgumentNullException(nameof(repository));
        _policy = policy
            ?? throw new ArgumentNullException(nameof(policy));
        _ = _policy.ValidateAndSnapshot();
        DerivedArtifactSetPolicy.ValidateLineageKey(lineageKey);
        _lineageKey = lineageKey;
    }

    public async ValueTask<SessionContextCandidate?> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateShape();
        if (request.Mode != SessionContextSelectionMode.Latest
            || !string.Equals(
                request.CoherenceGroup,
                _policy.CoherenceGroup,
                StringComparison.Ordinal
            )) {
            return null;
        }

        DerivedArtifactSet? set = await _repository.ArtifactSets
            .TryReadLatestAsync(
                _policy,
                _lineageKey,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (set is null) {
            return null;
        }

        var contributions = new List<SessionContextContribution>(
            set.Members.Count
        );
        foreach (DerivedArtifactSetMember member in set.Members) {
            cancellationToken.ThrowIfCancellationRequested();
            DerivedMemoryArtifact artifact = await _repository.ArtifactSets
                .ReadAndValidateMemberArtifactAsync(
                    set,
                    member,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!artifact.MemoryPack.TryGetBlock(
                    artifact.Target,
                    out MemoryPackBlock block
                )) {
                throw new InvalidDataException(
                    $"Derived ArtifactSet member '{member.ArtifactId}' is missing its target text."
                );
            }
            contributions.Add(new SessionContextContribution(
                member.Target,
                block.Text,
                member.ContentCodecId,
                member.ContentSha256,
                member.SourceRawHead
            ));
        }
        return new SessionContextCandidate(
            set.CommonAnchor,
            set.AnchorSetups,
            contributions.AsReadOnly()
        );
    }
}
