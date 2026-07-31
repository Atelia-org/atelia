using SJ = Atelia.SessionJournal;
using Atelia.EventJournal;

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

internal static class TestHistoryLoadMeasurement {
    public static RecapHistoryLoadMeasurement UnitCountEquivalent(
        RecapHistoryWindowFacts window,
        EventAddress baselineAddress,
        string estimatorId =
            TestHistoryUnitLoadEstimator.DefaultId
    ) {
        int baselineCompletedUnitCount = 0;
        int firstLaterBoundaryIndex = 0;
        if (baselineAddress != window.StartExclusive) {
            firstLaterBoundaryIndex = -1;
            for (int index = 0;
                 index < window.ReplaySafeBoundaries.Count;
                 index++) {
                SessionHistoryPlanningBoundary boundary =
                    window.ReplaySafeBoundaries[index];
                if (boundary.Address != baselineAddress) {
                    continue;
                }
                baselineCompletedUnitCount =
                    boundary.CompletedUnitCount;
                firstLaterBoundaryIndex = index + 1;
                break;
            }
            if (firstLaterBoundaryIndex < 0) {
                throw new ArgumentException(
                    "Baseline must be the exact window start or a "
                    + "replay-safe boundary.",
                    nameof(baselineAddress)
                );
            }
        }

        return new RecapHistoryLoadMeasurement(
            estimatorId,
            baselineAddress,
            baselineCompletedUnitCount,
            new HistoryLoadUnit(
                window.TotalHistoryUnitCount
                - baselineCompletedUnitCount
            ),
            renderedUtf8Bytes:
                window.TotalHistoryUnitCount
                - baselineCompletedUnitCount,
            [
                .. window.ReplaySafeBoundaries
                    .Skip(firstLaterBoundaryIndex)
                    .Select(boundary =>
                        new RecapHistoryLoadBoundary(
                            boundary.Address,
                            boundary.CompletedUnitCount
                            - baselineCompletedUnitCount,
                            new HistoryLoadUnit(
                                boundary.CompletedUnitCount
                                - baselineCompletedUnitCount
                            )
                        ))
            ]
        );
    }
}
