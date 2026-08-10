namespace Atelia.SessionJournal.HistoryTimeline.Tests;

internal static class HistoryTimelineTestExtensions {
    internal static TimelineHeadRef ReadSnapshotRequired(
        this HistoryTimelineCoordinator coordinator
    ) => Assert.IsType<HistoryTimelineSnapshotResult.Available>(
        coordinator.ReadSnapshot()
    ).Head;

    internal static TimelineHeadRef ReadSnapshotRequired(
        this InMemoryHistoryTimelineLedger ledger
    ) => Assert.IsType<HistoryTimelineStoreReadResult<
        TimelineHeadRef>.Found>(ledger.ReadSnapshot()).Value;

    internal static HistorySegmentDescriptor? ReadRowOrNull(
        this InMemoryHistoryTimelineLedger ledger,
        HistoryRowId rowId
    ) => ledger.ReadRow(rowId) is HistoryTimelineStoreReadResult<
        HistorySegmentDescriptor>.Found found
            ? found.Value
            : null;
}
