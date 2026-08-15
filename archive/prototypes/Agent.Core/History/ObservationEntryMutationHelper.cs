using Atelia.Completion.Tools;

namespace Atelia.Agent.Core.History;

internal static class ObservationEntryMutationHelper {
    internal static ObservationEntry CloneWithMergedNotifications(ObservationEntry source, string notifications) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(notifications);

        var clone = source switch {
            ToolResultsEntry toolResultsEntry => CloneToolResultsEntry(toolResultsEntry),
            _ => CloneObservationEntry(source)
        };

        clone.AssignSerial(source.Serial);
        if (source.Notifications is { } existingNotifications) {
            clone.AssignNotifications(existingNotifications);
        }

        clone.MergeNotifications(notifications);
        clone.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(clone));
        return clone;
    }

    private static ObservationEntry CloneObservationEntry(ObservationEntry source) {
        return new ObservationEntry {
            Timestamp = source.Timestamp
        };
    }

    private static ToolResultsEntry CloneToolResultsEntry(ToolResultsEntry source) {
        var clonedResults = new ToolCallExecutionResult[source.Results.Count];
        for (int i = 0; i < source.Results.Count; i++) {
            clonedResults[i] = AgentState.CloneToolCallExecutionResult(source.Results[i]);
        }

        return new ToolResultsEntry(clonedResults) {
            Timestamp = source.Timestamp
        };
    }
}
