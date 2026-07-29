namespace Atelia.SessionJournal.DerivedMemory;

/// <summary>
/// Exact two-phase provider over one ArtifactSet lineage. Selection reads set metadata
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

    public async ValueTask<SessionContextCandidateSelection> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateShape();

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
                SessionContextCandidateSelectionStatus.EmptyLineage,
                null
            );
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int ordinal = 0; ; ordinal++) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current.SetId)) {
                throw new InvalidDataException(
                    "ArtifactSet candidate lineage contains a cycle."
                );
            }
            if (ordinal == request.NthPrevious) {
                return new(
                    SessionContextCandidateSelectionStatus.Selected,
                    new SessionContextCandidateDescriptor(
                        current.SetId,
                        current.SetId,
                        current.CommonAnchor,
                        current.AnchorSetups
                    )
                );
            }
            if (current.PreviousSetId is not { } previous) {
                return new(
                    SessionContextCandidateSelectionStatus.OrdinalUnavailable,
                    null
                );
            }
            current = await _repository.ArtifactSets.TryReadAsync(
                    previous,
                    _policy,
                    _scope,
                    cancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"ArtifactSet candidate lineage references missing previous set '{previous}'."
                );
        }
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
        if (!string.Equals(
                set.SetId,
                descriptor.SnapshotToken,
                StringComparison.Ordinal
            )
            || set.CommonAnchor != descriptor.SetAdmissionAnchor
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
            if (!artifact.ContextHeaderPack.TryGetBlock(
                    artifact.Target,
                    out ContextHeaderBlock block
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
                member.AbsorbedThrough
            ));
        }
        return new(
            set.CommonAnchor,
            set.AnchorSetups,
            contributions.AsReadOnly()
        );
    }
}
