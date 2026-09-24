namespace Atelia.MemoPod;

public enum MemoRecallFailureKind {
    LocalLimitExceeded,
    InvalidModelOutput,
    ProviderFailure,
}

public enum MemoRecallOutputFailureCode {
    UnexpectedBlock,
    TextBlock,
    MultipleToolCalls,
    NullToolCall,
    NullBlock,
    MissingToolCall,
    WrongToolName,
    InvalidToolCallId,
    MissingArguments,
    InvalidArgumentsEncoding,
    ArgumentsTooLarge,
    InvalidArgumentsJson,
    InvalidArgumentsShape,
    TooManyIds,
    InvalidMemoId,
    DuplicateMemoId,
    UnknownMemoId,
}

public sealed class MemoRecallException : Exception {
    internal MemoRecallException(
        MemoRecallFailureKind failureKind,
        string message,
        Exception? innerException = null,
        MemoRecallOutputFailureCode? outputFailureCode = null
    ) : base(message, innerException) {
        FailureKind = failureKind;
        OutputFailureCode = outputFailureCode;
    }

    public MemoRecallFailureKind FailureKind { get; }
    public MemoRecallOutputFailureCode? OutputFailureCode { get; }
}
