using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>
/// AwaitingAgentAction recovery has no pending Observation argument. Stop may
/// switch to observer-only only after the recovery lifecycle has succeeded.
/// Prepared/Started recovery does not run this lifecycle and transitions at
/// its separate frozen-runtime binding fence.
/// </summary>
internal sealed class GalateaRecoveryLifecycleGate
    : ISessionContextLifecycleCoordinator {
    private readonly ISessionContextLifecycleCoordinator _inner;
    private readonly GalateaTurnStopController _stop;
    private int _transitioned;

    internal GalateaRecoveryLifecycleGate(
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
        SessionContextLifecycleResult result = await _inner
            .PrepareAsync(readView, request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status is (
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
