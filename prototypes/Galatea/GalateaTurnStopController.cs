using Atelia.Completion.Abstractions;

namespace Atelia.Galatea.Server;

internal enum GalateaTurnStopPhase {
    PreDispatch = 0,
    ObserverOnly = 1,
    Completed = 2
}

/// <summary>
/// Linearizes user stop against the transition from cancellable preparation
/// to observer-only provider dispatch for one live turn.
/// </summary>
internal sealed class GalateaTurnStopController {
    private readonly object _gate = new();
    private readonly CancellationTokenSource _preDispatchStop = new();
    private bool _stopRequested;
    private GalateaTurnStopPhase _phase =
        GalateaTurnStopPhase.PreDispatch;

    internal GalateaTurnStopController() {
        Observer = new CompletionStreamObserver();
    }

    internal CompletionStreamObserver Observer { get; }

    internal CancellationToken PreDispatchStopToken =>
        _preDispatchStop.Token;

    internal bool StopRequested {
        get {
            lock (_gate) {
                return _stopRequested;
            }
        }
    }

    internal GalateaTurnStopPhase Phase {
        get {
            lock (_gate) {
                return _phase;
            }
        }
    }

    internal bool RequestStop() {
        bool cancelPreDispatch;
        lock (_gate) {
            if (_phase == GalateaTurnStopPhase.Completed) {
                return false;
            }

            _stopRequested = true;
            cancelPreDispatch =
                _phase == GalateaTurnStopPhase.PreDispatch;
            if (!cancelPreDispatch) {
                Observer.ShouldStop = true;
            }
        }
        if (cancelPreDispatch) {
            _preDispatchStop.Cancel();
        }
        return true;
    }

    internal void EnterObserverOnlyOrThrow(
        CancellationToken cancellationToken
    ) {
        lock (_gate) {
            cancellationToken.ThrowIfCancellationRequested();
            if (_phase == GalateaTurnStopPhase.Completed) {
                throw new InvalidOperationException(
                    "A completed Galatea turn cannot enter dispatch."
                );
            }
            if (_phase == GalateaTurnStopPhase.ObserverOnly) {
                return;
            }
            if (_stopRequested
                || _preDispatchStop.IsCancellationRequested) {
                throw new OperationCanceledException(
                    "The Galatea turn was stopped before dispatch.",
                    innerException: null,
                    token: _preDispatchStop.Token
                );
            }
            _phase = GalateaTurnStopPhase.ObserverOnly;
        }
    }

    internal void Complete() {
        lock (_gate) {
            _phase = GalateaTurnStopPhase.Completed;
        }
    }
}
