namespace Atelia.SessionJournal.Tests;

internal sealed class TestMemoryLifecycle
    : ISessionMemoryLifecycleCoordinator {
    private readonly List<SessionMemoryLifecycleRequest> _requests =
        [];

    internal SessionMemoryLifecycleResult Result { get; set; } =
        SessionMemoryLifecycleResult.Ready;

    internal Action<
        SessionJournalEngine,
        SessionMemoryLifecycleRequest
    >? OnPrepare { get; set; }

    internal IReadOnlyList<SessionMemoryLifecycleRequest> Requests =>
        _requests;

    internal int InvocationCount => _requests.Count;

    public ValueTask<SessionMemoryLifecycleResult> PrepareAsync(
        SessionJournalEngine engine,
        SessionMemoryLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        _requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        OnPrepare?.Invoke(engine, request);
        return ValueTask.FromResult(Result);
    }
}
