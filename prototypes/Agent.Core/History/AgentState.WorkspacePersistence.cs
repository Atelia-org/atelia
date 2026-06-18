using Atelia.Agent.Core.Persistence;

namespace Atelia.Agent.Core.History;

public sealed partial class AgentState {
    private AgentWorkspaceRoot? _attachedWorkspaceRoot;

    internal void AttachWorkspaceRoot(AgentWorkspaceRoot workspaceRoot, bool syncExistingState = true) {
        ArgumentNullException.ThrowIfNull(workspaceRoot);

        if (_attachedWorkspaceRoot is not null) {
            if (ReferenceEquals(_attachedWorkspaceRoot.Root, workspaceRoot.Root)) {
                return;
            }

            throw new InvalidOperationException("AgentState workspace root is already attached.");
        }

        _attachedWorkspaceRoot = workspaceRoot;
        if (syncExistingState) {
            SyncAttachedWorkspaceAll();
        }
    }

    internal void DetachWorkspaceRoot() {
        _attachedWorkspaceRoot = null;
    }

    internal bool IsAttachedToWorkspaceRoot(AgentWorkspaceRoot workspaceRoot) {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        return ReferenceEquals(_attachedWorkspaceRoot?.Root, workspaceRoot.Root);
    }

    private ulong AllocateNextSerial() {
        if (_attachedWorkspaceRoot is null) {
            return ++_lastSerial;
        }

        var nextSerial = _attachedWorkspaceRoot.History.AllocateNextSerial();
        _lastSerial = nextSerial;
        return nextSerial;
    }

    private void SyncAttachedWorkspaceAll() {
        if (_attachedWorkspaceRoot is null) { return; }

        _attachedWorkspaceRoot.Meta.Stamp();
        _attachedWorkspaceRoot.Meta.SetSystemPrompt(SystemPrompt);
        _attachedWorkspaceRoot.History.SetLastSerial(_lastSerial);
        _attachedWorkspaceRoot.History.ReplaceRecent(_recentHistory);
        _attachedWorkspaceRoot.History.ReplacePendingNotifications(_pendingNotifications.ToArray());
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
