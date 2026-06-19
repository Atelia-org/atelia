using Atelia.Agent.Core.History;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core.Persistence;

/// <summary>
/// 以 <see cref="DurableDict{TKey}"/> 为 graph root 的 AgentEngine workspace adapter。
/// 当前 repo-backed host / internal path 以 live durable workspace 为主；
/// 本类型仍保留 snapshot save/load 入口，主要用于 compatibility、diagnostic 和 import/export。
/// </summary>
public sealed class AgentEngineStateRoot {
    private readonly AgentWorkspaceRoot _workspaceRoot;

    private AgentEngineStateRoot(AgentWorkspaceRoot workspaceRoot) {
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
    }

    public DurableDict<string> Root => _workspaceRoot.Root;

    public Revision Revision => _workspaceRoot.Revision;

    internal AgentWorkspaceRoot WorkspaceRoot => _workspaceRoot;

    public static AgentEngineStateRoot Create(Revision revision, string? systemPrompt = null) {
        ArgumentNullException.ThrowIfNull(revision);

        return new AgentEngineStateRoot(AgentWorkspaceRoot.Create(revision, systemPrompt));
    }

    public static AgentEngineStateRoot Create(Revision revision, AgentEngine engine) {
        ArgumentNullException.ThrowIfNull(engine);

        var stateRoot = Create(revision, engine.SystemPrompt);
        stateRoot.Save(engine);
        return stateRoot;
    }

    /// <summary>
    /// 从已有 graph root 包装出 workspace adapter。
    /// public 调用方若随后走 <see cref="Load"/>，得到的是 snapshot compatibility 视图，而不是绑定 live host 的运行时会话。
    /// </summary>
    public static AgentEngineStateRoot FromRoot(DurableDict<string> root) => FromWorkspaceRoot(AgentWorkspaceRoot.FromRoot(root));

    internal static AgentEngineStateRoot FromWorkspaceRoot(AgentWorkspaceRoot workspaceRoot) => new(workspaceRoot);

    /// <summary>
    /// 把当前 <see cref="AgentEngine"/> 导出为 snapshot，再写回 workspace。
    /// 保留该入口主要是为了 compatibility/import-export；repo-backed live host 的主路径应优先直接做 workspace mutation。
    /// </summary>
    public void Save(AgentEngine engine) {
        ArgumentNullException.ThrowIfNull(engine);
        Save(engine.ExportStateSnapshot());
    }

    public void SaveAndCommit(Repository repo, AgentEngine engine) {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(engine);

        Save(engine);
        repo.Commit(Root).Unwrap();
    }

    public void SaveAndCommit(Repository repo, AgentEngineStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(snapshot);

        Save(snapshot);
        repo.Commit(Root).Unwrap();
    }

    /// <summary>
    /// 把一个完整 snapshot 投影进 workspace root。
    /// 这是 compatibility adapter 入口，不表示 snapshot 是 runtime 的主真相。
    /// </summary>
    public void Save(AgentEngineStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.Meta.SetSystemPrompt(snapshot.AgentState.SystemPrompt);
        _workspaceRoot.History.SetLastSerial(snapshot.AgentState.LastSerial);
        _workspaceRoot.History.ReplaceRecent(snapshot.AgentState.RecentHistory);
        _workspaceRoot.History.ReplacePendingNotifications(snapshot.AgentState.PendingNotifications);
        ReplaceRuntimeState(ToRuntimeStateSnapshot(snapshot));
    }

    /// <summary>
    /// 从当前 workspace materialize 一个 <see cref="AgentEngineStateSnapshot"/>。
    /// public non-live restore 应显式走 `Load()` -> `AgentEngine.CreateFromStateSnapshot(...)`；
    /// internal live host 则应直接围绕 workspaceRoot / session 恢复。
    /// </summary>
    public AgentEngineStateSnapshot Load() => LoadSnapshot(_workspaceRoot);

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

    private void ReplaceRuntimeState(AgentEngineRuntimeStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.SetToolSessionExecutionSequence(snapshot.ToolSessionExecutionSequence);
        _workspaceRoot.RuntimeState.ReplacePendingToolResults(snapshot.PendingToolResults);
        _workspaceRoot.RuntimeState.ReplaceTurnRuntime(snapshot.ResolvedProfile, snapshot.LockedCompactionSplitIndex);
        _workspaceRoot.RuntimeState.ReplacePendingCompaction(snapshot.PendingCompaction);
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
