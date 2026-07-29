namespace Atelia.SessionJournal.Tests;

internal sealed class TestContextLifecycle
    : ISessionContextLifecycleCoordinator {
    private readonly List<SessionContextLifecycleRequest> _requests =
        [];

    internal SessionContextLifecycleResult Result { get; set; } =
        SessionContextLifecycleResult.Ready;

    internal Action<
        SessionJournalEngine,
        SessionContextLifecycleRequest
    >? OnPrepare { get; set; }

    internal IReadOnlyList<SessionContextLifecycleRequest> Requests =>
        _requests;

    internal int InvocationCount => _requests.Count;

    public ValueTask<SessionContextLifecycleResult> PrepareAsync(
        SessionJournalEngine engine,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        _requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        OnPrepare?.Invoke(engine, request);
        return ValueTask.FromResult(Result);
    }
}
