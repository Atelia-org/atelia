namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal enum RuntimeLanePriority {
    Leader,
    Follower
}

internal sealed class RuntimeLane {
    private readonly object _gate = new();
    private readonly Queue<Waiter> _leaders = [];
    private readonly Queue<Waiter> _followers = [];
    private int _available;

    internal RuntimeLane(int maximumConcurrency) {
        _available = maximumConcurrency;
    }

    internal ValueTask<Lease> AcquireAsync(
        RuntimeLanePriority priority,
        CancellationToken cancellationToken
    ) {
        lock (_gate) {
            if (cancellationToken.IsCancellationRequested) {
                return ValueTask.FromCanceled<Lease>(cancellationToken);
            }
            if (_available > 0
                && _leaders.Count == 0
                && _followers.Count == 0) {
                _available--;
                return ValueTask.FromResult(new Lease(this));
            }
            var waiter = new Waiter(this, cancellationToken);
            (priority is RuntimeLanePriority.Leader
                ? _leaders
                : _followers).Enqueue(waiter);
            waiter.RegisterCancellation();
            return new ValueTask<Lease>(waiter.Task);
        }
    }

    private void Release() {
        Waiter? next = null;
        lock (_gate) {
            while (TryDequeue(out next)) {
                if (next!.TryGrant(new Lease(this))) {
                    return;
                }
            }
            _available++;
        }
    }

    private bool TryDequeue(out Waiter? value) {
        if (_leaders.TryDequeue(out value)) { return true; }
        return _followers.TryDequeue(out value);
    }

    internal sealed class Lease : IDisposable {
        private RuntimeLane? _owner;

        internal Lease(RuntimeLane owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();

        internal void Abandon() => _owner = null;
    }

    private sealed class Waiter {
        private readonly RuntimeLane _owner;
        private readonly CancellationToken _token;
        private readonly TaskCompletionSource<Lease> _source = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private CancellationTokenRegistration _registration;

        internal Waiter(RuntimeLane owner, CancellationToken token) {
            _owner = owner;
            _token = token;
        }

        internal Task<Lease> Task => _source.Task;

        internal void RegisterCancellation() {
            _registration = _token.Register(static state => {
                var waiter = (Waiter)state!;
                waiter._source.TrySetCanceled(waiter._token);
            }, this);
        }

        internal bool TryGrant(Lease lease) {
            _registration.Dispose();
            if (_source.TrySetResult(lease)) { return true; }
            lease.Abandon();
            return false;
        }
    }
}
