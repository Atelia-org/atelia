using Atelia.Agent.Core.Persistence;

namespace Atelia.Agent.Core.History;

public sealed partial class AgentState {
    private AgentWorkspaceRoot? _attachedWorkspaceRoot;
    private bool _workspaceSessionClosed;

    internal void AttachRestoredWorkspaceRoot(AgentWorkspaceRoot workspaceRoot) {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        EnsureWorkspaceSessionOpen();

        if (_attachedWorkspaceRoot is not null) {
            if (ReferenceEquals(_attachedWorkspaceRoot.Root, workspaceRoot.Root)) {
                return;
            }

            throw new InvalidOperationException("AgentState workspace root is already attached.");
        }

        _attachedWorkspaceRoot = workspaceRoot;
    }

    internal void CloseWorkspaceSession() {
        _attachedWorkspaceRoot = null;
        _workspaceSessionClosed = true;
    }

    private void EnsureWorkspaceSessionOpen() {
        if (_workspaceSessionClosed) {
            throw new InvalidOperationException("AgentState workspace session has been closed.");
        }
    }

    private ulong AllocateNextSerial() {
        if (_attachedWorkspaceRoot is null) {
            return ++_lastSerial;
        }

        var nextSerial = _attachedWorkspaceRoot.History.AllocateNextSerial();
        _lastSerial = nextSerial;
        return nextSerial;
    }

    private void SyncAttachedWorkspaceHistoryAndSerial() {
        if (_attachedWorkspaceRoot is null) { return; }

        _attachedWorkspaceRoot.History.ReplaceRecent(_recentHistory);
        _attachedWorkspaceRoot.History.SetLastSerial(_lastSerial);
    }

    private void SyncAttachedWorkspaceAppendedHistoryEntry(HistoryEntry entry) {
        if (_attachedWorkspaceRoot is null) { return; }

        _attachedWorkspaceRoot.History.AppendRecent(entry);
    }

    private void SyncAttachedWorkspacePendingNotifications() {
        _attachedWorkspaceRoot?.History.ReplacePendingNotifications(_pendingNotifications.ToArray());
    }

    private void SyncAttachedWorkspaceAppendedNotification(string item) {
        if (_attachedWorkspaceRoot is null) { return; }

        _attachedWorkspaceRoot.History.AppendPendingNotification(item);
    }

    private void SyncAttachedWorkspaceSystemPrompt() {
        if (_attachedWorkspaceRoot is null) { return; }

        _attachedWorkspaceRoot.Meta.SetSystemPrompt(SystemPrompt);
    }
}
