using Atelia.Agent.Core.History;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core.Persistence;

/// <summary>
/// 以 <see cref="DurableDict{TKey}"/> 为 graph root 的 AgentEngine 持久化 façade。
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

        return Create(AgentWorkspaceRoot.Create(revision), systemPrompt);
    }

    internal static AgentEngineStateRoot Create(AgentWorkspaceRoot workspaceRoot, string? systemPrompt = null) {
        ArgumentNullException.ThrowIfNull(workspaceRoot);

        var stateRoot = new AgentEngineStateRoot(workspaceRoot);
        var defaultState = AgentState.CreateDefault(systemPrompt);
        stateRoot.Save(
            new AgentEngineStateSnapshot(
                AgentState: defaultState.ExportSnapshot(),
                PendingToolResults: Array.Empty<ToolCallExecutionResult>(),
                ResolvedProfile: null,
                LockedCompactionSplitIndex: null,
                PendingCompaction: null,
                ToolSessionExecutionSequence: 0
            )
        );

        return stateRoot;
    }

    public static AgentEngineStateRoot Create(Revision revision, AgentEngine engine) {
        ArgumentNullException.ThrowIfNull(engine);

        var stateRoot = Create(revision, engine.SystemPrompt);
        stateRoot.Save(engine);
        return stateRoot;
    }

    public static AgentEngineStateRoot FromRoot(DurableDict<string> root) => FromWorkspaceRoot(AgentWorkspaceRoot.FromRoot(root));

    internal static AgentEngineStateRoot FromWorkspaceRoot(AgentWorkspaceRoot workspaceRoot) => new(workspaceRoot);

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

    public void Save(AgentEngineStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.Meta.SetSystemPrompt(snapshot.AgentState.SystemPrompt);
        _workspaceRoot.Meta.SetLastSerial(snapshot.AgentState.LastSerial);
        _workspaceRoot.History.SaveRecent(snapshot.AgentState.RecentHistory);
        _workspaceRoot.History.SavePendingNotifications(snapshot.AgentState.PendingNotifications);
        SaveRuntimeState(ToRuntimeStateSnapshot(snapshot));
    }

    public AgentEngineStateSnapshot Load() {
        string systemPrompt = _workspaceRoot.Meta.GetRequiredSystemPrompt();
        ulong lastSerial = _workspaceRoot.Meta.GetRequiredLastSerial();
        var recentHistory = _workspaceRoot.History.LoadRecent();
        var pendingNotifications = _workspaceRoot.History.LoadPendingNotifications();
        var runtimeState = LoadRuntimeState();

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

    internal void SaveRuntimeState(AgentEngine engine) {
        ArgumentNullException.ThrowIfNull(engine);
        SaveRuntimeState(engine.ExportRuntimeStateSnapshot());
    }

    internal void SaveRuntimeStateAndCommit(Repository repo, AgentEngine engine) {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(engine);

        SaveRuntimeState(engine);
        repo.Commit(Root).Unwrap();
    }

    internal void Commit(Repository repo) {
        ArgumentNullException.ThrowIfNull(repo);
        repo.Commit(Root).Unwrap();
    }

    internal void SavePendingToolResults(IReadOnlyList<ToolCallExecutionResult> pendingResults) {
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.SavePendingToolResults(pendingResults);
    }

    internal void SaveTurnRuntime(LlmProfileCheckpoint? resolvedProfile, int? lockedCompactionSplitIndex) {
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.SaveTurnRuntime(resolvedProfile, lockedCompactionSplitIndex);
    }

    internal void SavePendingCompaction(CompactionCheckpoint? pendingCompaction) {
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.SavePendingCompaction(pendingCompaction);
    }

    internal void SetToolSessionExecutionSequence(long executionSequence) {
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.SetToolSessionExecutionSequence(executionSequence);
    }

    internal void SaveRuntimeState(AgentEngineRuntimeStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.SetToolSessionExecutionSequence(snapshot.ToolSessionExecutionSequence);
        _workspaceRoot.RuntimeState.SavePendingToolResults(snapshot.PendingToolResults);
        _workspaceRoot.RuntimeState.SaveTurnRuntime(snapshot.ResolvedProfile, snapshot.LockedCompactionSplitIndex);
        _workspaceRoot.RuntimeState.SavePendingCompaction(snapshot.PendingCompaction);
    }

    internal AgentEngineRuntimeStateSnapshot LoadRuntimeState() {
        var pendingToolResults = _workspaceRoot.RuntimeState.LoadPendingToolResults();
        var (resolvedProfile, lockedCompactionSplitIndex) = _workspaceRoot.RuntimeState.LoadTurnRuntime();
        var pendingCompaction = _workspaceRoot.RuntimeState.LoadPendingCompaction();
        var toolSessionExecutionSequence = _workspaceRoot.RuntimeState.GetToolSessionExecutionSequenceOrDefault();

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
