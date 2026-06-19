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

    private ulong AllocateNextSerial() {
        if (_workspaceSession is null) {
            return _workingSet.AllocateNextSerial();
        }

        var nextSerial = _workspaceSession.AllocateNextSerial();
        return _workingSet.RememberAllocatedSerial(nextSerial);
    }

    private void SyncSessionAppendedHistoryEntry(HistoryEntry entry) {
        if (_workspaceSession is null) { return; }

        _workspaceSession.AppendHistoryEntry(entry);
    }

    private void ReloadWorkingSetFromWorkspaceSession() {
        var snapshot = _workspaceSession?.LoadStateSnapshot()
            ?? throw new InvalidOperationException("AgentState is not attached to a live workspace session.");

        ApplySnapshot(snapshot);
    }

    private void ReloadRecentHistoryFromWorkspaceSession() {
        var (recentHistory, lastSerial) = _workspaceSession?.LoadRecentHistorySnapshot()
            ?? throw new InvalidOperationException("AgentState is not attached to a live workspace session.");

        _workingSet.ReplaceRecentHistory(recentHistory, lastSerial, CloneHistoryEntry);
    }

    private void ReloadPendingNotificationsFromWorkspaceSession() {
        var pendingNotifications = _workspaceSession?.LoadPendingNotifications()
            ?? throw new InvalidOperationException("AgentState is not attached to a live workspace session.");

        _workingSet.ReplacePendingNotifications(pendingNotifications);
    }

    private void SyncSessionAppendedNotification(string item) {
        if (_workspaceSession is null) { return; }

        _workspaceSession.AppendPendingNotification(item);
    }

    private void SyncSessionSystemPrompt() {
        if (_workspaceSession is null) { return; }

        _workspaceSession.SetSystemPrompt(SystemPrompt);
    }
}
