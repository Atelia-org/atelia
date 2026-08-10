using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

internal static class HistoryLoadMeasurementEngine {
    internal static string RequireEstimatorId(
        IHistoryUnitLoadEstimator estimator
    ) {
        ArgumentNullException.ThrowIfNull(estimator);
        string estimatorId;
        try {
            estimatorId = estimator.Id;
        }
        catch (Exception exception) when (
            HistoryLoadNonFatalException.IsCatchable(exception)
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
        return estimatorId;
    }

    internal static HistoryUnitLoadMeasurement MeasureUnit(
        IHistoryUnitLoadEstimator estimator,
        SJ.SessionHistoryPlanningUnit unit
    ) {
        try {
            HistoryUnitLoadMeasurement measured = estimator.Measure(
                    unit,
                    HistoryLoadMeasurementSafety.V1
                        .MaxRenderedHistoryUnitUtf8Bytes
                )
                ?? throw Invalid(
                    HistoryLoadMeasurementDefectCodes.MeasurementInvalid,
                    "History-load estimator returned null."
                );
            if (measured.Load.Value < 1) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes.MeasurementInvalid,
                    "History-load estimator returned a load below one."
                );
            }
            if (measured.RenderedUtf8Bytes < 0
                || measured.RenderedUtf8Bytes
                    > HistoryLoadMeasurementSafety.V1
                        .MaxRenderedHistoryUnitUtf8Bytes) {
                throw Invalid(
                    HistoryLoadMeasurementDefectCodes.MeasurementInvalid,
                    "History-load estimator returned an invalid rendered UTF-8 byte count."
                );
            }
            return measured;
        }
        catch (HistoryLoadMeasurementException) {
            throw;
        }
        catch (OverflowException exception) {
            throw Invalid(
                HistoryLoadMeasurementDefectCodes.MeasurementOverflow,
                "History-load estimator overflowed.",
                exception
            );
        }
        catch (Exception exception) when (
            HistoryLoadNonFatalException.IsCatchable(exception)
        ) {
            throw Invalid(
                HistoryLoadMeasurementDefectCodes.EstimatorFailed,
                "History-load estimator failed.",
                exception
            );
        }
    }

    internal static HistoryLoadMeasurementException Invalid(
        string code,
        string detail,
        Exception? innerException = null
    ) => new(code, detail, innerException);
}

internal sealed record HistoryLoadWindowShape(
    IReadOnlyDictionary<Atelia.EventJournal.EventAddress, int> RawPositions
);

internal static class HistoryLoadWindowValidator {
    internal static void ValidateCollections(
        SJ.SessionHistoryPlanningWindow window,
        bool requireBoundarySetups = false
    ) {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Units is null
            || window.RawAddresses is null
            || window.ReplaySafeBoundaries is null
            || (requireBoundarySetups
                && window.ReplaySafeBoundarySetups is null)) {
            throw HistoryLoadMeasurementEngine.Invalid(
                HistoryLoadMeasurementDefectCodes.PlanningWindowInvalid,
                "Planning window collections cannot be null."
            );
        }
    }

    internal static HistoryLoadWindowShape Validate(
        SJ.SessionHistoryPlanningWindow window,
        bool requireBoundarySetups = false
    ) {
        ValidateCollections(window, requireBoundarySetups);

        var rawPositions = new Dictionary<
            Atelia.EventJournal.EventAddress,
            int
        >();
        for (int index = 0; index < window.RawAddresses.Count; index++) {
            Atelia.EventJournal.EventAddress address =
                window.RawAddresses[index];
            if (!rawPositions.TryAdd(address, index)) {
                throw HistoryLoadMeasurementEngine.Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .PlanningWindowInvalid,
                    $"Planning raw range repeats address '{address}'."
                );
            }
        }

        var boundaryAddresses =
            new HashSet<Atelia.EventJournal.EventAddress>();
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
                || boundary.CompletedUnitCount > window.Units.Count
                || boundary.CompletedUnitCount
                    < previousCompletedUnitCount
                || (requireBoundarySetups
                    && !window.ReplaySafeBoundarySetups.ContainsKey(
                        boundary.Address
                    ))) {
                throw HistoryLoadMeasurementEngine.Invalid(
                    HistoryLoadMeasurementDefectCodes
                        .PlanningWindowInvalid,
                    "Planning window has malformed replay-safe boundary order, setups, or completed-unit counts."
                );
            }
            previousRawPosition = rawPosition;
            previousCompletedUnitCount = boundary.CompletedUnitCount;
        }
        return new HistoryLoadWindowShape(rawPositions);
    }
}
