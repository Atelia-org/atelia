using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public static class RecapGridManagerFactory {
    public static RecapGridManagerOpenResult Open(
        SJ.SessionJournalReadView selectedRef,
        params IHistoryUnitLoadEstimator[] estimators
    ) => OpenCore(selectedRef, ManagerTestHooks.None, estimators);

    internal static RecapGridManagerOpenResult OpenForTest(
        SJ.SessionJournalReadView selectedRef,
        ManagerTestHooks testHooks,
        params IHistoryUnitLoadEstimator[] estimators
    ) => OpenCore(selectedRef, testHooks, estimators);

    private static RecapGridManagerOpenResult OpenCore(
        SJ.SessionJournalReadView selectedRef,
        ManagerTestHooks testHooks,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        ArgumentNullException.ThrowIfNull(testHooks);
        ArgumentNullException.ThrowIfNull(estimators);
        HistoryTimelineBuildReadSession? timeline = null;
        RecapGridControlReaderHandle? control = null;
        RecapGridStoreHandle? store = null;
        try {
            HistoryTimelineBuildReadSessionOpenResult timelineResult =
                HistoryTimelineFactory.OpenBuildReadSession(
                    selectedRef,
                    estimators
                );
            switch (timelineResult) {
                case HistoryTimelineBuildReadSessionOpenResult.Opened opened:
                    timeline = opened.Session;
                    break;
                case HistoryTimelineBuildReadSessionOpenResult.Absent:
                    return new RecapGridManagerOpenResult.Absent(
                        RecapGridBuildDependency.Timeline
                    );
                case HistoryTimelineBuildReadSessionOpenResult.Busy:
                    return new RecapGridManagerOpenResult.Busy(
                        RecapGridBuildDependency.Timeline
                    );
                case HistoryTimelineBuildReadSessionOpenResult
                    .UnsupportedSchema unsupported:
                    return new RecapGridManagerOpenResult.UnsupportedSchema(
                        RecapGridBuildDependency.Timeline,
                        unsupported.SchemaVersion
                    );
                case HistoryTimelineBuildReadSessionOpenResult.Invalid invalid:
                    return new RecapGridManagerOpenResult.Invalid(
                        RecapGridBuildDependency.Timeline,
                        invalid.Code,
                        invalid.Detail
                    );
                default:
                    return Invalid(
                        RecapGridBuildDependency.Timeline,
                        "TimelineOpenOutcomeInvalid"
                    );
            }

            RecapGridControlReaderOpenResult controlResult =
                RecapGridControlFactory.OpenReader(
                    selectedRef.Path,
                    selectedRef.BranchRefId
                );
            switch (controlResult) {
                case RecapGridControlReaderOpenResult.Opened opened:
                    control = opened.Handle;
                    break;
                case RecapGridControlReaderOpenResult.Absent:
                case RecapGridControlReaderOpenResult.TimelineAbsent:
                    return new RecapGridManagerOpenResult.Absent(
                        RecapGridBuildDependency.Control
                    );
                case RecapGridControlReaderOpenResult.Busy:
                    return new RecapGridManagerOpenResult.Busy(
                        RecapGridBuildDependency.Control
                    );
                case RecapGridControlReaderOpenResult
                    .TimelineUnsupportedSchema unsupported:
                    return new RecapGridManagerOpenResult.UnsupportedSchema(
                        RecapGridBuildDependency.Timeline,
                        unsupported.SchemaVersion
                    );
                case RecapGridControlReaderOpenResult
                    .UnsupportedSchema unsupported:
                    return new RecapGridManagerOpenResult.UnsupportedSchema(
                        RecapGridBuildDependency.Control,
                        unsupported.SchemaVersion
                    );
                case RecapGridControlReaderOpenResult.Invalid invalid:
                    return new RecapGridManagerOpenResult.Invalid(
                        RecapGridBuildDependency.Control,
                        invalid.Code,
                        invalid.Detail
                    );
                default:
                    return Invalid(
                        RecapGridBuildDependency.Control,
                        "ControlOpenOutcomeInvalid"
                    );
            }

            RecapGridStoreOpenResult storeResult =
                RecapGridStoreFactory.Open(selectedRef.Path);
            switch (storeResult) {
                case RecapGridStoreOpenResult.Opened opened:
                    store = opened.Handle;
                    break;
                case RecapGridStoreOpenResult.Absent:
                    return new RecapGridManagerOpenResult.Absent(
                        RecapGridBuildDependency.Store
                    );
                case RecapGridStoreOpenResult.Busy:
                    return new RecapGridManagerOpenResult.Busy(
                        RecapGridBuildDependency.Store
                    );
                case RecapGridStoreOpenResult.UnsupportedSchema unsupported:
                    return new RecapGridManagerOpenResult.UnsupportedSchema(
                        RecapGridBuildDependency.Store,
                        unsupported.SchemaVersion
                    );
                case RecapGridStoreOpenResult.PlatformUnsupported:
                    return new RecapGridManagerOpenResult.PlatformUnsupported(
                        RecapGridBuildDependency.Store
                    );
                case RecapGridStoreOpenResult.Invalid invalid:
                    return new RecapGridManagerOpenResult.Invalid(
                        RecapGridBuildDependency.Store,
                        invalid.Code,
                        invalid.Detail
                    );
                default:
                    return Invalid(
                        RecapGridBuildDependency.Store,
                        "StoreOpenOutcomeInvalid"
                    );
            }

            var lifetime = new ManagerLifetime(
                store,
                control,
                timeline
            );
            var handle = new RecapGridManagerHandle(
                timeline,
                control,
                store,
                lifetime,
                testHooks.TimeProvider ?? TimeProvider.System,
                testHooks
            );
            timeline = null;
            control = null;
            store = null;
            return new RecapGridManagerOpenResult.Opened(handle);
        }
        finally {
            store?.Dispose();
            control?.Dispose();
            timeline?.Dispose();
        }
    }

    private static RecapGridManagerOpenResult.Invalid Invalid(
        RecapGridBuildDependency dependency,
        string code
    ) => new(
        dependency,
        code,
        "A dependency factory returned an unknown outcome."
    );
}

public sealed class RecapGridManagerHandle : IDisposable {
    private readonly ManagerLifetime _lifetime;

    internal RecapGridManagerHandle(
        HistoryTimelineBuildReadSession timeline,
        RecapGridControlReaderHandle control,
        RecapGridStoreHandle store,
        ManagerLifetime lifetime,
        TimeProvider timeProvider,
        ManagerTestHooks testHooks
    ) {
        Manager = new RecapGridManager(
            timeline,
            control,
            store,
            lifetime,
            timeProvider,
            testHooks
        );
        _lifetime = lifetime;
    }

    public RecapGridManager Manager { get; }

    public void Dispose() => _lifetime.Dispose();
}
