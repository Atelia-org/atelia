using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.SessionJournal.RecapGrid.Getter;

public static class RecapGridContextFactory {
    public static RecapGridContextOpenResult Open(
        SessionJournalReadView selectedRef
    ) => OpenCore(selectedRef, GetterTestHooks.None);

    internal static RecapGridContextOpenResult OpenForTest(
        SessionJournalReadView selectedRef,
        GetterTestHooks hooks
    ) => OpenCore(selectedRef, hooks);

    private static RecapGridContextOpenResult OpenCore(
        SessionJournalReadView selectedRef,
        GetterTestHooks hooks
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        ArgumentNullException.ThrowIfNull(hooks);
        hooks.ProvenanceBudget?.Validate();
        string repositoryPath;
        Atelia.EventJournal.RefId refId;
        try {
            repositoryPath = selectedRef.Path;
            refId = selectedRef.BranchRefId;
        }
        catch (ObjectDisposedException) {
            return new RecapGridContextOpenResult.DisposedRawAuthority();
        }

        HistoryTimelineReaderOpenResult timelineOpened =
            HistoryTimelineMaintenance.OpenReader(repositoryPath, refId);
        if (timelineOpened is not HistoryTimelineReaderOpenResult.Opened
                timeline) {
            return timelineOpened switch {
                HistoryTimelineReaderOpenResult.Absent
                    => new RecapGridContextOpenResult.TimelineAbsent(),
                HistoryTimelineReaderOpenResult.Busy
                    => new RecapGridContextOpenResult.Busy(
                        RecapGridContextComponent.Timeline
                    ),
                HistoryTimelineReaderOpenResult.UnsupportedSchema schema
                    => new RecapGridContextOpenResult.UnsupportedSchema(
                        RecapGridContextComponent.Timeline,
                        schema.SchemaVersion
                    ),
                HistoryTimelineReaderOpenResult.Invalid invalid
                    => new RecapGridContextOpenResult.Invalid(
                        RecapGridContextComponent.Timeline,
                        invalid.Code,
                        invalid.Detail
                    ),
                _ => new RecapGridContextOpenResult.Invalid(
                    RecapGridContextComponent.Timeline,
                    "TimelineOpenOutcomeInvalid",
                    "HistoryTimeline returned an unknown open outcome."
                )
            };
        }

        RecapGridControlReaderOpenResult controlOpened =
            RecapGridControlFactory.OpenReader(repositoryPath, refId);
        if (controlOpened is not RecapGridControlReaderOpenResult.Opened
                control) {
            timeline.Handle.Dispose();
            return controlOpened switch {
                RecapGridControlReaderOpenResult.Absent
                    => new RecapGridContextOpenResult.ControlAbsent(),
                RecapGridControlReaderOpenResult.TimelineAbsent
                    => new RecapGridContextOpenResult.TimelineAbsent(),
                RecapGridControlReaderOpenResult.TimelineUnsupportedSchema schema
                    => new RecapGridContextOpenResult.UnsupportedSchema(
                        RecapGridContextComponent.Timeline,
                        schema.SchemaVersion
                    ),
                RecapGridControlReaderOpenResult.Busy
                    => new RecapGridContextOpenResult.Busy(
                        RecapGridContextComponent.Control
                    ),
                RecapGridControlReaderOpenResult.UnsupportedSchema schema
                    => new RecapGridContextOpenResult.UnsupportedSchema(
                        RecapGridContextComponent.Control,
                        schema.SchemaVersion
                    ),
                RecapGridControlReaderOpenResult.Invalid invalid
                    => new RecapGridContextOpenResult.Invalid(
                        RecapGridContextComponent.Control,
                        invalid.Code,
                        invalid.Detail
                    ),
                _ => new RecapGridContextOpenResult.Invalid(
                    RecapGridContextComponent.Control,
                    "ControlOpenOutcomeInvalid",
                    "RecapGrid Control returned an unknown open outcome."
                )
            };
        }

        var lifetime = new GetterLifetime(
            repositoryPath,
            timeline.Handle,
            control.Handle
        );
        return new RecapGridContextOpenResult.Opened(
            new RecapGridContextHandle(
                selectedRef,
                repositoryPath,
                refId,
                lifetime,
                hooks
            )
        );
    }
}
