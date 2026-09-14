using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

internal sealed record ManagerTestHooks(
    Func<RowBuildSpec, RecapCellDraft, Func<RecapGridCellPutResult>,
        RecapGridCellPutResult>? PutCell = null,
    Func<RowBuildSpec, IReadOnlyList<RecapCellArtifact>, Func<RecapGridRowViewPutResult>,
        RecapGridRowViewPutResult>? PutRowView = null,
    Func<FulfilledViewKey, RowResultId,
        Func<RecapGridFulfilledPutResult>,
        RecapGridFulfilledPutResult>? PutFulfilled = null,
    Action? BeforeCaptureRaw = null,
    Func<HistoryTimelineSelectedRow, Func<HistorySegmentOpenResult>,
        HistorySegmentOpenResult>? OpenSelectedSegment = null,
    TimeProvider? TimeProvider = null,
    Action? AfterDiscoverProgression = null
) {
    internal static ManagerTestHooks None { get; } = new();
}
