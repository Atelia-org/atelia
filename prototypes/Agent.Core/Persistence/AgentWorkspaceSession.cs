using Atelia.Agent.Core.History;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core.Persistence;

internal enum AgentWorkspaceSessionFaultPoint {
    AfterReplacePendingToolResultsMutation,
    AfterUpsertPendingToolResultMutation,
    AfterUpdateTurnRuntimeMutation,
    AfterUpdatePendingCompactionMutation
}

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

    /// <summary>
    /// test-only fault injection seam，用于精确命中 engine 的 catch + reload 护栏。
    /// 返回非 null 异常时，session 会在对应 fault point 抛出它。
    /// </summary>
    internal Func<AgentWorkspaceSessionFaultPoint, Exception?>? FaultInjectionForTesting { get; set; }

    internal AgentWorkspaceRoot WorkspaceRoot => _workspaceRoot;

    internal AgentEngineStateSnapshot LoadSnapshot() {
        EnsureOpenForEngine();
        return AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(_workspaceRoot);
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
        return AgentState.RestoreSnapshot(LoadStateSnapshot(), this);
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

    internal string[] DrainPendingNotifications() {
        EnsureOpenForState();
        return _workspaceRoot.History.DrainPendingNotifications();
    }

    internal IReadOnlyList<string> LoadPendingNotifications() {
        EnsureOpenForState();
        return _workspaceRoot.History.LoadPendingNotifications();
    }

    internal IReadOnlyList<ToolCallExecutionResult> LoadPendingToolResults() {
        EnsureOpenForEngine();
        return _workspaceRoot.RuntimeState.LoadPendingToolResults();
    }

    internal AgentTurnRuntimeStateSnapshot LoadTurnRuntimeState() {
        EnsureOpenForEngine();
        var (resolvedProfile, lockedCompactionSplitIndex) = _workspaceRoot.RuntimeState.LoadTurnRuntime();
        return new AgentTurnRuntimeStateSnapshot(
            ResolvedProfile: resolvedProfile,
            LockedCompactionSplitIndex: lockedCompactionSplitIndex
        );
    }

    internal CompactionCheckpoint? LoadPendingCompaction() {
        EnsureOpenForEngine();
        return _workspaceRoot.RuntimeState.LoadPendingCompaction();
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

    internal AgentStateSnapshot AppendObservation(ObservationEntry entry, string? inlineNotifications = null) {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOpenForState();
        var recentHistory = _workspaceRoot.History.LoadRecent();
        if (RecentHistoryRules.HasPendingActionContinuation(recentHistory)) {
            throw new InvalidOperationException("Cannot append observation while a pending action continuation is open.");
        }

        RecentHistoryRules.ValidateAppendOrder(recentHistory, entry);
        AttachPendingNotifications(entry, inlineNotifications);
        entry.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(entry));
        entry.AssignSerial(_workspaceRoot.History.AllocateNextSerial());
        _workspaceRoot.History.AppendRecent(entry);
        return LoadStateSnapshot();
    }

    internal AgentStateSnapshot AppendToolResults(ToolResultsEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Results is not { Count: > 0 }) {
            throw new ArgumentException("ToolResultsEntry must include at least one tool result.", nameof(entry));
        }

        EnsureOpenForState();
        var recentHistory = _workspaceRoot.History.LoadRecent();
        if (RecentHistoryRules.HasPendingActionContinuation(recentHistory)) {
            throw new InvalidOperationException("Cannot append tool results while a pending action continuation is open.");
        }

        RecentHistoryRules.ValidateAppendOrder(recentHistory, entry);
        AttachPendingNotifications(entry);
        entry.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(entry));
        entry.AssignSerial(_workspaceRoot.History.AllocateNextSerial());
        _workspaceRoot.History.AppendRecent(entry);
        return LoadStateSnapshot();
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

    internal IReadOnlyList<string> AppendPendingNotification(string notification) {
        ArgumentNullException.ThrowIfNull(notification);

        EnsureOpenForState();
        _workspaceRoot.History.AppendPendingNotification(notification);
        return _workspaceRoot.History.LoadPendingNotifications();
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

    internal string SetSystemPrompt(string systemPrompt) {
        ArgumentNullException.ThrowIfNull(systemPrompt);

        EnsureOpenForState();
        _workspaceRoot.Meta.SetSystemPrompt(systemPrompt);
        return _workspaceRoot.Meta.GetRequiredSystemPrompt();
    }

    internal AgentEngineRuntimeStateSnapshot LoadRuntimeState() {
        EnsureOpenForEngine();

        var pendingToolResults = _workspaceRoot.RuntimeState.LoadPendingToolResults();
        var turnRuntime = LoadTurnRuntimeState();
        var pendingCompaction = _workspaceRoot.RuntimeState.LoadPendingCompaction();
        var toolSessionExecutionSequence = _workspaceRoot.RuntimeState.GetToolSessionExecutionSequenceOrDefault();

        return new AgentEngineRuntimeStateSnapshot(
            PendingToolResults: pendingToolResults,
            ResolvedProfile: turnRuntime.ResolvedProfile,
            LockedCompactionSplitIndex: turnRuntime.LockedCompactionSplitIndex,
            PendingCompaction: pendingCompaction,
            ToolSessionExecutionSequence: toolSessionExecutionSequence
        );
    }

    internal void Commit() {
        EnsureOpenForEngine();
        if (_repo is null) { return; }

        _repo.Commit(_workspaceRoot.Root).Unwrap();
    }

    internal IReadOnlyList<ToolCallExecutionResult> ReplacePendingToolResults(IReadOnlyList<ToolCallExecutionResult> pendingResults) {
        ArgumentNullException.ThrowIfNull(pendingResults);

        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.ReplacePendingToolResults(pendingResults);
        ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterReplacePendingToolResultsMutation);
        return _workspaceRoot.RuntimeState.LoadPendingToolResults();
    }

    internal IReadOnlyList<ToolCallExecutionResult> UpsertPendingToolResult(ToolCallExecutionResult pendingResult) {
        ArgumentNullException.ThrowIfNull(pendingResult);

        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.UpsertPendingToolResult(pendingResult);
        ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterUpsertPendingToolResultMutation);
        return _workspaceRoot.RuntimeState.LoadPendingToolResults();
    }

    internal AgentTurnRuntimeStateSnapshot UpdateTurnRuntime(
        LlmProfileCheckpoint? resolvedProfile,
        int? lockedCompactionSplitIndex
    ) {
        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        _workspaceRoot.RuntimeState.UpdateTurnRuntime(resolvedProfile, lockedCompactionSplitIndex);
        ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterUpdateTurnRuntimeMutation);
        return LoadTurnRuntimeState();
    }

    internal CompactionCheckpoint? UpdatePendingCompaction(CompactionCheckpoint? pendingCompaction) {
        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        if (pendingCompaction is null) {
            _workspaceRoot.RuntimeState.ClearPendingCompaction();
        }
        else {
            _workspaceRoot.RuntimeState.SetPendingCompaction(pendingCompaction);
        }

        ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterUpdatePendingCompactionMutation);
        return _workspaceRoot.RuntimeState.LoadPendingCompaction();
    }

    internal long AllocateToolSessionExecutionSequence() {
        EnsureOpenForEngine();
        _workspaceRoot.Meta.Stamp();
        return _workspaceRoot.RuntimeState.AllocateNextToolSessionExecutionSequence();
    }

    private void AttachPendingNotifications(ObservationEntry entry, string? inlineNotifications = null) {
        var queuedNotifications = CollapseNotifications(_workspaceRoot.History.DrainPendingNotifications());
        var notifications = MergeNotifications(queuedNotifications, inlineNotifications);
        if (notifications is null) {
            return;
        }

        entry.MergeNotifications(notifications);
    }

    private void ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint point) {
        var exception = FaultInjectionForTesting?.Invoke(point);
        if (exception is not null) {
            throw exception;
        }
    }

    private static string? CollapseNotifications(IReadOnlyList<string> notifications) {
        if (notifications.Count == 0) {
            return null;
        }

        return string.Join("\n", notifications);
    }

    private static string? MergeNotifications(string? first, string? second) {
        if (first is null) { return second; }
        if (second is null) { return first; }
        return string.Join("\n", first, second);
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
