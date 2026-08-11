using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

internal sealed record ManagerTestHooks(
    Func<RecapCellArtifact, Func<RecapGridCellPutResult>,
        RecapGridCellPutResult>? PutCell = null,
    Func<RowBuildSpec, RecapRowView, Func<RecapGridRowViewPutResult>,
        RecapGridRowViewPutResult>? PutRowView = null,
    Func<FulfilledViewKey, RowViewDigest,
        Func<RecapGridFulfilledPutResult>,
        RecapGridFulfilledPutResult>? PutFulfilled = null,
    Action? BeforeCaptureRaw = null,
    Func<HistoryTimelineSelectedRow, Func<HistorySegmentOpenResult>,
        HistorySegmentOpenResult>? OpenSelectedSegment = null,
    TimeProvider? TimeProvider = null
) {
    internal static ManagerTestHooks None { get; } = new();
}
