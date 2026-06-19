using Atelia.Agent.Core.Persistence;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.Agent.Core.History;

public sealed partial class AgentState {
    internal AgentStateSnapshot ExportSnapshot() {
        return new AgentStateSnapshot(
            SystemPrompt: SystemPrompt,
            RecentHistory: _workingSet.ExportRecentHistorySnapshot(),
            PendingNotifications: _workingSet.ExportPendingNotificationsSnapshot(),
            LastSerial: _workingSet.LastSerial
        );
    }

    internal static AgentState RestoreSnapshot(AgentStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        var state = new AgentState(snapshot.SystemPrompt);
        state.ApplySnapshot(snapshot);
        return state;
    }

    private void ApplySnapshot(AgentStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        SystemPrompt = snapshot.SystemPrompt;
        ReplaceWorkingSet(snapshot.RecentHistory, snapshot.PendingNotifications, snapshot.LastSerial);
    }

    private void ReplaceWorkingSet(
        IReadOnlyList<HistoryEntry> recentHistory,
        IReadOnlyList<string> pendingNotifications,
        ulong lastSerial
    ) {
        _workingSet.ReplaceAll(
            recentHistory,
            pendingNotifications,
            lastSerial,
            CloneHistoryEntry
        );
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
