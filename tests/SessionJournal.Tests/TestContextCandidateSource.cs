namespace Atelia.SessionJournal.Tests;

/// <summary>Small fake provider shared by online tests; it never reads the live journal.</summary>
internal sealed class TestContextCandidateSource : ICoherentContextCandidateSource {
    private readonly List<SessionContextSelectionRequest> _requests = [];
    private readonly List<CancellationToken> _cancellationTokens = [];
    private readonly List<string> _materializedHandles = [];
    private int _materializationCount;

    internal TestContextCandidateSource(
        SessionContextCandidate? candidate = null
    ) {
        Candidate = candidate;
    }

    internal SessionContextCandidate? Candidate { get; set; }
    internal IReadOnlyList<SessionContextCandidate>? Candidates {
        get;
        set;
    }
    internal bool IsEmptyLineage { get; set; }

    internal IReadOnlyList<SessionContextSelectionRequest> Requests
        => _requests;

    internal IReadOnlyList<CancellationToken> CancellationTokens
        => _cancellationTokens;

    internal int SelectionCount => _requests.Count;
    internal int MaterializationCount => _materializationCount;
    internal IReadOnlyList<string> MaterializedHandles =>
        _materializedHandles;

    public ValueTask<SessionContextCandidateSelection> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) {
        _requests.Add(request);
        _cancellationTokens.Add(cancellationToken);
        request.ValidateShape();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SessionContextCandidate> candidates =
            Candidates
            ?? (Candidate is null
                ? Array.Empty<SessionContextCandidate>()
                : new[] { Candidate });
        if (candidates.Count == 0) {
            return ValueTask.FromResult(new SessionContextCandidateSelection(
                IsEmptyLineage
                    ? SessionContextCandidateSelectionStatus.EmptyLineage
                    : SessionContextCandidateSelectionStatus.OrdinalUnavailable,
                null
            ));
        }
        if (request.NthPrevious >= candidates.Count) {
            return ValueTask.FromResult(new SessionContextCandidateSelection(
                SessionContextCandidateSelectionStatus.OrdinalUnavailable,
                null
            ));
        }
        SessionContextCandidate candidate = candidates[request.NthPrevious];
        return ValueTask.FromResult(new SessionContextCandidateSelection(
            SessionContextCandidateSelectionStatus.Selected,
            new SessionContextCandidateDescriptor(
                $"test-candidate-{request.NthPrevious}",
                candidate.RawStartExclusive,
                candidate.AnchorSetups
            )
        ));
    }

    public ValueTask<SessionContextCandidate> MaterializeAsync(
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        _materializationCount++;
        _materializedHandles.Add(descriptor.Handle);
        IReadOnlyList<SessionContextCandidate> candidates =
            Candidates
            ?? (Candidate is null
                ? Array.Empty<SessionContextCandidate>()
                : new[] { Candidate });
        const string prefix = "test-candidate-";
        if (!descriptor.Handle.StartsWith(
                prefix,
                StringComparison.Ordinal
            )
            || !int.TryParse(
                descriptor.Handle.AsSpan(prefix.Length),
                out int index
            )
            || index < 0
            || index >= candidates.Count) {
            throw new InvalidDataException(
                "The requested test context candidate is unavailable."
            );
        }
        return ValueTask.FromResult(candidates[index]);
    }
}
