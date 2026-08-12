namespace Atelia.SessionJournal;

/// <summary>
/// Indicates that a mutation was attempted while another mutation operation
/// still owned the same <see cref="SessionJournalEngine"/> instance.
/// </summary>
public sealed class SessionJournalConcurrentMutationException
    : InvalidOperationException {
    public SessionJournalConcurrentMutationException(
        string attemptedOperation,
        string activeOperation
    ) : base(
        "SessionJournalEngine mutation operation "
        + $"'{attemptedOperation}' cannot start while "
        + $"'{activeOperation}' is active on the same engine instance."
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptedOperation);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeOperation);
        AttemptedOperation = attemptedOperation;
        ActiveOperation = activeOperation;
    }

    public string AttemptedOperation { get; }
    public string ActiveOperation { get; }
}

public sealed partial class SessionJournalEngine {
    private MutationOwnerToken? _activeMutationOwner;
    private readonly object _derivedSidecarGate = new();
    private int _activeDerivedSidecarMutations;
    private bool _derivedSidecarDisposePending;

    private MutationLease EnterMutation(string operation) {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var owner = new MutationOwnerToken(
            operation,
            Environment.CurrentManagedThreadId);
        MutationOwnerToken? active = Interlocked.CompareExchange(
            ref _activeMutationOwner,
            owner,
            comparand: null);
        if (active is not null) {
            throw new SessionJournalConcurrentMutationException(
                operation,
                active.Operation);
        }
        return new MutationLease(this, owner);
    }

    /// <summary>
    /// Executes one synchronous repository-bound sidecar publication while
    /// holding the mutable SessionJournal owner. The callback cannot escape
    /// the mutation lease or survive owner disposal.
    /// </summary>
    internal T ExecuteDerivedSidecarMutation<T>(
        string operation,
        Func<SessionJournalReadView, T> callback
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(callback);
        lock (_derivedSidecarGate) {
            ObjectDisposedException.ThrowIf(
                _derivedSidecarDisposePending || _disposed,
                this);
            _activeDerivedSidecarMutations++;
        }
        try {
            using MutationLease lease = EnterMutation(operation);
            ThrowIfReadOnlyMutation(operation);
            return callback(_readView);
        }
        finally {
            lock (_derivedSidecarGate) {
                _activeDerivedSidecarMutations--;
                if (_activeDerivedSidecarMutations == 0) {
                    Monitor.PulseAll(_derivedSidecarGate);
                }
            }
        }
    }

    private void BeginDerivedSidecarDispose() {
        MutationOwnerToken? active = Volatile.Read(
            ref _activeMutationOwner);
        if (active is not null
            && active.ThreadId == Environment.CurrentManagedThreadId) {
            throw new SessionJournalConcurrentMutationException(
                nameof(Dispose),
                active.Operation);
        }
        lock (_derivedSidecarGate) {
            _derivedSidecarDisposePending = true;
            while (_activeDerivedSidecarMutations != 0) {
                Monitor.Wait(_derivedSidecarGate);
            }
        }
    }

    private void CancelDerivedSidecarDispose() {
        lock (_derivedSidecarGate) {
            if (!_disposed) {
                _derivedSidecarDisposePending = false;
                Monitor.PulseAll(_derivedSidecarGate);
            }
        }
    }

    private void ExitMutation(MutationOwnerToken owner) {
        MutationOwnerToken? released = Interlocked.CompareExchange(
            ref _activeMutationOwner,
            value: null,
            comparand: owner);
        if (!ReferenceEquals(released, owner)) {
            throw new InvalidOperationException(
                "SessionJournalEngine mutation lease owner mismatch.");
        }
    }

    private sealed class MutationOwnerToken(
        string operation,
        int threadId
    ) {
        internal string Operation { get; } = operation;
        internal int ThreadId { get; } = threadId;
    }

    private sealed class MutationLease : IDisposable {
        private SessionJournalEngine? _engine;
        private readonly MutationOwnerToken _owner;

        internal MutationLease(
            SessionJournalEngine engine,
            MutationOwnerToken owner
        ) {
            _engine = engine;
            _owner = owner;
        }

        public void Dispose() {
            SessionJournalEngine? engine = Interlocked.Exchange(
                ref _engine,
                value: null);
            engine?.ExitMutation(_owner);
        }
    }
}
