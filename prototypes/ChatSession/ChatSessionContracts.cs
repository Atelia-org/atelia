using Atelia.Completion.Abstractions;

namespace Atelia.ChatSession;

public sealed record ChatSessionTurnResult(
    ActionMessage Message,
    CompletionDescriptor Invocation,
    IReadOnlyList<string>? Errors,
    int ToolCallsExecuted
);

public sealed class ChatSessionTurnAbortedException : Exception {
    public ChatSessionTurnAbortedException(
        string message,
        CompletionTermination termination,
        IReadOnlyList<string>? errors = null
    ) : base(message) {
        Termination = termination ?? throw new ArgumentNullException(nameof(termination));
        Errors = errors;
    }

    public CompletionTermination Termination { get; }

    public IReadOnlyList<string>? Errors { get; }
}

public sealed record RecapMessage(string? Content) : ObservationMessage(Content);

public sealed record CompactionResult(
    bool Applied,
    CompactionFailureReason? FailureReason,
    int SplitIndex,
    int SummaryLength,
    int HistoryCountBefore,
    int HistoryCountAfter,
    ulong TokensBefore,
    ulong TokensAfter
);

public enum CompactionFailureReason {
    NoValidSplitPoint,
    InvalidSplitPoint,
    EmptySummary
}

public sealed record ChatSessionStatistics(
    int MessageCount,
    int ObservationCount,
    int ActionCount,
    int ToolResultsCount,
    int RecapCount,
    ulong EstimatedTokens
);

public sealed record RemovedChatTurnResult(
    string UserMessage,
    int RemovedMessageCount
);
