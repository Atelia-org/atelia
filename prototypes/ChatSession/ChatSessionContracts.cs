using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

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

public sealed record ContextHeader(
    string? SystemPromptFragment,
    string? ObservationMessage,
    ActionMessage? ActionMessage
) : IHistoryMessage {
    public HistoryMessageKind Kind => HistoryMessageKind.ContextHeader;
}

public interface IMemoryMaintainerAgent {
    string Id { get; }
    string TargetBlockKey { get; }
    string SystemPrompt { get; }
    string UserPrompt { get; }
    ToolSession ToolSession { get; }
}

public sealed record MemoryMaintenanceRequest(
    IReadOnlyList<IMemoryMaintainerAgent> Maintainers,
    bool AllowActionToObservationBoundary = true
);

public sealed record MemoryMaintenanceResult(
    bool Completed,
    CompactionFailureReason? FailureReason,
    int SplitIndex,
    IReadOnlyList<MemoryMaintainerResult> MaintainerResults,
    int HistoryCountBefore,
    ulong TokensBefore
);

public sealed record MemoryMaintainerResult(
    string MaintainerId,
    string TargetBlockKey,
    string UpdatedText,
    CompletionDescriptor Invocation,
    IReadOnlyList<string>? Errors,
    int ToolCallsExecuted
);

public sealed record RecapSourceAnchor(
    string SourceHeadBeforeCompaction,
    string SourceBranchName,
    int SourceStartIndex,
    int SourceEndExclusive,
    int SourceMessageCountBefore,
    string CompactionKind
);

public sealed record RecapMessage(
    string? Content,
    RecapSourceAnchor? SourceAnchor = null
) : ObservationMessage(Content);

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
