namespace Atelia.SessionJournal.Tests;

/// <summary>Small fake provider shared by online tests; it never reads the live journal.</summary>
internal sealed class TestContextCandidateSource : ICoherentContextCandidateSource {
    private readonly List<SessionContextSelectionRequest> _requests = [];
    private readonly List<CancellationToken> _cancellationTokens = [];

    internal TestContextCandidateSource(
        SessionContextCandidate? candidate = null
    ) {
        Candidate = candidate;
    }

    internal SessionContextCandidate? Candidate { get; set; }

    internal IReadOnlyList<SessionContextSelectionRequest> Requests
        => _requests;

    internal IReadOnlyList<CancellationToken> CancellationTokens
        => _cancellationTokens;

    internal int SelectionCount => _requests.Count;

    public ValueTask<SessionContextCandidate?> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) {
        _requests.Add(request);
        _cancellationTokens.Add(cancellationToken);
        request.ValidateShape();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Candidate);
    }
}
