using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.Agent.Core.Persistence;

/// <summary>
/// <see cref="AgentState"/> 的可持久化快照。
/// </summary>
public sealed record AgentStateSnapshot(
    string SystemPrompt,
    IReadOnlyList<HistoryEntry> RecentHistory,
    IReadOnlyList<string> PendingNotifications,
    ulong LastSerial
);

/// <summary>
/// 用于在 durable state 中记录一个可重建的 <see cref="LlmProfile"/> 身份。
/// </summary>
public sealed record LlmProfileCheckpoint(
    string ProviderId,
    string ApiSpecId,
    string ModelId,
    string Name,
    uint SoftContextTokenCap
) {
    public CompletionDescriptor ToCompletionDescriptor() => new(ProviderId, ApiSpecId, ModelId);

    public static LlmProfileCheckpoint FromProfile(LlmProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        return new LlmProfileCheckpoint(
            ProviderId: profile.Client.Name,
            ApiSpecId: profile.Client.ApiSpecId,
            ModelId: profile.ModelId,
            Name: profile.Name,
            SoftContextTokenCap: profile.SoftContextTokenCap
        );
    }
}

/// <summary>
/// 记录一次待恢复的 compaction 请求。
/// </summary>
public sealed record CompactionCheckpoint(
    int SplitIndex,
    string SystemPrompt,
    string SummarizePrompt
);

/// <summary>
/// AgentEngine 中仍通过 snapshot adapter 读写的 runtime-only 可持久化状态。
/// </summary>
internal sealed record AgentEngineRuntimeStateSnapshot(
    IReadOnlyList<ToolCallExecutionResult> PendingToolResults,
    LlmProfileCheckpoint? ResolvedProfile,
    int? LockedCompactionSplitIndex,
    CompactionCheckpoint? PendingCompaction,
    long ToolSessionExecutionSequence
);

/// <summary>
/// AgentEngine 的完整可持久化快照。
/// </summary>
public sealed record AgentEngineStateSnapshot(
    AgentStateSnapshot AgentState,
    IReadOnlyList<ToolCallExecutionResult> PendingToolResults,
    LlmProfileCheckpoint? ResolvedProfile,
    int? LockedCompactionSplitIndex,
    CompactionCheckpoint? PendingCompaction,
    long ToolSessionExecutionSequence
);
