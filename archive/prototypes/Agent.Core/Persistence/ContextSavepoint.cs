using Atelia.Agent.Core;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.Agent.Core.Persistence;

/// <summary>
/// 表示一次可 durable 化的运行态 checkpoint。
/// </summary>
internal sealed record RuntimeCheckpoint {
    public RuntimeCheckpoint(
        IReadOnlyList<ToolCallExecutionResult> pendingToolResults,
        LlmProfileCheckpoint? resolvedProfile,
        int? lockedCompactionSplitIndex,
        CompactionCheckpoint? pendingCompaction,
        long toolSessionExecutionSequence
    ) {
        PendingToolResults = pendingToolResults ?? throw new ArgumentNullException(nameof(pendingToolResults));
        ResolvedProfile = resolvedProfile;
        LockedCompactionSplitIndex = lockedCompactionSplitIndex;
        PendingCompaction = pendingCompaction;
        ToolSessionExecutionSequence = toolSessionExecutionSequence;

        if (ToolSessionExecutionSequence < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(ToolSessionExecutionSequence),
                ToolSessionExecutionSequence,
                "Tool session execution sequence must be greater than or equal to zero."
            );
        }
    }

    public IReadOnlyList<ToolCallExecutionResult> PendingToolResults { get; }

    public LlmProfileCheckpoint? ResolvedProfile { get; }

    public int? LockedCompactionSplitIndex { get; }

    public CompactionCheckpoint? PendingCompaction { get; }

    public long ToolSessionExecutionSequence { get; }
}

/// <summary>
/// 表示一次 route-neutral 的 durable context savepoint。
/// </summary>
internal sealed record ContextSavepoint {
    public ContextSavepoint(
        ulong? anchorEntrySerial,
        int anchorHistoryCount,
        AgentRunState entryState,
        CompletionDescriptor? turnLock,
        bool pendingCompactionSuppressed,
        RuntimeCheckpoint runtimeCheckpoint
    ) {
        AnchorEntrySerial = anchorEntrySerial;
        AnchorHistoryCount = anchorHistoryCount;
        EntryState = entryState;
        TurnLock = turnLock;
        PendingCompactionSuppressed = pendingCompactionSuppressed;
        RuntimeCheckpoint = runtimeCheckpoint ?? throw new ArgumentNullException(nameof(runtimeCheckpoint));

        if (AnchorHistoryCount < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(AnchorHistoryCount),
                AnchorHistoryCount,
                "Anchor history count must be greater than or equal to zero."
            );
        }

        if (AnchorHistoryCount == 0 && AnchorEntrySerial.HasValue) {
            throw new ArgumentException(
                "Anchor entry serial must be null when anchor history count is zero.",
                nameof(anchorEntrySerial)
            );
        }

        if (AnchorHistoryCount > 0 && !AnchorEntrySerial.HasValue) {
            throw new ArgumentException(
                "Anchor entry serial is required when anchor history count is greater than zero.",
                nameof(anchorEntrySerial)
            );
        }
    }

    public ulong? AnchorEntrySerial { get; }

    public int AnchorHistoryCount { get; }

    public AgentRunState EntryState { get; }

    public CompletionDescriptor? TurnLock { get; }

    public bool PendingCompactionSuppressed { get; }

    public RuntimeCheckpoint RuntimeCheckpoint { get; }
}
