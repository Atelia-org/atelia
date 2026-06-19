using Atelia.Agent.Core.Persistence;

namespace Atelia.Agent.Core.History;

public sealed partial class AgentState {
    private AgentWorkspaceSession? _workspaceSession;

    internal void BindWorkspaceSession(AgentWorkspaceSession workspaceSession) {
        ArgumentNullException.ThrowIfNull(workspaceSession);
        EnsureWorkspaceSessionOpen();

        if (_workspaceSession is not null) {
            if (ReferenceEquals(_workspaceSession, workspaceSession)) {
                return;
            }

            throw new InvalidOperationException("AgentState workspace session is already attached.");
        }

        _workspaceSession = workspaceSession;
    }

    private void EnsureWorkspaceSessionOpen() {
        _workspaceSession?.EnsureOpenForState();
    }

    private void ReloadWorkingSetFromWorkspaceSession() {
        var snapshot = _workspaceSession?.LoadStateSnapshot()
            ?? throw new InvalidOperationException("AgentState is not attached to a live workspace session.");

        ApplySnapshot(snapshot);
    }

    private void ReloadRecentHistoryFromWorkspaceSession() {
        var (recentHistory, lastSerial) = _workspaceSession?.LoadRecentHistorySnapshot()
            ?? throw new InvalidOperationException("AgentState is not attached to a live workspace session.");

        ApplyRecentHistorySnapshot(recentHistory, lastSerial);
    }

    private void ApplyRecentHistorySnapshot(
        IReadOnlyList<HistoryEntry> recentHistory,
        ulong lastSerial
    ) {
        _workingSet.ReplaceRecentHistory(recentHistory, lastSerial, CloneHistoryEntry);
    }

    private void ApplyPendingNotificationsSnapshot(IReadOnlyList<string> pendingNotifications) {
        _workingSet.ReplacePendingNotifications(pendingNotifications);
    }

    private void ReloadPendingNotificationsFromWorkspaceSession() {
        var pendingNotifications = _workspaceSession?.LoadPendingNotifications()
            ?? throw new InvalidOperationException("AgentState is not attached to a live workspace session.");

        ApplyPendingNotificationsSnapshot(pendingNotifications);
    }
}
