using Atelia.Agent.Core.History;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core.Persistence;

internal sealed class AgentWorkspaceSession : IDisposable {
    private readonly AgentWorkspaceRoot _workspaceRoot;
    private readonly Repository? _repo;
    private bool _closed;

    private AgentWorkspaceSession(AgentWorkspaceRoot workspaceRoot, Repository? repo) {
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
        _repo = repo;
    }

    internal static AgentWorkspaceSession Open(AgentWorkspaceRoot workspaceRoot, Repository? repo = null) {
        return new AgentWorkspaceSession(workspaceRoot, repo);
    }

    internal AgentWorkspaceRoot WorkspaceRoot => _workspaceRoot;

    internal AgentEngineStateSnapshot LoadSnapshot() {
        EnsureOpenForEngine();
        return AgentEngineStateRoot.LoadSnapshot(_workspaceRoot);
    }

    internal AgentStateSnapshot LoadStateSnapshot() {
        EnsureOpenForState();

        return new AgentStateSnapshot(
            SystemPrompt: _workspaceRoot.Meta.GetRequiredSystemPrompt(),
            RecentHistory: _workspaceRoot.History.LoadRecent(),
            PendingNotifications: _workspaceRoot.History.LoadPendingNotifications(),
            LastSerial: _workspaceRoot.History.GetRequiredLastSerial()
        );
    }

    internal AgentState RestoreState() {
        EnsureOpenForState();

        var state = AgentState.RestoreSnapshot(LoadStateSnapshot());
        state.BindWorkspaceSession(this);
        return state;
    }

    internal void Close() {
        if (_closed) {
            return;
        }

        _closed = true;
        _repo?.Dispose();
    }

    public void Dispose() {
        Close();
    }

    internal void EnsureOpenForState() {
        if (_closed) {
            throw new InvalidOperationException("AgentState workspace session has been closed.");
        }
    }

    internal void EnsureOpenForEngine() {
        if (_closed) {
            throw new InvalidOperationException("AgentEngine workspace session has been closed.");
        }
    }

    internal ulong AllocateNextSerial() {
        EnsureOpenForState();
        return _workspaceRoot.History.AllocateNextSerial();
    }

    internal void ReplaceRecentHistory(IReadOnlyList<HistoryEntry> recentHistory, ulong lastSerial) {
        ArgumentNullException.ThrowIfNull(recentHistory);

        EnsureOpenForState();
        _workspaceRoot.History.ReplaceRecent(recentHistory);
        _workspaceRoot.History.SetLastSerial(lastSerial);
    }

    internal void AppendHistoryEntry(HistoryEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOpenForState();
        _workspaceRoot.History.AppendRecent(entry);
    }

    internal void ReplacePendingNotifications(IReadOnlyList<string> notifications) {
        ArgumentNullException.ThrowIfNull(notifications);

        EnsureOpenForState();
        _workspaceRoot.History.ReplacePendingNotifications(notifications);
    }

    internal void AppendPendingNotification(string notification) {
        ArgumentNullException.ThrowIfNull(notification);

        EnsureOpenForState();
        _workspaceRoot.History.AppendPendingNotification(notification);
    }

    internal void SetSystemPrompt(string systemPrompt) {
        ArgumentNullException.ThrowIfNull(systemPrompt);

        EnsureOpenForState();
        _workspaceRoot.Meta.SetSystemPrompt(systemPrompt);
    }

    internal AgentEngineRuntimeStateSnapshot LoadRuntimeState() {
        EnsureOpenForEngine();

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

    internal void Commit() {
        EnsureOpenForEngine();
        if (_repo is null) { return; }

        _repo.Commit(_workspaceRoot.Root).Unwrap();
    }

    internal void ReplacePendingToolResults(IReadOnlyList<ToolCallExecutionResult> pendingResults) {
        ArgumentNullException.ThrowIfNull(pendingResults);

        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.ReplacePendingToolResults(pendingResults);
    }

    internal void UpsertPendingToolResult(ToolCallExecutionResult pendingResult) {
        ArgumentNullException.ThrowIfNull(pendingResult);

        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.UpsertPendingToolResult(pendingResult);
    }

    internal void UpdateTurnRuntime(LlmProfileCheckpoint? resolvedProfile, int? lockedCompactionSplitIndex) {
        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.UpdateTurnRuntime(resolvedProfile, lockedCompactionSplitIndex);
    }

    internal void SetPendingCompaction(CompactionCheckpoint pendingCompaction) {
        ArgumentNullException.ThrowIfNull(pendingCompaction);

        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.SetPendingCompaction(pendingCompaction);
    }

    internal void ClearPendingCompaction() {
        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.ClearPendingCompaction();
    }

    internal void SetToolSessionExecutionSequence(long executionSequence) {
        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.SetToolSessionExecutionSequence(executionSequence);
    }
}
