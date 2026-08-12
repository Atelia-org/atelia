using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.SessionJournal.RecapGrid.Getter;

public static class RecapGridContextFactory {
    public static RecapGridContextOpenResult Open(
        SessionJournalReadView selectedRef,
        params IHistoryUnitLoadEstimator[] estimators
    ) => OpenCore(selectedRef, GetterTestHooks.None, estimators);

    internal static RecapGridContextOpenResult OpenForTest(
        SessionJournalReadView selectedRef,
        GetterTestHooks hooks,
        params IHistoryUnitLoadEstimator[] estimators
    ) => OpenCore(selectedRef, hooks, estimators);

    private static RecapGridContextOpenResult OpenCore(
        SessionJournalReadView selectedRef,
        GetterTestHooks hooks,
        IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(estimators);
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

        HistoryTimelineBuildReadSessionOpenResult timelineOpened =
            HistoryTimelineFactory.OpenBuildReadSession(selectedRef, estimators);
        if (timelineOpened is not HistoryTimelineBuildReadSessionOpenResult.Opened
                timeline) {
            return timelineOpened switch {
                HistoryTimelineBuildReadSessionOpenResult.Absent
                    => new RecapGridContextOpenResult.TimelineAbsent(),
                HistoryTimelineBuildReadSessionOpenResult.Busy
                    => new RecapGridContextOpenResult.Busy(
                        RecapGridContextComponent.Timeline
                    ),
                HistoryTimelineBuildReadSessionOpenResult.UnsupportedSchema schema
                    => new RecapGridContextOpenResult.UnsupportedSchema(
                        RecapGridContextComponent.Timeline,
                        schema.SchemaVersion
                    ),
                HistoryTimelineBuildReadSessionOpenResult.Invalid invalid
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

        RecapGridCadenceReaderOpenResult cadenceOpened =
            RecapGridCadenceFactory.OpenReader(selectedRef);
        if (cadenceOpened is not RecapGridCadenceReaderOpenResult.Opened
                cadence) {
            timeline.Session.Dispose();
            return cadenceOpened switch {
                RecapGridCadenceReaderOpenResult.Absent
                    => new RecapGridContextOpenResult.CadenceAbsent(),
                RecapGridCadenceReaderOpenResult.Busy
                    => new RecapGridContextOpenResult.Busy(
                        RecapGridContextComponent.Cadence),
                RecapGridCadenceReaderOpenResult.UnsupportedSchema schema
                    => new RecapGridContextOpenResult.UnsupportedSchema(
                        RecapGridContextComponent.Cadence, schema.Version),
                RecapGridCadenceReaderOpenResult.PlatformUnsupported
                    => new RecapGridContextOpenResult.Invalid(
                        RecapGridContextComponent.Cadence,
                        "CadencePlatformUnsupported",
                        "The RecapGrid Cadence platform is unsupported."),
                RecapGridCadenceReaderOpenResult.Invalid invalid
                    => new RecapGridContextOpenResult.Invalid(
                        RecapGridContextComponent.Cadence,
                        invalid.Code,
                        invalid.Detail),
                _ => new RecapGridContextOpenResult.Invalid(
                    RecapGridContextComponent.Cadence,
                    "CadenceOpenOutcomeInvalid",
                    "RecapGrid Cadence returned an unknown open outcome.")
            };
        }

        RecapGridControlReaderOpenResult controlOpened =
            RecapGridControlFactory.OpenReader(repositoryPath, refId);
        if (controlOpened is not RecapGridControlReaderOpenResult.Opened
                control) {
            cadence.Handle.Dispose();
            timeline.Session.Dispose();
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
            timeline.Session,
            cadence.Handle,
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
