using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Online;

public static class RecapGridOnlineFactory {
    public static RecapGridOnlineOpenResult Open(
        SessionJournalEngine owner,
        IRecapCellBatchExecutor executor,
        RecapGridOnlineLimits? limits = null,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(estimators);
        limits ??= RecapGridOnlineLimits.Production;
        SessionJournalReadView selectedRef;
        try {
            if (owner.IsReadOnly) {
                return new RecapGridOnlineOpenResult.Invalid(
                    RecapGridOnlineComponent.RawAuthority,
                    "MutableSessionJournalRequired",
                    "Online composition requires the mutable SessionJournal owner."
                );
            }
            selectedRef = owner.ReadView;
            _ = selectedRef.ReadCurrentHead();
        }
        catch (ObjectDisposedException) {
            return new RecapGridOnlineOpenResult.DisposedRawAuthority();
        }

        HistoryTimelineHandle? timeline = null;
        RecapGridContextHandle? getter = null;
        try {
            HistoryTimelineOpenResult timelineOpened =
                HistoryTimelineFactory.Open(selectedRef, estimators);
            if (timelineOpened is not HistoryTimelineOpenResult.Opened opened) {
                return MapTimelineOpen(timelineOpened);
            }
            timeline = opened.Handle;

            RecapGridContextOpenResult getterOpened =
                RecapGridContextFactory.Open(selectedRef);
            if (getterOpened is not RecapGridContextOpenResult.Opened available) {
                return MapGetterOpen(getterOpened);
            }
            getter = available.Handle;
            return new RecapGridOnlineOpenResult.Opened(
                new RecapGridOnlineContextHandle(
                    owner,
                    selectedRef,
                    timeline,
                    getter,
                    executor,
                    limits,
                    estimators.ToArray()
                )
            );
        }
        finally {
            if (getter is null) {
                timeline?.Dispose();
            }
        }
    }

    private static RecapGridOnlineOpenResult MapTimelineOpen(
        HistoryTimelineOpenResult result
    ) => result switch {
        HistoryTimelineOpenResult.Absent
            => new RecapGridOnlineOpenResult.Absent(
                RecapGridOnlineComponent.Timeline),
        HistoryTimelineOpenResult.Busy
            => new RecapGridOnlineOpenResult.Busy(
                RecapGridOnlineComponent.Timeline),
        HistoryTimelineOpenResult.UnsupportedSchema value
            => new RecapGridOnlineOpenResult.UnsupportedSchema(
                RecapGridOnlineComponent.Timeline,
                value.SchemaVersion),
        HistoryTimelineOpenResult.Invalid value
            => new RecapGridOnlineOpenResult.Invalid(
                RecapGridOnlineComponent.Timeline,
                value.Code,
                value.Detail),
        _ => new RecapGridOnlineOpenResult.Invalid(
            RecapGridOnlineComponent.Timeline,
            "TimelineOpenOutcomeInvalid",
            "HistoryTimeline returned an unknown open outcome.")
    };

    private static RecapGridOnlineOpenResult MapGetterOpen(
        RecapGridContextOpenResult result
    ) => result switch {
        RecapGridContextOpenResult.TimelineAbsent
            => new RecapGridOnlineOpenResult.Absent(
                RecapGridOnlineComponent.Timeline),
        RecapGridContextOpenResult.ControlAbsent
            => new RecapGridOnlineOpenResult.Absent(
                RecapGridOnlineComponent.Control),
        RecapGridContextOpenResult.Busy value
            => new RecapGridOnlineOpenResult.Busy(Map(value.Component)),
        RecapGridContextOpenResult.UnsupportedSchema value
            => new RecapGridOnlineOpenResult.UnsupportedSchema(
                Map(value.Component), value.SchemaVersion),
        RecapGridContextOpenResult.DisposedRawAuthority
            => new RecapGridOnlineOpenResult.DisposedRawAuthority(),
        RecapGridContextOpenResult.Invalid value
            => new RecapGridOnlineOpenResult.Invalid(
                Map(value.Component), value.Code, value.Detail),
        _ => new RecapGridOnlineOpenResult.Invalid(
            RecapGridOnlineComponent.Getter,
            "GetterOpenOutcomeInvalid",
            "RecapGrid Getter returned an unknown open outcome.")
    };

    internal static RecapGridOnlineComponent Map(
        RecapGridContextComponent component
    ) => component switch {
        RecapGridContextComponent.RawAuthority
            => RecapGridOnlineComponent.RawAuthority,
        RecapGridContextComponent.Timeline
            => RecapGridOnlineComponent.Timeline,
        RecapGridContextComponent.Control
            => RecapGridOnlineComponent.Control,
        RecapGridContextComponent.Store
            => RecapGridOnlineComponent.Store,
        _ => RecapGridOnlineComponent.Getter
    };
}
