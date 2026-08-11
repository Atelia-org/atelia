namespace Atelia.SessionJournal.RecapGrid.Manager;

internal sealed class ManagerLifetime : IDisposable {
    private readonly object _gate = new();
    private readonly IDisposable[] _owned;
    private readonly AsyncLocal<int> _operationDepth = new();
    private bool _closing;
    private bool _disposeClaimed;
    private bool _complete;
    private int _operations;

    internal ManagerLifetime(params IDisposable[] owned) {
        _owned = owned;
    }

    internal Operation? TryEnter() {
        lock (_gate) {
            if (_closing) {
                return null;
            }
            _operations = checked(_operations + 1);
            _operationDepth.Value = checked(
                _operationDepth.Value + 1
            );
            return new Operation(this);
        }
    }

    public void Dispose() {
        bool disposeOwned = false;
        lock (_gate) {
            if (_complete) {
                return;
            }
            _closing = true;
            if (_operationDepth.Value > 0) {
                return;
            }
            while (_operations != 0 || _disposeClaimed) {
                Monitor.Wait(_gate);
                if (_complete) {
                    return;
                }
            }
            _disposeClaimed = true;
            disposeOwned = true;
        }
        if (disposeOwned) {
            DisposeOwned();
        }
    }

    private void Exit() {
        bool disposeOwned = false;
        if (_operationDepth.Value <= 0) {
            throw new InvalidOperationException(
                "Manager operation ownership is unbalanced."
            );
        }
        _operationDepth.Value--;
        lock (_gate) {
            _operations--;
            if (_operations == 0) {
                Monitor.PulseAll(_gate);
                if (_closing && !_disposeClaimed) {
                    _disposeClaimed = true;
                    disposeOwned = true;
                }
            }
        }
        if (disposeOwned) {
            DisposeOwned();
        }
    }

    private void DisposeOwned() {
        try {
            foreach (IDisposable owned in _owned) {
                owned.Dispose();
            }
        }
        finally {
            lock (_gate) {
                _complete = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    internal sealed class Operation : IDisposable {
        private ManagerLifetime? _owner;

        internal Operation(ManagerLifetime owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(
            ref _owner,
            null
        )?.Exit();
    }
}
