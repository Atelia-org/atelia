using Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

internal static class HistoryTimelineTestMutationExtensions {
    internal static HistoryTimelinePlanResult PlanNextRow(
        this HistoryTimelineCoordinator coordinator,
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture,
        CancellationToken cancellationToken = default
    ) => coordinator.PlanNextRowForTests(
        expectedWholeHead,
        capture,
        cancellationToken);

    internal static HistoryTimelineOfflineBuilderOpenResult OpenOfflineBuilder(
        this HistoryTimelineCoordinator coordinator,
        TimelineHeadRef expectedWholeHead,
        SessionSelectedLineageForwardCursor cursor
    ) => coordinator.OpenOfflineBuilderForTests(
        expectedWholeHead,
        cursor);
}
