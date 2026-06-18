using Atelia.Agent.Core.History;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core.Persistence;

internal sealed class AgentWorkspaceSession : IDisposable {
    private readonly AgentWorkspaceRoot _workspaceRoot;
    private readonly AgentEngineStateRoot _stateRoot;
    private readonly Repository? _repo;
    private bool _closed;

    private AgentWorkspaceSession(AgentEngineStateRoot stateRoot, Repository? repo) {
        _stateRoot = stateRoot ?? throw new ArgumentNullException(nameof(stateRoot));
        _workspaceRoot = stateRoot.WorkspaceRoot;
        _repo = repo;
    }

    internal static AgentWorkspaceSession Open(AgentEngineStateRoot stateRoot, Repository? repo = null) {
        return new AgentWorkspaceSession(stateRoot, repo);
    }

    internal AgentEngineStateRoot StateRoot => _stateRoot;

    internal AgentState RestoreState() {
        EnsureOpenForState();

        var state = AgentState.RestoreCore(
            LoadSystemPrompt(),
            LoadRecentHistory(),
            LoadPendingNotifications(),
            LoadLastSerial()
        );
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

    internal string LoadSystemPrompt() {
        EnsureOpenForState();
        return _workspaceRoot.Meta.GetRequiredSystemPrompt();
    }

    internal IReadOnlyList<HistoryEntry> LoadRecentHistory() {
        EnsureOpenForState();
        return _workspaceRoot.History.LoadRecent();
    }

    internal IReadOnlyList<string> LoadPendingNotifications() {
        EnsureOpenForState();
        return _workspaceRoot.History.LoadPendingNotifications();
    }

    internal ulong LoadLastSerial() {
        EnsureOpenForState();
        return _workspaceRoot.History.GetRequiredLastSerial();
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
        return _stateRoot.LoadRuntimeState();
    }

    internal void Commit() {
        EnsureOpenForEngine();
        if (_repo is null) { return; }

        _stateRoot.Commit(_repo);
    }

    internal void ReplacePendingToolResults(IReadOnlyList<ToolCallExecutionResult> pendingResults) {
        ArgumentNullException.ThrowIfNull(pendingResults);

        EnsureOpenForEngine();
        _stateRoot.ReplacePendingToolResults(pendingResults);
    }

    internal void UpsertPendingToolResult(ToolCallExecutionResult pendingResult) {
        ArgumentNullException.ThrowIfNull(pendingResult);

        EnsureOpenForEngine();
        _stateRoot.UpsertPendingToolResult(pendingResult);
    }

    internal void UpdateTurnRuntime(LlmProfileCheckpoint? resolvedProfile, int? lockedCompactionSplitIndex) {
        EnsureOpenForEngine();
        _stateRoot.UpdateTurnRuntime(resolvedProfile, lockedCompactionSplitIndex);
    }

    internal void SetPendingCompaction(CompactionCheckpoint pendingCompaction) {
        ArgumentNullException.ThrowIfNull(pendingCompaction);

        EnsureOpenForEngine();
        _stateRoot.SetPendingCompaction(pendingCompaction);
    }

    internal void ClearPendingCompaction() {
        EnsureOpenForEngine();
        _stateRoot.ClearPendingCompaction();
    }

    internal void SetToolSessionExecutionSequence(long executionSequence) {
        EnsureOpenForEngine();
        _stateRoot.SetToolSessionExecutionSequence(executionSequence);
    }
}
