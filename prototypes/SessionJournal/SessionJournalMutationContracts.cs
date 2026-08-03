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
