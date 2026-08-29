namespace Atelia.MemoPod;

public enum MemoPodPersistenceFailureKind {
    NotFound,
    AlreadyExists,
    InvalidDocument,
    UnsafePath,
    IoFailure,
    CommitIndeterminate,
}

public class MemoPodPersistenceException : IOException {
    internal MemoPodPersistenceException(
        MemoPodPersistenceFailureKind failureKind,
        string message,
        Exception? innerException = null
    ) : base(message, innerException) {
        FailureKind = failureKind;
    }

    public MemoPodPersistenceFailureKind FailureKind { get; }
}

public sealed class MemoPodCommitIndeterminateException
    : MemoPodPersistenceException {
    internal MemoPodCommitIndeterminateException(
        string message,
        Exception? innerException = null
    ) : base(
        MemoPodPersistenceFailureKind.CommitIndeterminate,
        message,
        innerException
    ) { }
}

public sealed class MemoPodInvalidatedException : InvalidOperationException {
    internal MemoPodInvalidatedException()
        : base(
            "The MemoPod handle was invalidated by an indeterminate durable commit. Discard it and call MemoPod.Open to observe durable authority."
        ) { }
}
