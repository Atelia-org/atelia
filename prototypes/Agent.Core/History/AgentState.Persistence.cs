using Atelia.Agent.Core.Persistence;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.Agent.Core.History;

public sealed partial class AgentState {
    internal AgentStateSnapshot ExportSnapshot() {
        return new AgentStateSnapshot(
            SystemPrompt: SystemPrompt,
            RecentHistory: _recentHistory.ToArray(),
            PendingNotifications: _pendingNotifications.ToArray(),
            LastSerial: _lastSerial
        );
    }

    internal static AgentState RestoreSnapshot(AgentStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        return RestoreCore(
            snapshot.SystemPrompt,
            snapshot.RecentHistory,
            snapshot.PendingNotifications,
            snapshot.LastSerial
        );
    }

    internal static AgentState RestoreFromWorkspaceRoot(AgentWorkspaceRoot workspaceRoot) {
        ArgumentNullException.ThrowIfNull(workspaceRoot);

        return RestoreCore(
            workspaceRoot.Meta.GetRequiredSystemPrompt(),
            workspaceRoot.History.LoadRecent(),
            workspaceRoot.History.LoadPendingNotifications(),
            workspaceRoot.Meta.GetRequiredLastSerial()
        );
    }

    private static AgentState RestoreCore(
        string systemPrompt,
        IReadOnlyList<HistoryEntry> recentHistory,
        IReadOnlyList<string> pendingNotifications,
        ulong lastSerial
    ) {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(recentHistory);
        ArgumentNullException.ThrowIfNull(pendingNotifications);

        var state = new AgentState(systemPrompt);
        ulong maxSerial = 0;

        foreach (var sourceEntry in recentHistory) {
            ArgumentNullException.ThrowIfNull(sourceEntry);

            var restoredEntry = CloneHistoryEntry(sourceEntry);
            state._recentHistory.Add(restoredEntry);
            maxSerial = Math.Max(maxSerial, restoredEntry.Serial);
        }

        foreach (var notification in pendingNotifications) {
            if (notification is null) { throw new InvalidOperationException("Pending notifications must not contain null values."); }

            state._pendingNotifications.Enqueue(notification);
        }

        state._lastSerial = Math.Max(lastSerial, maxSerial);
        return state;
    }

    private static HistoryEntry CloneHistoryEntry(HistoryEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);

        HistoryEntry cloned = entry switch {
            ActionEntry actionEntry => CloneActionEntry(actionEntry),
            InjectionEntry injectionEntry => CloneInjectionEntry(injectionEntry),
            ToolResultsEntry toolResultsEntry => CloneToolResultsEntry(toolResultsEntry),
            ObservationEntry observationEntry => CloneObservationEntry(observationEntry),
            RecapEntry recapEntry => CloneRecapEntry(recapEntry),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unsupported history entry kind.")
        };

        cloned.AssignTokenEstimate(entry.TokenEstimate);
        cloned.AssignSerial(entry.Serial);
        return cloned;
    }

    private static ActionEntry CloneActionEntry(ActionEntry entry) {
        return new ActionEntry(
            Message: new ActionMessage(entry.Message.Blocks.ToArray()),
            Invocation: entry.Invocation
        ) {
            Timestamp = entry.Timestamp
        };
    }

    private static InjectionEntry CloneInjectionEntry(InjectionEntry entry) {
        return new InjectionEntry(
            content: entry.Content,
            blockKind: entry.BlockKind,
            source: entry.Source
        ) {
            Timestamp = entry.Timestamp
        };
    }

    private static ObservationEntry CloneObservationEntry(ObservationEntry entry) {
        var clone = new ObservationEntry {
            Timestamp = entry.Timestamp
        };

        if (entry.Notifications is not null) {
            clone.AssignNotifications(entry.Notifications);
        }

        return clone;
    }

    private static ToolResultsEntry CloneToolResultsEntry(ToolResultsEntry entry) {
        var clonedResults = new ToolCallExecutionResult[entry.Results.Count];
        for (int i = 0; i < entry.Results.Count; i++) {
            clonedResults[i] = CloneToolCallExecutionResult(entry.Results[i]);
        }

        var clone = new ToolResultsEntry(clonedResults) {
            Timestamp = entry.Timestamp
        };

        if (entry.Notifications is not null) {
            clone.AssignNotifications(entry.Notifications);
        }

        return clone;
    }

    private static RecapEntry CloneRecapEntry(RecapEntry entry) {
        return new RecapEntry(entry.Content, entry.InsteadSerial) {
            Timestamp = entry.Timestamp
        };
    }

    internal static ToolCallExecutionResult CloneToolCallExecutionResult(ToolCallExecutionResult result) {
        ArgumentNullException.ThrowIfNull(result);

        return new ToolCallExecutionResult(
            rawToolCall: new RawToolCall(
                result.RawToolCall.ToolName,
                result.RawToolCall.ToolCallId,
                result.RawToolCall.RawArgumentsJson
            ),
            executeResult: new ToolExecuteResult(
                result.ExecuteResult.Status,
                result.ExecuteResult.Blocks.ToArray()
            ),
            elapsed: result.Elapsed ?? default
        );
    }
}
