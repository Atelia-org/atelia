using Atelia.Agent.Core.Persistence;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.Agent.Core.History;

public sealed partial class AgentState {
    private AgentWorkspaceSession? _workspaceSession;

    private void BindWorkspaceSession(AgentWorkspaceSession workspaceSession) {
        ArgumentNullException.ThrowIfNull(workspaceSession);
        EnsureWorkspaceSessionOpen();

        if (_workspaceSession is not null) {
            if (ReferenceEquals(_workspaceSession, workspaceSession)) {
                return;
            }

            throw new InvalidOperationException("AgentState is already live-bound to a workspace session.");
        }

        _workspaceSession = workspaceSession;
    }

    private void EnsureWorkspaceSessionOpen() {
        _workspaceSession?.EnsureOpenForState();
    }

    private void ReloadWorkingSetFromWorkspaceSession() {
        var (systemPrompt, recentHistory, pendingNotifications, lastSerial) = _workspaceSession?.LoadStateCacheSeed()
            ?? throw new InvalidOperationException("AgentState is not live-bound to a workspace session.");

        ApplyWorkspaceState(systemPrompt, recentHistory, pendingNotifications, lastSerial);
    }

    private void ReloadRecentHistoryFromWorkspaceSession() {
        var (recentHistory, lastSerial) = _workspaceSession?.LoadRecentHistoryState()
            ?? throw new InvalidOperationException("AgentState is not live-bound to a workspace session.");

        ApplyRecentHistoryState(recentHistory, lastSerial);
    }

    private void ApplyRecentHistoryState(
        IReadOnlyList<HistoryEntry> recentHistory,
        ulong lastSerial
    ) {
        _workingSet.ReplaceRecentHistory(recentHistory, lastSerial, CloneHistoryEntry);
    }

    private bool TryApplyRecentHistoryDelta(
        IReadOnlyList<HistoryEntry> authoritativePreRecentHistory,
        HistoryEntry appendedEntry,
        ulong lastSerial
    ) {
        if (!IsRecentHistoryCacheAligned(authoritativePreRecentHistory)) {
            return false;
        }

        _workingSet.BackfillAppendedHistoryEntry(appendedEntry, lastSerial);
        return true;
    }

    private bool TryApplyWorkingSetDelta(
        IReadOnlyList<HistoryEntry> authoritativePreRecentHistory,
        IReadOnlyList<string> authoritativePrePendingNotifications,
        ObservationEntry appendedEntry,
        ulong lastSerial
    ) {
        if (!IsRecentHistoryCacheAligned(authoritativePreRecentHistory)
            || !IsPendingNotificationsCacheAligned(authoritativePrePendingNotifications)) {
            return false;
        }

        _workingSet.ClearPendingNotifications();
        _workingSet.BackfillAppendedHistoryEntry(appendedEntry, lastSerial);
        return true;
    }

    private bool IsRecentHistoryCacheAligned(IReadOnlyList<HistoryEntry> authoritativeRecentHistory) {
        ArgumentNullException.ThrowIfNull(authoritativeRecentHistory);

        if (_workingSet.LastSerial != GetMaxSerial(authoritativeRecentHistory)) {
            return false;
        }

        var localRecentHistory = _workingSet.RecentHistory;
        if (localRecentHistory.Count != authoritativeRecentHistory.Count) {
            return false;
        }

        for (int index = 0; index < localRecentHistory.Count; index++) {
            if (!HistoryEntriesStructurallyEqual(localRecentHistory[index], authoritativeRecentHistory[index])) {
                return false;
            }
        }

        return true;
    }

    private bool IsPendingNotificationsCacheAligned(IReadOnlyList<string> authoritativePendingNotifications) {
        ArgumentNullException.ThrowIfNull(authoritativePendingNotifications);

        var localPendingNotifications = _workingSet.ExportPendingNotificationsSnapshot();
        if (localPendingNotifications.Length != authoritativePendingNotifications.Count) {
            return false;
        }

        for (int index = 0; index < localPendingNotifications.Length; index++) {
            if (!string.Equals(localPendingNotifications[index], authoritativePendingNotifications[index], StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    private static ulong GetMaxSerial(IReadOnlyList<HistoryEntry> recentHistory) {
        ulong maxSerial = 0;
        foreach (var entry in recentHistory) {
            ArgumentNullException.ThrowIfNull(entry);
            maxSerial = Math.Max(maxSerial, entry.Serial);
        }

        return maxSerial;
    }

    private static bool HistoryEntriesStructurallyEqual(HistoryEntry left, HistoryEntry right) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Kind != right.Kind
            || left.Timestamp != right.Timestamp
            || left.Serial != right.Serial
            || left.TokenEstimate != right.TokenEstimate) {
            return false;
        }

        return (left, right) switch {
            (ActionEntry leftAction, ActionEntry rightAction) => ActionEntriesStructurallyEqual(leftAction, rightAction),
            (InjectionEntry leftInjection, InjectionEntry rightInjection) => InjectionEntriesStructurallyEqual(leftInjection, rightInjection),
            (ToolResultsEntry leftToolResults, ToolResultsEntry rightToolResults) => ToolResultsEntriesStructurallyEqual(leftToolResults, rightToolResults),
            (ObservationEntry leftObservation, ObservationEntry rightObservation) => ObservationEntriesStructurallyEqual(leftObservation, rightObservation),
            (RecapEntry leftRecap, RecapEntry rightRecap) => RecapEntriesStructurallyEqual(leftRecap, rightRecap),
            _ => false
        };
    }

    private static bool ActionEntriesStructurallyEqual(ActionEntry left, ActionEntry right) {
        return Equals(left.Invocation, right.Invocation)
            && string.Equals(
                ActionMessageSerialization.SerializeBlocks(left.Message.Blocks),
                ActionMessageSerialization.SerializeBlocks(right.Message.Blocks),
                StringComparison.Ordinal
            );
    }

    private static bool InjectionEntriesStructurallyEqual(InjectionEntry left, InjectionEntry right) {
        return left.BlockKind == right.BlockKind
            && string.Equals(left.Content, right.Content, StringComparison.Ordinal)
            && Equals(left.Source, right.Source);
    }

    private static bool ObservationEntriesStructurallyEqual(ObservationEntry left, ObservationEntry right) {
        return string.Equals(left.Notifications, right.Notifications, StringComparison.Ordinal);
    }

    private static bool ToolResultsEntriesStructurallyEqual(ToolResultsEntry left, ToolResultsEntry right) {
        if (!ObservationEntriesStructurallyEqual(left, right) || left.Results.Count != right.Results.Count) {
            return false;
        }

        for (int index = 0; index < left.Results.Count; index++) {
            if (!ToolCallExecutionResultsStructurallyEqual(left.Results[index], right.Results[index])) {
                return false;
            }
        }

        return true;
    }

    private static bool ToolCallExecutionResultsStructurallyEqual(ToolCallExecutionResult left, ToolCallExecutionResult right) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!Equals(left.RawToolCall, right.RawToolCall)
            || left.ExecuteResult.Status != right.ExecuteResult.Status
            || left.Elapsed != right.Elapsed
            || left.ExecuteResult.Blocks.Count != right.ExecuteResult.Blocks.Count) {
            return false;
        }

        for (int index = 0; index < left.ExecuteResult.Blocks.Count; index++) {
            if (!Equals(left.ExecuteResult.Blocks[index], right.ExecuteResult.Blocks[index])) {
                return false;
            }
        }

        return true;
    }

    private static bool RecapEntriesStructurallyEqual(RecapEntry left, RecapEntry right) {
        return left.InsteadSerial == right.InsteadSerial
            && string.Equals(left.Content, right.Content, StringComparison.Ordinal);
    }

    private void ApplyPendingNotificationsState(IReadOnlyList<string> pendingNotifications) {
        _workingSet.ReplacePendingNotifications(pendingNotifications);
    }

    private void ReloadPendingNotificationsFromWorkspaceSession() {
        var pendingNotifications = _workspaceSession?.LoadPendingNotifications()
            ?? throw new InvalidOperationException("AgentState is not live-bound to a workspace session.");

        ApplyPendingNotificationsState(pendingNotifications);
    }
}
