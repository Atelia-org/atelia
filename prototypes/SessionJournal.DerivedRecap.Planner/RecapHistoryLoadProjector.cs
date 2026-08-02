using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Projects exact replay-safe SessionJournal boundaries onto additive H0
/// HistoryLoad values without changing production cadence behavior.
/// </summary>
public static class RecapHistoryLoadProjector {
    public static RecapHistoryLoadMeasurement Measure(
        SJ.SessionHistoryPlanningWindow window,
        EventAddress baselineAddress,
        IHistoryUnitLoadEstimator estimator
    ) {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(estimator);
        string estimatorId;
        try {
            estimatorId = estimator.Id;
        }
        catch (Exception exception) when (
            RecapNonFatalException.IsCatchable(exception)
        ) {
            throw Invalid(
                HistoryLoadMeasurementDefectCodes.EstimatorFailed,
                "History-load estimator ID could not be read.",
                exception
            );
        }
        if (string.IsNullOrWhiteSpace(estimatorId)) {
            throw Invalid(
                HistoryLoadMeasurementDefectCodes.MeasurementInvalid,
                "History-load estimator ID cannot be empty."
            );
        }

        ValidateWindowCollections(window);
        RecapHistoryLoadBaseline baseline =
            RecapHistoryLoadBaselineResolver.Resolve(
                window.StartExclusive,
                window.Units.Count,
                window.ReplaySafeBoundaries,
                baselineAddress
            );
        int baselineCompletedUnitCount =
            baseline.CompletedUnitCount;
        int firstOutputBoundaryIndex =
            baseline.FirstLaterBoundaryIndex;
        ValidateWindowShape(window);

        int suffixUnitCount =
            window.Units.Count - baselineCompletedUnitCount;
        var loadPrefix = new long[suffixUnitCount + 1];
        int renderedWindowBytes = 0;
        for (int suffixIndex = 0;
             suffixIndex < suffixUnitCount;
             suffixIndex++) {
            SJ.SessionHistoryPlanningUnit unit =
                window.Units[
                    baselineCompletedUnitCount + suffixIndex
                ] ?? throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .PlanningWindowInvalid,
                    "Planning window contains a null HistoryUnit."
                );
            HistoryUnitLoadMeasurement measured =
                MeasureUnit(estimator, unit);
            if (measured.Load.Value < 1) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .MeasurementInvalid,
                    "History-load estimator returned a load below one."
                );
            }
            if (measured.RenderedUtf8Bytes < 0
                || measured.RenderedUtf8Bytes
                    > HistoryLoadMeasurementSafety.V1
                        .MaxRenderedHistoryUnitUtf8Bytes) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .MeasurementInvalid,
                    "History-load estimator returned an invalid "
                    + "rendered UTF-8 byte count."
                );
            }
            try {
                loadPrefix[suffixIndex + 1] = checked(
                    loadPrefix[suffixIndex]
                    + measured.Load.Value
                );
                renderedWindowBytes = checked(
                    renderedWindowBytes
                    + measured.RenderedUtf8Bytes
                );
            }
            catch (OverflowException exception) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .MeasurementOverflow,
                    "Baseline-relative HistoryLoad aggregation "
                    + "overflowed.",
                    exception
                );
            }
            if (renderedWindowBytes
                > HistoryLoadMeasurementSafety.V1
                    .MaxBaselineRelativeWindowUtf8Bytes) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .HistoryLoadInputTooLarge,
                    "Baseline-relative HistoryUnit rendering exceeds "
                    + $"{HistoryLoadMeasurementSafety.V1.MaxBaselineRelativeWindowUtf8Bytes} "
                    + "UTF-8 bytes."
                );
            }
        }

        var projected = new List<RecapHistoryLoadBoundary>();
        for (int index = firstOutputBoundaryIndex;
             index < window.ReplaySafeBoundaries.Count;
             index++) {
            SJ.SessionHistoryPlanningBoundary boundary =
                window.ReplaySafeBoundaries[index];
            int relativeUnitCount;
            try {
                relativeUnitCount = checked(
                    boundary.CompletedUnitCount
                    - baselineCompletedUnitCount
                );
            }
            catch (OverflowException exception) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .MeasurementOverflow,
                    "Replay-safe boundary unit-count projection "
                    + "overflowed.",
                    exception
                );
            }
            if (relativeUnitCount < 0
                || relativeUnitCount >= loadPrefix.Length) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .PlanningWindowInvalid,
                    $"Replay-safe boundary '{boundary.Address}' has "
                    + "an invalid completed-unit count."
                );
            }
            projected.Add(new RecapHistoryLoadBoundary(
                boundary.Address,
                relativeUnitCount,
                new HistoryLoadUnit(loadPrefix[relativeUnitCount])
            ));
        }

        long growth = loadPrefix[^1];
        if (projected.Any(boundary =>
                boundary.AbsorbedSinceBaseline.Value > growth)) {
            throw Invalid(
                HistoryLoadMeasurementDefectCodes
                    .PlanningWindowInvalid,
                "A projected replay-safe boundary exceeds total "
                + "baseline-relative HistoryLoad."
            );
        }
        return new RecapHistoryLoadMeasurement(
            estimatorId,
            baselineAddress,
            baselineCompletedUnitCount,
            new HistoryLoadUnit(growth),
            renderedWindowBytes,
            projected
        );
    }

    private static HistoryUnitLoadMeasurement MeasureUnit(
        IHistoryUnitLoadEstimator estimator,
        SJ.SessionHistoryPlanningUnit unit
    ) {
        try {
            return estimator.Measure(
                    unit,
                    HistoryLoadMeasurementSafety.V1
                        .MaxRenderedHistoryUnitUtf8Bytes
                )
                ?? throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .MeasurementInvalid,
                    "History-load estimator returned null."
                );
        }
        catch (HistoryLoadMeasurementException) {
            throw;
        }
        catch (OverflowException exception) {
            throw Invalid(
                HistoryLoadMeasurementDefectCodes
                    .MeasurementOverflow,
                "History-load estimator overflowed.",
                exception
            );
        }
        catch (Exception exception) when (
            RecapNonFatalException.IsCatchable(exception)
        ) {
            throw Invalid(
                HistoryLoadMeasurementDefectCodes.EstimatorFailed,
                "History-load estimator failed.",
                exception
            );
        }
    }

    private static void ValidateWindowShape(
        SJ.SessionHistoryPlanningWindow window
    ) {
        var rawPositions = new Dictionary<EventAddress, int>();
        for (int index = 0;
             index < window.RawAddresses.Count;
             index++) {
            EventAddress address = window.RawAddresses[index];
            if (!rawPositions.TryAdd(address, index)) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .PlanningWindowInvalid,
                    $"Planning raw range repeats address "
                    + $"'{address}'."
                );
            }
        }

        var boundaryAddresses = new HashSet<EventAddress>();
        int previousRawPosition = -1;
        int previousCompletedUnitCount = 0;
        foreach (SJ.SessionHistoryPlanningBoundary? boundary
                 in window.ReplaySafeBoundaries) {
            if (boundary is null
                || !boundaryAddresses.Add(boundary.Address)
                || !rawPositions.TryGetValue(
                    boundary.Address,
                    out int rawPosition
                )
                || rawPosition <= previousRawPosition
                || boundary.CompletedUnitCount < 0
                || boundary.CompletedUnitCount
                    > window.Units.Count
                || boundary.CompletedUnitCount
                    < previousCompletedUnitCount) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .PlanningWindowInvalid,
                    "Planning window has malformed replay-safe "
                    + "boundary order or completed-unit counts."
                );
            }
            previousRawPosition = rawPosition;
            previousCompletedUnitCount =
                boundary.CompletedUnitCount;
        }
    }

    private static void ValidateWindowCollections(
        SJ.SessionHistoryPlanningWindow window
    ) {
        if (window.Units is null
            || window.RawAddresses is null
            || window.ReplaySafeBoundaries is null) {
            throw Invalid(
                HistoryLoadMeasurementDefectCodes
                    .PlanningWindowInvalid,
                "Planning window collections cannot be null."
            );
        }
    }

    private static HistoryLoadMeasurementException Invalid(
        string code,
        string detail,
        Exception? innerException = null
    ) => new(code, detail, innerException);

}

internal sealed record RecapHistoryLoadBaseline(
    EventAddress Address,
    int CompletedUnitCount,
    int FirstLaterBoundaryIndex
);

internal static class RecapHistoryLoadBaselineResolver {
    internal static RecapHistoryLoadBaseline Resolve(
        EventAddress startExclusive,
        int totalHistoryUnitCount,
        IReadOnlyList<SJ.SessionHistoryPlanningBoundary>
            replaySafeBoundaries,
        EventAddress baselineAddress
    ) {
        if (totalHistoryUnitCount < 0
            || replaySafeBoundaries is null) {
            throw Invalid(
                HistoryLoadMeasurementDefectCodes
                    .PlanningWindowInvalid,
                "Planning window unit count or replay-safe boundaries "
                + "are invalid."
            );
        }
        if (baselineAddress == startExclusive) {
            return new RecapHistoryLoadBaseline(
                baselineAddress,
                CompletedUnitCount: 0,
                FirstLaterBoundaryIndex: 0
            );
        }

        int matchIndex = -1;
        for (int index = 0;
             index < replaySafeBoundaries.Count;
             index++) {
            SJ.SessionHistoryPlanningBoundary? boundary =
                replaySafeBoundaries[index];
            if (boundary is null) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .PlanningWindowInvalid,
                    "Planning window contains a null replay-safe "
                    + "boundary."
                );
            }
            if (boundary.Address != baselineAddress) {
                continue;
            }
            if (matchIndex >= 0) {
                throw BaselineInvalid(
                    baselineAddress,
                    "appears more than once"
                );
            }
            matchIndex = index;
        }
        if (matchIndex < 0) {
            throw BaselineInvalid(
                baselineAddress,
                "is not an exact replay-safe boundary"
            );
        }

        int completedUnitCount =
            replaySafeBoundaries[matchIndex]
                .CompletedUnitCount;
        if (completedUnitCount < 0
            || completedUnitCount > totalHistoryUnitCount) {
            throw BaselineInvalid(
                baselineAddress,
                "has an out-of-range completed-unit count"
            );
        }
        return new RecapHistoryLoadBaseline(
            baselineAddress,
            completedUnitCount,
            matchIndex + 1
        );
    }

    private static HistoryLoadMeasurementException BaselineInvalid(
        EventAddress baseline,
        string detail
    ) => Invalid(
        HistoryLoadMeasurementDefectCodes.CadenceBaselineInvalid,
        $"Cadence baseline '{baseline}' {detail}."
    );

    private static HistoryLoadMeasurementException Invalid(
        string code,
        string detail,
        Exception? innerException = null
    ) => new(code, detail, innerException);
}
