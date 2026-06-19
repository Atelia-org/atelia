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

    internal void AppendHistoryEntry(HistoryEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOpenForState();
        _workspaceRoot.History.AppendRecent(entry);
    }

    internal string[] DrainPendingNotifications() {
        EnsureOpenForState();
        return _workspaceRoot.History.DrainPendingNotifications();
    }

    internal IReadOnlyList<string> LoadPendingNotifications() {
        EnsureOpenForState();
        return _workspaceRoot.History.LoadPendingNotifications();
    }

    internal (IReadOnlyList<HistoryEntry> RecentHistory, ulong LastSerial) LoadRecentHistorySnapshot() {
        EnsureOpenForState();
        return (
            RecentHistory: _workspaceRoot.History.LoadRecent(),
            LastSerial: _workspaceRoot.History.GetRequiredLastSerial()
        );
    }

    internal WorkspaceAppendActionMutationResult AppendAction(ActionEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOpenForState();
        var recentHistory = _workspaceRoot.History.LoadRecent();
        RecentHistoryRules.ValidateAppendOrder(recentHistory, entry);
        entry.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(entry));
        entry.AssignSerial(_workspaceRoot.History.AllocateNextSerial());
        _workspaceRoot.History.AppendRecent(entry);
        var (updatedRecentHistory, updatedLastSerial) = LoadRecentHistorySnapshot();

        return new WorkspaceAppendActionMutationResult(
            RecentHistory: updatedRecentHistory,
            LastSerial: updatedLastSerial
        );
    }

    internal WorkspaceInjectionMutationResult InjectActionContent(ActionInjectionRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content)) {
            throw new ArgumentException("Injected action content must not be null or whitespace.", nameof(request));
        }

        EnsureOpenForState();
        var recentHistory = _workspaceRoot.History.LoadRecent();
        if (recentHistory.Count == 0) {
            throw new InvalidOperationException("Cannot inject action content into empty history. At least one prior ActionEntry is required.");
        }

        var injectedBlockKind = RecentHistoryRules.ResolveInjectedBlockKind(recentHistory, request);
        if (recentHistory[^1] is ActionEntry tailAction) {
            RecentHistoryRules.EnsureActionAcceptsInjection(tailAction, context: "inject after trailing action");
        }

        var injectionEntry = new InjectionEntry(
            content: request.Content,
            blockKind: injectedBlockKind,
            source: request.Source
        );
        RecentHistoryRules.ValidateAppendOrder(recentHistory, injectionEntry);
        injectionEntry.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(injectionEntry));
        injectionEntry.AssignSerial(_workspaceRoot.History.AllocateNextSerial());
        _workspaceRoot.History.AppendRecent(injectionEntry);
        var (updatedRecentHistory, updatedLastSerial) = LoadRecentHistorySnapshot();

        return new WorkspaceInjectionMutationResult(
            RecentHistory: updatedRecentHistory,
            LastSerial: updatedLastSerial,
            Result: new ActionInjectionResult(
                InjectedEntrySerial: injectionEntry.Serial,
                InjectedBlockKind: injectedBlockKind
            )
        );
    }

    internal void AppendPendingNotification(string notification) {
        ArgumentNullException.ThrowIfNull(notification);

        EnsureOpenForState();
        _workspaceRoot.History.AppendPendingNotification(notification);
    }

    internal void ReplacePrefixWithRecap(int splitIndex, string summary) {
        EnsureOpenForState();

        var recentHistory = _workspaceRoot.History.LoadRecent();
        if (splitIndex < 1 || splitIndex >= recentHistory.Count) {
            throw new ArgumentOutOfRangeException(nameof(splitIndex), splitIndex, "splitIndex must replace a non-empty prefix and preserve a non-empty suffix.");
        }

        var insteadSerial = recentHistory[splitIndex - 1].Serial;
        var recap = new RecapEntry(summary, insteadSerial);
        recap.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(recap));
        recap.AssignSerial(_workspaceRoot.History.AllocateNextSerial());

        _workspaceRoot.History.ReplacePrefixWithRecap(splitIndex, recap);
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

internal sealed record WorkspaceAppendActionMutationResult(
    IReadOnlyList<HistoryEntry> RecentHistory,
    ulong LastSerial
);

internal sealed record WorkspaceInjectionMutationResult(
    IReadOnlyList<HistoryEntry> RecentHistory,
    ulong LastSerial,
    ActionInjectionResult Result
);
