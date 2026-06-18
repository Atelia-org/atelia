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
            return ++_lastSerial;
        }

        var nextSerial = _workspaceSession.AllocateNextSerial();
        _lastSerial = nextSerial;
        return nextSerial;
    }

    private void SyncSessionHistoryAndSerial() {
        if (_workspaceSession is null) { return; }

        _workspaceSession.ReplaceRecentHistory(_recentHistory, _lastSerial);
    }

    private void SyncSessionAppendedHistoryEntry(HistoryEntry entry) {
        if (_workspaceSession is null) { return; }

        _workspaceSession.AppendHistoryEntry(entry);
    }

    private void SyncSessionPendingNotifications() {
        _workspaceSession?.ReplacePendingNotifications(_pendingNotifications.ToArray());
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
