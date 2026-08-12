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
    [ThreadStatic]
    private static SessionJournalEngine? _threadDerivedSidecarOwner;
    private MutationOwnerToken? _activeMutationOwner;
    private readonly object _derivedSidecarGate = new();
    private int _activeDerivedSidecarMutations;
    private bool _derivedSidecarDisposePending;

    private MutationLease EnterMutation(string operation) {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var owner = new MutationOwnerToken(operation);
        MutationOwnerToken? active = Interlocked.CompareExchange(
            ref _activeMutationOwner,
            owner,
            comparand: null
        );
        if (active is not null) {
            throw new SessionJournalConcurrentMutationException(
                operation,
                active.Operation
            );
        }
        return new MutationLease(this, owner);
    }

    /// <summary>
    /// Holds the mutable SessionJournal owner across one repository-bound
    /// derived-sidecar publication. The opaque scope serializes with raw
    /// mutation and prevents owner disposal for the full publication window.
    /// </summary>
    public SessionJournalDerivedMutationScope EnterDerivedSidecarMutation(
        string operation
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (_threadDerivedSidecarOwner is not null) {
            throw new SessionJournalConcurrentMutationException(
                operation,
                "derived-sidecar-mutation");
        }
        lock (_derivedSidecarGate) {
            ObjectDisposedException.ThrowIf(
                _derivedSidecarDisposePending || _disposed,
                this);
            _activeDerivedSidecarMutations++;
        }
        MutationLease? lease = null;
        try {
            lease = EnterMutation(operation);
            ThrowIfReadOnlyMutation(operation);
            _threadDerivedSidecarOwner = this;
            return new SessionJournalDerivedMutationScope(
                this,
                lease,
                _readView);
        }
        catch {
            lease?.Dispose();
            ExitDerivedSidecarMutation();
            throw;
        }
    }

    private void BeginDerivedSidecarDispose() {
        if (ReferenceEquals(_threadDerivedSidecarOwner, this)) {
            throw new SessionJournalConcurrentMutationException(
                nameof(Dispose),
                "derived-sidecar-mutation");
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

    internal void ExitDerivedSidecarMutation() {
        if (ReferenceEquals(_threadDerivedSidecarOwner, this)) {
            _threadDerivedSidecarOwner = null;
        }
        lock (_derivedSidecarGate) {
            _activeDerivedSidecarMutations--;
            if (_activeDerivedSidecarMutations == 0) {
                Monitor.PulseAll(_derivedSidecarGate);
            }
        }
    }

    private void ExitMutation(MutationOwnerToken owner) {
        MutationOwnerToken? released = Interlocked.CompareExchange(
            ref _activeMutationOwner,
            value: null,
            comparand: owner
        );
        if (!ReferenceEquals(released, owner)) {
            throw new InvalidOperationException(
                "SessionJournalEngine mutation lease owner mismatch."
            );
        }
    }

    private sealed class MutationOwnerToken {
        internal MutationOwnerToken(string operation) {
            Operation = operation;
        }

        internal string Operation { get; }
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
                value: null
            );
            engine?.ExitMutation(_owner);
        }
    }
}

public sealed class SessionJournalDerivedMutationScope : IDisposable {
    private SessionJournalEngine? _owner;
    private IDisposable? _lease;

    internal SessionJournalDerivedMutationScope(
        SessionJournalEngine owner,
        IDisposable lease,
        SessionJournalReadView readView
    ) {
        _owner = owner;
        _lease = lease;
        ReadView = readView;
    }

    public SessionJournalReadView ReadView { get; }

    public void Dispose() {
        SessionJournalEngine? owner = Interlocked.Exchange(
            ref _owner, null);
        Interlocked.Exchange(ref _lease, null)?.Dispose();
        owner?.ExitDerivedSidecarMutation();
    }
}
