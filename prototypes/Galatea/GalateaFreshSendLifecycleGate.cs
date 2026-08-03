using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>
/// Switches user stop to observer-only after a fresh SendAsync operation's
/// successful pre-observation lifecycle pass. Recovery operations require
/// mode-specific transition points and must not reuse this decorator.
/// </summary>
internal sealed class GalateaFreshSendLifecycleGate
    : ISessionContextLifecycleCoordinator {
    private readonly ISessionContextLifecycleCoordinator _inner;
    private readonly GalateaTurnStopController _stop;
    private int _transitioned;

    internal GalateaFreshSendLifecycleGate(
        ISessionContextLifecycleCoordinator inner,
        GalateaTurnStopController stop
    ) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
    }

    public async ValueTask<SessionContextLifecycleResult> PrepareAsync(
        SessionJournalReadView readView,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(readView);
        ArgumentNullException.ThrowIfNull(request);

        SessionContextLifecycleResult result = await _inner
            .PrepareAsync(readView, request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(result);

        if (request.PendingObservation is not null
            && result.Status is (
                SessionContextLifecycleStatus.Ready
                or SessionContextLifecycleStatus.RawHistoryAuthorized
            )
            && Interlocked.CompareExchange(
                ref _transitioned,
                1,
                0
            ) == 0) {
            _stop.EnterObserverOnlyOrThrow(cancellationToken);
        }
        return result;
    }
}
