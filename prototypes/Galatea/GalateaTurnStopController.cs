using Atelia.Completion.Abstractions;

namespace Atelia.Galatea.Server;

internal enum GalateaTurnStopPhase {
    PreDispatch = 0,
    Dispatched = 1,
    Completed = 2
}

/// <summary>
/// Linearizes user stop against the transition from cancellable preparation
/// to generation-only cancellation after provider dispatch for one live turn.
/// </summary>
internal sealed class GalateaTurnStopController {
    private readonly object _gate = new();
    private readonly CancellationTokenSource _preDispatchStop = new();
    private readonly CancellationTokenSource _userStop = new();
    private bool _stopRequested;
    private GalateaTurnStopPhase _phase =
        GalateaTurnStopPhase.PreDispatch;

    internal GalateaTurnStopController() {
        Observer = new CompletionStreamObserver();
    }

    internal CompletionStreamObserver Observer { get; }

    internal CancellationToken PreDispatchStopToken =>
        _preDispatchStop.Token;

    // Never pass this token to tool execution: it cancels generation only.
    internal CancellationToken UserStopToken => _userStop.Token;

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
        }
        _userStop.Cancel();
        if (cancelPreDispatch) {
            _preDispatchStop.Cancel();
        }
        return true;
    }

    internal void EnterDispatchOrThrow(
        CancellationToken cancellationToken
    ) {
        lock (_gate) {
            cancellationToken.ThrowIfCancellationRequested();
            if (_phase == GalateaTurnStopPhase.Completed) {
                throw new InvalidOperationException(
                    "A completed Galatea turn cannot enter dispatch."
                );
            }
            if (_phase == GalateaTurnStopPhase.Dispatched) {
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
            _phase = GalateaTurnStopPhase.Dispatched;
        }
    }

    internal void ProtectCommittedTools() {
        lock (_gate) {
            if (_phase == GalateaTurnStopPhase.Completed) {
                throw new InvalidOperationException("A completed turn cannot continue tools.");
            }
            _phase = GalateaTurnStopPhase.Dispatched;
        }
    }

    internal void Complete() {
        lock (_gate) {
            _phase = GalateaTurnStopPhase.Completed;
        }
    }
}
