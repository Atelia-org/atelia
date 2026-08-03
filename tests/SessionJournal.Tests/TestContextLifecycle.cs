namespace Atelia.SessionJournal.Tests;

internal sealed class TestContextLifecycle
    : ISessionContextLifecycleCoordinator {
    private readonly List<SessionContextLifecycleRequest> _requests =
        [];

    internal SessionContextLifecycleResult Result { get; set; } =
        SessionContextLifecycleResult.Ready;

    internal Action<
        SessionJournalReadView,
        SessionContextLifecycleRequest
    >? OnPrepare { get; set; }

    internal IReadOnlyList<SessionContextLifecycleRequest> Requests =>
        _requests;

    internal int InvocationCount => _requests.Count;

    public ValueTask<SessionContextLifecycleResult> PrepareAsync(
        SessionJournalReadView readView,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(readView);
        _requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        OnPrepare?.Invoke(readView, request);
        return ValueTask.FromResult(Result);
    }
}
