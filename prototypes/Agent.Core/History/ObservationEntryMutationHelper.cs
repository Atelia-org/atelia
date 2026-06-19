namespace Atelia.Agent.Core.History;

internal static class ObservationEntryMutationHelper {
    internal static ObservationEntry CloneWithMergedNotifications(ObservationEntry source, string notifications) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(notifications);

        var clone = new ObservationEntry {
            Timestamp = source.Timestamp
        };

        clone.AssignSerial(source.Serial);
        if (source.Notifications is { } existingNotifications) {
            clone.AssignNotifications(existingNotifications);
        }

        clone.MergeNotifications(notifications);
        clone.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(clone));
        return clone;
    }
}
