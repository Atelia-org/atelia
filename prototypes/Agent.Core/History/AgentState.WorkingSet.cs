using System.Collections.Concurrent;

namespace Atelia.Agent.Core.History;

internal sealed class AgentStateWorkingSet {
    private readonly List<HistoryEntry> _recentHistory = new();
    private readonly ConcurrentQueue<string> _pendingNotifications = new();

    public IReadOnlyList<HistoryEntry> RecentHistory => _recentHistory;

    public ulong LastSerial { get; private set; }

    public bool HasPendingNotifications => !_pendingNotifications.IsEmpty;

    public HistoryEntry[] ExportRecentHistorySnapshot() => _recentHistory.ToArray();

    public string[] ExportPendingNotificationsSnapshot() => _pendingNotifications.ToArray();

    public void ReplaceAll(
        IReadOnlyList<HistoryEntry> recentHistory,
        IReadOnlyList<string> pendingNotifications,
        ulong lastSerial,
        Func<HistoryEntry, HistoryEntry> cloneHistoryEntry
    ) {
        ArgumentNullException.ThrowIfNull(recentHistory);
        ArgumentNullException.ThrowIfNull(pendingNotifications);
        ArgumentNullException.ThrowIfNull(cloneHistoryEntry);

        _recentHistory.Clear();
        while (_pendingNotifications.TryDequeue(out _)) { }

        ulong maxSerial = 0;
        foreach (var sourceEntry in recentHistory) {
            ArgumentNullException.ThrowIfNull(sourceEntry);

            var restoredEntry = cloneHistoryEntry(sourceEntry);
            _recentHistory.Add(restoredEntry);
            maxSerial = Math.Max(maxSerial, restoredEntry.Serial);
        }

        foreach (var notification in pendingNotifications) {
            if (notification is null) {
                throw new InvalidOperationException("Pending notifications must not contain null values.");
            }

            _pendingNotifications.Enqueue(notification);
        }

        LastSerial = Math.Max(lastSerial, maxSerial);
    }

    public ulong AllocateNextSerial() {
        LastSerial = checked(LastSerial + 1);
        return LastSerial;
    }

    public ulong RememberAllocatedSerial(ulong serial) {
        LastSerial = serial;
        return serial;
    }

    public void EnqueuePendingNotification(string notification) {
        ArgumentNullException.ThrowIfNull(notification);
        _pendingNotifications.Enqueue(notification);
    }

    public string[] DrainPendingNotifications() {
        if (_pendingNotifications.IsEmpty) {
            return Array.Empty<string>();
        }

        var drained = new List<string>();
        while (_pendingNotifications.TryDequeue(out var pending)) {
            drained.Add(pending);
        }

        return drained.ToArray();
    }

    public void AppendHistoryEntry(HistoryEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);
        _recentHistory.Add(entry);
    }

    public void ReplacePrefixWithRecap(int splitIndex, RecapEntry recap) {
        ArgumentNullException.ThrowIfNull(recap);

        _recentHistory.RemoveRange(0, splitIndex);
        _recentHistory.Insert(0, recap);
    }
}
