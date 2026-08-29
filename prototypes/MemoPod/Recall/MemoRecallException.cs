namespace Atelia.MemoPod;

public enum MemoRecallFailureKind {
    LocalLimitExceeded,
    InvalidModelOutput,
    ProviderFailure,
}

public sealed class MemoRecallException : Exception {
    internal MemoRecallException(
        MemoRecallFailureKind failureKind,
        string message,
        Exception? innerException = null
    ) : base(message, innerException) {
        FailureKind = failureKind;
    }

    public MemoRecallFailureKind FailureKind { get; }
}
