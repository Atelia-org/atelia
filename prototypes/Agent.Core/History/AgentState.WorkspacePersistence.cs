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

        var nextSerial = _attachedWorkspaceRoot.AllocateNextHistorySerial();
        _lastSerial = nextSerial;
        return nextSerial;
    }

    private void SyncAttachedWorkspaceAll() {
        if (_attachedWorkspaceRoot is null) { return; }

        _attachedWorkspaceRoot.StampMetadata();
        _attachedWorkspaceRoot.SetSystemPrompt(SystemPrompt);
        _attachedWorkspaceRoot.SetLastSerial(_lastSerial);
        _attachedWorkspaceRoot.SaveHistory(_recentHistory);
        _attachedWorkspaceRoot.SavePendingNotifications(_pendingNotifications.ToArray());
    }

    private void SyncAttachedWorkspaceHistoryAndSerial() {
        if (_attachedWorkspaceRoot is null) { return; }

        _attachedWorkspaceRoot.SaveHistory(_recentHistory);
        _attachedWorkspaceRoot.SetLastSerial(_lastSerial);
    }

    private void SyncAttachedWorkspacePendingNotifications() {
        _attachedWorkspaceRoot?.SavePendingNotifications(_pendingNotifications.ToArray());
    }

    private void SyncAttachedWorkspaceSystemPrompt() {
        if (_attachedWorkspaceRoot is null) { return; }

        _attachedWorkspaceRoot.SetSystemPrompt(SystemPrompt);
    }
}
