using Atelia.Agent.Core.History;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core.Persistence;

/// <summary>
/// 围绕 <see cref="AgentWorkspaceRoot"/> 做 snapshot materialize/project 的内部 helper。
/// 当前 repo-backed host / internal path 以 live durable workspace 为主；
/// 本类型不再充当第二个 root façade，保留的职责仅是 snapshot save/load 桥接，主要用于测试、diagnostic 和少量内部 compatibility/import-export。
/// 对外公开的 non-live restore surface 应显式停留在 <see cref="AgentEngineStateSnapshot"/> + `AgentEngine.CreateFromStateSnapshot(...)`。
/// </summary>
internal static class AgentEngineStateRoot {
    /// <summary>
    /// 把当前 <see cref="AgentEngine"/> 导出为 snapshot，再写回 workspace。
    /// 保留该入口主要是为了 compatibility/import-export；repo-backed live host 的主路径应优先直接做 workspace mutation。
    /// </summary>
    internal static void SaveSnapshot(AgentWorkspaceRoot workspaceRoot, AgentEngine engine) {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        ArgumentNullException.ThrowIfNull(engine);
        SaveSnapshot(workspaceRoot, engine.ExportStateSnapshot());
    }

    /// <summary>
    /// 把一个完整 snapshot 投影进 workspace root。
    /// 这是 compatibility adapter 入口，不表示 snapshot 是 runtime 的主真相。
    /// </summary>
    internal static void SaveSnapshot(AgentWorkspaceRoot workspaceRoot, AgentEngineStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        ArgumentNullException.ThrowIfNull(snapshot);

        workspaceRoot.Meta.Stamp();
        workspaceRoot.Meta.SetSystemPrompt(snapshot.AgentState.SystemPrompt);
        workspaceRoot.History.SetLastSerial(snapshot.AgentState.LastSerial);
        workspaceRoot.History.ReplaceRecent(snapshot.AgentState.RecentHistory);
        workspaceRoot.History.ReplacePendingNotifications(snapshot.AgentState.PendingNotifications);
        ReplaceRuntimeState(workspaceRoot, ToRuntimeStateSnapshot(snapshot));
    }

    /// <summary>
    /// 从当前 workspace materialize 一个 <see cref="AgentEngineStateSnapshot"/>。
    /// 公开的 non-live restore surface 应显式走 snapshot + `AgentEngine.CreateFromStateSnapshot(...)`；
    /// internal live host 则应直接围绕 workspaceRoot / session 恢复。
    /// </summary>
    internal static AgentEngineStateSnapshot LoadSnapshot(AgentWorkspaceRoot workspaceRoot) {
        ArgumentNullException.ThrowIfNull(workspaceRoot);

        string systemPrompt = workspaceRoot.Meta.GetRequiredSystemPrompt();
        ulong lastSerial = workspaceRoot.History.GetRequiredLastSerial();
        var recentHistory = workspaceRoot.History.LoadRecent();
        var pendingNotifications = workspaceRoot.History.LoadPendingNotifications();
        var runtimeState = LoadRuntimeState(workspaceRoot);

        return new AgentEngineStateSnapshot(
            AgentState: new AgentStateSnapshot(
                SystemPrompt: systemPrompt,
                RecentHistory: recentHistory,
                PendingNotifications: pendingNotifications,
                LastSerial: lastSerial
            ),
            PendingToolResults: runtimeState.PendingToolResults,
            ResolvedProfile: runtimeState.ResolvedProfile,
            LockedCompactionSplitIndex: runtimeState.LockedCompactionSplitIndex,
            PendingCompaction: runtimeState.PendingCompaction,
            ToolSessionExecutionSequence: runtimeState.ToolSessionExecutionSequence
        );
    }

    private static void ReplaceRuntimeState(AgentWorkspaceRoot workspaceRoot, AgentEngineRuntimeStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        workspaceRoot.Meta.Stamp();
        workspaceRoot.RuntimeState.SetToolSessionExecutionSequence(snapshot.ToolSessionExecutionSequence);
        workspaceRoot.RuntimeState.ReplacePendingToolResults(snapshot.PendingToolResults);
        workspaceRoot.RuntimeState.ReplaceTurnRuntime(snapshot.ResolvedProfile, snapshot.LockedCompactionSplitIndex);
        workspaceRoot.RuntimeState.ReplacePendingCompaction(snapshot.PendingCompaction);
    }

    private static AgentEngineRuntimeStateSnapshot LoadRuntimeState(AgentWorkspaceRoot workspaceRoot) {
        var pendingToolResults = workspaceRoot.RuntimeState.LoadPendingToolResults();
        var (resolvedProfile, lockedCompactionSplitIndex) = workspaceRoot.RuntimeState.LoadTurnRuntime();
        var pendingCompaction = workspaceRoot.RuntimeState.LoadPendingCompaction();
        var toolSessionExecutionSequence = workspaceRoot.RuntimeState.GetToolSessionExecutionSequenceOrDefault();

        return new AgentEngineRuntimeStateSnapshot(
            PendingToolResults: pendingToolResults,
            ResolvedProfile: resolvedProfile,
            LockedCompactionSplitIndex: lockedCompactionSplitIndex,
            PendingCompaction: pendingCompaction,
            ToolSessionExecutionSequence: toolSessionExecutionSequence
        );
    }

    private static AgentEngineRuntimeStateSnapshot ToRuntimeStateSnapshot(AgentEngineStateSnapshot snapshot) {
        return new AgentEngineRuntimeStateSnapshot(
            PendingToolResults: snapshot.PendingToolResults,
            ResolvedProfile: snapshot.ResolvedProfile,
            LockedCompactionSplitIndex: snapshot.LockedCompactionSplitIndex,
            PendingCompaction: snapshot.PendingCompaction,
            ToolSessionExecutionSequence: snapshot.ToolSessionExecutionSequence
        );
    }
}
