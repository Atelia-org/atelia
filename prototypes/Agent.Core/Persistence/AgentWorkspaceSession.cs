using Atelia.Agent.Core.History;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core.Persistence;

internal enum AgentWorkspaceSessionFaultPoint {
    AfterReplacePendingToolResultsMutation,
    AfterUpsertPendingToolResultMutation,
    AfterUpdateTurnRuntimeMutation,
    AfterUpdatePendingCompactionMutation,
    AfterFoldPendingNotificationsIntoCurrentObservationMutation,
    AfterReplacePrefixWithRecapFrontPopMutation,
    AfterReplacePrefixWithRecapMutation,
    AfterRewriteRecentHistoryTailStepMutation,
    AfterRewriteRecentHistoryTailMutation
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

    internal (string SystemPrompt, IReadOnlyList<HistoryEntry> RecentHistory, IReadOnlyList<string> PendingNotifications, ulong LastSerial) LoadStateCacheSeed() {
        EnsureOpenForState();

        return (
            SystemPrompt: _workspaceRoot.Meta.GetRequiredSystemPrompt(),
            RecentHistory: _workspaceRoot.History.LoadRecent(),
            PendingNotifications: _workspaceRoot.History.LoadPendingNotifications(),
            LastSerial: _workspaceRoot.History.GetRequiredLastSerial()
        );
    }

    internal AgentState RestoreState() {
        EnsureOpenForState();
        var (systemPrompt, recentHistory, pendingNotifications, lastSerial) = LoadStateCacheSeed();
        return AgentState.RestoreWorkspaceState(this, systemPrompt, recentHistory, pendingNotifications, lastSerial);
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

    internal (LlmProfileCheckpoint? ResolvedProfile, int? LockedCompactionSplitIndex) LoadTurnRuntimeState() {
        EnsureOpenForEngine();
        return _workspaceRoot.RuntimeState.LoadTurnRuntime();
    }

    internal CompactionCheckpoint? LoadPendingCompaction() {
        EnsureOpenForEngine();
        return _workspaceRoot.RuntimeState.LoadPendingCompaction();
    }

    internal (IReadOnlyList<HistoryEntry> RecentHistory, ulong LastSerial) LoadRecentHistoryState() {
        EnsureOpenForState();
        return (
            RecentHistory: _workspaceRoot.History.LoadRecent(),
            LastSerial: _workspaceRoot.History.GetRequiredLastSerial()
        );
    }

    internal WorkspaceAppendActionMutationResult AppendAction(ActionEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOpenForState();
        var authoritativePreRecentHistory = _workspaceRoot.History.LoadRecent();
        var authoritativePreLastSerial = _workspaceRoot.History.GetRequiredLastSerial();
        RecentHistoryRules.ValidateAppendOrder(authoritativePreRecentHistory, entry);
        entry.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(entry));
        entry.AssignSerial(_workspaceRoot.History.AllocateNextSerial());
        _workspaceRoot.History.AppendRecent(entry);

        return new WorkspaceAppendActionMutationResult(
            AuthoritativePreRecentHistory: authoritativePreRecentHistory,
            AuthoritativePreLastSerial: authoritativePreLastSerial,
            AppendedEntry: entry,
            LastSerial: entry.Serial
        );
    }

    internal WorkspaceWorkingSetAppendMutationResult AppendObservation(ObservationEntry entry, string? inlineNotifications = null) {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOpenForState();
        var authoritativePreRecentHistory = _workspaceRoot.History.LoadRecent();
        var authoritativePreLastSerial = _workspaceRoot.History.GetRequiredLastSerial();
        if (RecentHistoryRules.HasPendingActionContinuation(authoritativePreRecentHistory)) {
            throw new InvalidOperationException("Cannot append observation while a pending action continuation is open.");
        }

        var authoritativePrePendingNotifications = _workspaceRoot.History.LoadPendingNotifications();
        RecentHistoryRules.ValidateAppendOrder(authoritativePreRecentHistory, entry);
        AttachPendingNotifications(entry, inlineNotifications);
        entry.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(entry));
        entry.AssignSerial(_workspaceRoot.History.AllocateNextSerial());
        _workspaceRoot.History.AppendRecent(entry);
        return new WorkspaceWorkingSetAppendMutationResult(
            AuthoritativePreRecentHistory: authoritativePreRecentHistory,
            AuthoritativePreLastSerial: authoritativePreLastSerial,
            AuthoritativePrePendingNotifications: authoritativePrePendingNotifications,
            AppendedEntry: entry,
            LastSerial: entry.Serial
        );
    }

    internal WorkspaceWorkingSetAppendMutationResult AppendToolResults(ToolResultsEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Results is not { Count: > 0 }) {
            throw new ArgumentException("ToolResultsEntry must include at least one tool result.", nameof(entry));
        }

        EnsureOpenForState();
        var authoritativePreRecentHistory = _workspaceRoot.History.LoadRecent();
        var authoritativePreLastSerial = _workspaceRoot.History.GetRequiredLastSerial();
        if (RecentHistoryRules.HasPendingActionContinuation(authoritativePreRecentHistory)) {
            throw new InvalidOperationException("Cannot append tool results while a pending action continuation is open.");
        }

        var authoritativePrePendingNotifications = _workspaceRoot.History.LoadPendingNotifications();
        RecentHistoryRules.ValidateAppendOrder(authoritativePreRecentHistory, entry);
        AttachPendingNotifications(entry);
        entry.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(entry));
        entry.AssignSerial(_workspaceRoot.History.AllocateNextSerial());
        _workspaceRoot.History.AppendRecent(entry);
        return new WorkspaceWorkingSetAppendMutationResult(
            AuthoritativePreRecentHistory: authoritativePreRecentHistory,
            AuthoritativePreLastSerial: authoritativePreLastSerial,
            AuthoritativePrePendingNotifications: authoritativePrePendingNotifications,
            AppendedEntry: entry,
            LastSerial: entry.Serial
        );
    }

    internal WorkspaceInjectionMutationResult InjectActionContent(ActionInjectionRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content)) {
            throw new ArgumentException("Injected action content must not be null or whitespace.", nameof(request));
        }

        EnsureOpenForState();
        var authoritativePreRecentHistory = _workspaceRoot.History.LoadRecent();
        var authoritativePreLastSerial = _workspaceRoot.History.GetRequiredLastSerial();
        if (authoritativePreRecentHistory.Count == 0) {
            throw new InvalidOperationException("Cannot inject action content into empty history. At least one prior ActionEntry is required.");
        }

        var injectedBlockKind = RecentHistoryRules.ResolveInjectedBlockKind(authoritativePreRecentHistory, request);
        if (authoritativePreRecentHistory[^1] is ActionEntry tailAction) {
            RecentHistoryRules.EnsureActionAcceptsInjection(tailAction, context: "inject after trailing action");
        }

        var injectionEntry = new InjectionEntry(
            content: request.Content,
            blockKind: injectedBlockKind,
            source: request.Source
        );
        RecentHistoryRules.ValidateAppendOrder(authoritativePreRecentHistory, injectionEntry);
        injectionEntry.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(injectionEntry));
        injectionEntry.AssignSerial(_workspaceRoot.History.AllocateNextSerial());
        _workspaceRoot.History.AppendRecent(injectionEntry);

        return new WorkspaceInjectionMutationResult(
            AuthoritativePreRecentHistory: authoritativePreRecentHistory,
            AuthoritativePreLastSerial: authoritativePreLastSerial,
            AppendedEntry: injectionEntry,
            LastSerial: injectionEntry.Serial,
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

    internal WorkspaceFoldPendingNotificationsIntoObservationMutationResult FoldPendingNotificationsIntoCurrentObservation() {
        EnsureOpenForState();

        var authoritativePreRecentHistory = _workspaceRoot.History.LoadRecent();
        var authoritativePrePendingNotifications = _workspaceRoot.History.LoadPendingNotifications();
        var lastSerial = _workspaceRoot.History.GetRequiredLastSerial();
        if (authoritativePrePendingNotifications.Count == 0) {
            return new WorkspaceFoldPendingNotificationsIntoObservationMutationResult(
                AuthoritativePreRecentHistory: authoritativePreRecentHistory,
                AuthoritativePreLastSerial: lastSerial,
                AuthoritativePrePendingNotifications: authoritativePrePendingNotifications,
                UpdatedObservation: null,
                LastSerial: lastSerial
            );
        }

        if (authoritativePreRecentHistory.Count == 0 || authoritativePreRecentHistory[^1] is not ObservationEntry observation) {
            throw new InvalidOperationException("Cannot fold pending notifications because the durable recent-history tail is not observation-like.");
        }

        var collapsedNotifications = CollapseNotifications(authoritativePrePendingNotifications)
            ?? throw new InvalidOperationException("Pending notifications snapshot unexpectedly collapsed to null.");
        var updatedObservation = ObservationEntryMutationHelper.CloneWithMergedNotifications(observation, collapsedNotifications);
        _workspaceRoot.History.ReplaceRecentAt(authoritativePreRecentHistory.Count - 1, updatedObservation);
        _workspaceRoot.History.ReplacePendingNotifications(Array.Empty<string>());
        ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterFoldPendingNotificationsIntoCurrentObservationMutation);

        return new WorkspaceFoldPendingNotificationsIntoObservationMutationResult(
            AuthoritativePreRecentHistory: authoritativePreRecentHistory,
            AuthoritativePreLastSerial: lastSerial,
            AuthoritativePrePendingNotifications: authoritativePrePendingNotifications,
            UpdatedObservation: updatedObservation,
            LastSerial: lastSerial
        );
    }

    internal WorkspaceFoldPendingNotificationsIntoObservationMutationResult FoldPendingNotificationsIntoCurrentToolResults() {
        EnsureOpenForState();

        var authoritativePreRecentHistory = _workspaceRoot.History.LoadRecent();
        var authoritativePrePendingNotifications = _workspaceRoot.History.LoadPendingNotifications();
        var lastSerial = _workspaceRoot.History.GetRequiredLastSerial();
        if (authoritativePrePendingNotifications.Count == 0) {
            return new WorkspaceFoldPendingNotificationsIntoObservationMutationResult(
                AuthoritativePreRecentHistory: authoritativePreRecentHistory,
                AuthoritativePreLastSerial: lastSerial,
                AuthoritativePrePendingNotifications: authoritativePrePendingNotifications,
                UpdatedObservation: null,
                LastSerial: lastSerial
            );
        }

        if (authoritativePreRecentHistory.Count == 0 || authoritativePreRecentHistory[^1] is not ToolResultsEntry toolResultsEntry) {
            throw new InvalidOperationException("Cannot fold pending notifications because the durable recent-history tail is not a ToolResultsEntry.");
        }

        var collapsedNotifications = CollapseNotifications(authoritativePrePendingNotifications)
            ?? throw new InvalidOperationException("Pending notifications snapshot unexpectedly collapsed to null.");
        var updatedToolResults = AssertToolResultsEntry(
            ObservationEntryMutationHelper.CloneWithMergedNotifications(toolResultsEntry, collapsedNotifications)
        );
        _workspaceRoot.History.ReplaceRecentAt(authoritativePreRecentHistory.Count - 1, updatedToolResults);
        _workspaceRoot.History.ReplacePendingNotifications(Array.Empty<string>());
        ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterFoldPendingNotificationsIntoCurrentObservationMutation);

        return new WorkspaceFoldPendingNotificationsIntoObservationMutationResult(
            AuthoritativePreRecentHistory: authoritativePreRecentHistory,
            AuthoritativePreLastSerial: lastSerial,
            AuthoritativePrePendingNotifications: authoritativePrePendingNotifications,
            UpdatedObservation: updatedToolResults,
            LastSerial: lastSerial
        );
    }

    internal WorkspaceRecapMutationResult ReplacePrefixWithRecap(int splitIndex, string summary) {
        EnsureOpenForState();

        var authoritativePreRecentHistory = _workspaceRoot.History.LoadRecent();
        var previousLastSerial = _workspaceRoot.History.GetRequiredLastSerial();
        if (splitIndex < 1 || splitIndex >= authoritativePreRecentHistory.Count) {
            throw new ArgumentOutOfRangeException(nameof(splitIndex), splitIndex, "splitIndex must replace a non-empty prefix and preserve a non-empty suffix.");
        }
        if (!authoritativePreRecentHistory[splitIndex - 1].IsObservationLike) {
            throw new InvalidOperationException("splitIndex must end the replaced prefix at an observation-like entry.");
        }
        if (!authoritativePreRecentHistory[splitIndex].IsActorLike) {
            throw new InvalidOperationException("splitIndex must preserve an actor-like suffix head.");
        }

        var insteadSerial = authoritativePreRecentHistory[splitIndex - 1].Serial;
        var recap = new RecapEntry(summary, insteadSerial);
        recap.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(recap));
        recap.AssignSerial(_workspaceRoot.History.AllocateNextSerial());

        try {
            _workspaceRoot.History.ReplaceRecentAt(splitIndex - 1, recap);
            for (int index = 0; index < splitIndex - 1; index++) {
                _workspaceRoot.History.PopFrontRecent();
                ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterReplacePrefixWithRecapFrontPopMutation);
            }
        }
        catch {
            _workspaceRoot.History.ReplaceRecent(authoritativePreRecentHistory);
            _workspaceRoot.History.SetLastSerial(previousLastSerial);
            throw;
        }

        ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterReplacePrefixWithRecapMutation);
        return new WorkspaceRecapMutationResult(
            AuthoritativePreRecentHistory: authoritativePreRecentHistory,
            AuthoritativePreLastSerial: previousLastSerial,
            SplitIndex: splitIndex,
            RecapEntry: recap,
            LastSerial: recap.Serial
        );
    }

    internal WorkspaceTailRewriteMutationResult RewriteRecentHistoryTail(
        ulong anchorSerial,
        IReadOnlyList<HistoryEntry> replacementEntries
    ) {
        ArgumentNullException.ThrowIfNull(replacementEntries);

        EnsureOpenForState();
        var authoritativePreRecentHistory = _workspaceRoot.History.LoadRecent();
        var authoritativePreLastSerial = _workspaceRoot.History.GetRequiredLastSerial();
        var anchorIndex = RecentHistoryRules.FindIndexBySerial(authoritativePreRecentHistory, anchorSerial);
        if (anchorIndex < 0) {
            throw new InvalidOperationException($"Cannot rewrite recent history tail because anchor serial {anchorSerial} was not found.");
        }

        RecentHistoryRules.ValidateTailRewrite(authoritativePreRecentHistory, anchorIndex, replacementEntries);

        try {
            for (int index = authoritativePreRecentHistory.Count - 1; index > anchorIndex; index--) {
                _workspaceRoot.History.PopBackRecent();
                ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterRewriteRecentHistoryTailStepMutation);
            }

            foreach (var replacementEntry in replacementEntries) {
                ArgumentNullException.ThrowIfNull(replacementEntry);
                replacementEntry.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(replacementEntry));
                replacementEntry.AssignSerial(_workspaceRoot.History.AllocateNextSerial());
                _workspaceRoot.History.AppendRecent(replacementEntry);
                ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterRewriteRecentHistoryTailStepMutation);
            }
        }
        catch {
            _workspaceRoot.History.ReplaceRecent(authoritativePreRecentHistory);
            _workspaceRoot.History.SetLastSerial(authoritativePreLastSerial);
            throw;
        }

        var lastSerial = _workspaceRoot.History.GetRequiredLastSerial();
        ThrowInjectedFaultIfAny(AgentWorkspaceSessionFaultPoint.AfterRewriteRecentHistoryTailMutation);
        return new WorkspaceTailRewriteMutationResult(
            AuthoritativePreRecentHistory: authoritativePreRecentHistory,
            AuthoritativePreLastSerial: authoritativePreLastSerial,
            AnchorIndex: anchorIndex,
            ReplacementEntries: replacementEntries,
            LastSerial: lastSerial
        );
    }

    internal string SetSystemPrompt(string systemPrompt) {
        ArgumentNullException.ThrowIfNull(systemPrompt);

        EnsureOpenForState();
        _workspaceRoot.Meta.SetSystemPrompt(systemPrompt);
        return _workspaceRoot.Meta.GetRequiredSystemPrompt();
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

    internal (LlmProfileCheckpoint? ResolvedProfile, int? LockedCompactionSplitIndex) UpdateTurnRuntime(
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

    internal long LoadToolSessionExecutionSequence() {
        EnsureOpenForEngine();
        return _workspaceRoot.RuntimeState.GetToolSessionExecutionSequenceOrDefault();
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

    private static ToolResultsEntry AssertToolResultsEntry(ObservationEntry entry) {
        return entry as ToolResultsEntry
            ?? throw new InvalidOperationException("Merged tool-results observation unexpectedly lost its ToolResultsEntry subtype.");
    }

    private static string? MergeNotifications(string? first, string? second) {
        if (first is null) { return second; }
        if (second is null) { return first; }
        return string.Join("\n", first, second);
    }
}

internal sealed record WorkspaceAppendActionMutationResult(
    IReadOnlyList<HistoryEntry> AuthoritativePreRecentHistory,
    ulong AuthoritativePreLastSerial,
    ActionEntry AppendedEntry,
    ulong LastSerial
);

internal sealed record WorkspaceWorkingSetAppendMutationResult(
    IReadOnlyList<HistoryEntry> AuthoritativePreRecentHistory,
    ulong AuthoritativePreLastSerial,
    IReadOnlyList<string> AuthoritativePrePendingNotifications,
    ObservationEntry AppendedEntry,
    ulong LastSerial
);

internal sealed record WorkspaceInjectionMutationResult(
    IReadOnlyList<HistoryEntry> AuthoritativePreRecentHistory,
    ulong AuthoritativePreLastSerial,
    InjectionEntry AppendedEntry,
    ulong LastSerial,
    ActionInjectionResult Result
);

internal sealed record WorkspaceFoldPendingNotificationsIntoObservationMutationResult(
    IReadOnlyList<HistoryEntry> AuthoritativePreRecentHistory,
    ulong AuthoritativePreLastSerial,
    IReadOnlyList<string> AuthoritativePrePendingNotifications,
    ObservationEntry? UpdatedObservation,
    ulong LastSerial
) {
    public bool FoldApplied => UpdatedObservation is not null;
}

internal sealed record WorkspaceRecapMutationResult(
    IReadOnlyList<HistoryEntry> AuthoritativePreRecentHistory,
    ulong AuthoritativePreLastSerial,
    int SplitIndex,
    RecapEntry RecapEntry,
    ulong LastSerial
);

internal sealed record WorkspaceTailRewriteMutationResult(
    IReadOnlyList<HistoryEntry> AuthoritativePreRecentHistory,
    ulong AuthoritativePreLastSerial,
    int AnchorIndex,
    IReadOnlyList<HistoryEntry> ReplacementEntries,
    ulong LastSerial
);
