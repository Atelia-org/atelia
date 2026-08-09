using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

internal sealed class TestHistoryUnitLoadEstimator
    : IHistoryUnitLoadEstimator {
    public const string DefaultId =
        "atelia.tests.history-load.constant-v1";

    private readonly Func<
        SJ.SessionHistoryPlanningUnit,
        int,
        HistoryUnitLoadMeasurement
    > _measure;

    public TestHistoryUnitLoadEstimator(
        long loadPerUnit = 1,
        string id = DefaultId
    ) : this(
        id,
        (_, _) => new HistoryUnitLoadMeasurement(
            new HistoryLoadUnit(loadPerUnit),
            RenderedUtf8Bytes: 1
        )
    ) {
    }

    public TestHistoryUnitLoadEstimator(
        string id,
        Func<
            SJ.SessionHistoryPlanningUnit,
            int,
            HistoryUnitLoadMeasurement
        > measure
    ) {
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException(
                "Estimator ID cannot be empty.",
                nameof(id)
            )
            : id;
        _measure = measure
            ?? throw new ArgumentNullException(nameof(measure));
    }

    public string Id { get; }
    public int MeasureCallCount { get; private set; }

    public HistoryUnitLoadMeasurement Measure(
        SJ.SessionHistoryPlanningUnit unit,
        int maxRenderedUtf8Bytes
    ) {
        MeasureCallCount++;
        return _measure(unit, maxRenderedUtf8Bytes);
    }
}
