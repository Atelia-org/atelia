namespace Atelia.SessionJournal.DerivedMemory;

/// <summary>
/// Bounded two-phase provider over one exact ArtifactSet lineage. Discovery reads set metadata
/// only; exact contribution text is loaded only for a descriptor selected by SessionJournal raw
/// authority. This provider never opens the raw journal.
/// </summary>
public sealed class DerivedArtifactSetContextCandidateSource
    : ICoherentContextCandidateSource {
    private readonly DerivedMemoryRepository _repository;
    private readonly DerivedArtifactSetPolicy _policy;
    private readonly DerivedMemoryBranchScope _scope;

    public DerivedArtifactSetContextCandidateSource(
        DerivedMemoryRepository repository,
        DerivedArtifactSetPolicy policy,
        DerivedMemoryBranchScope scope
    ) {
        _repository = repository
            ?? throw new ArgumentNullException(nameof(repository));
        _policy = policy
            ?? throw new ArgumentNullException(nameof(policy));
        _ = _policy.ValidateAndSnapshot();
        _repository.RequireScope(scope);
        _scope = scope;
    }

    public async ValueTask<SessionContextCandidateDiscovery> DiscoverAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateShape();
        if (!string.Equals(
                request.CoherenceGroup,
                _policy.CoherenceGroup,
                StringComparison.Ordinal
            )) {
            return new(
                SessionContextCandidateDiscoveryStatus.Candidates,
                Array.Empty<SessionContextCandidateDescriptor>()
            );
        }

        DerivedArtifactSet? current = await _repository.ArtifactSets
            .TryReadLatestAsync(
                _policy,
                _scope,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (current is null) {
            // A missing pointer is never evidence of an empty lineage. Discovery proves the
            // unique immutable tip without repairing repository state; raw-authority-gated
            // maintenance/ops paths own durable pointer rebuild.
            current = await _repository.ArtifactSets
                .TryDiscoverLatestTipAsync(
                    _policy,
                    _scope,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        if (current is null) {
            return new(
                SessionContextCandidateDiscoveryStatus.EmptyLineage,
                Array.Empty<SessionContextCandidateDescriptor>()
            );
        }

        int requestedCount = request.Mode switch {
            SessionContextSelectionMode.Latest => 1,
            SessionContextSelectionMode.NthPrevious =>
                checked(request.NthPreviousOrdinal + 1),
            SessionContextSelectionMode.Budgeted =>
                request.MaxCandidateCount,
            _ => throw new ArgumentOutOfRangeException(
                nameof(request.Mode),
                request.Mode,
                "Unsupported context selection mode."
            )
        };
        var descriptors =
            new List<SessionContextCandidateDescriptor>(
                requestedCount
            );
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null
               && descriptors.Count < requestedCount) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current.SetId)) {
                throw new InvalidDataException(
                    "ArtifactSet candidate lineage contains a cycle."
                );
            }
            descriptors.Add(new(
                current.SetId,
                descriptors.Count,
                current.CommonAnchor,
                current.AnchorSetups
            ));
            current = current.PreviousSetId is { } previous
                ? await _repository.ArtifactSets.TryReadAsync(
                        previous,
                        _policy,
                        _scope,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"ArtifactSet candidate lineage references missing previous set '{previous}'."
                    )
                : null;
        }
        return new(
            SessionContextCandidateDiscoveryStatus.Candidates,
            descriptors.AsReadOnly()
        );
    }

    public async ValueTask<SessionContextCandidate> MaterializeAsync(
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);
        DerivedArtifactSet set = await _repository.ArtifactSets
            .TryReadAsync(
                descriptor.Handle,
                _policy,
                _scope,
                cancellationToken
            )
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Discovered ArtifactSet '{descriptor.Handle}' is no longer available."
            );
        if (set.CommonAnchor != descriptor.RawStartExclusive
            || set.AnchorSetups != descriptor.AnchorSetups) {
            throw new InvalidDataException(
                $"Discovered ArtifactSet '{descriptor.Handle}' changed before materialization."
            );
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
        return new(
            set.CommonAnchor,
            set.AnchorSetups,
            contributions.AsReadOnly()
        );
    }
}
