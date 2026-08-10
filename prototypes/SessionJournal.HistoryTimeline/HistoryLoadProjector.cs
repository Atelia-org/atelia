using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>
/// Projects exact replay-safe SessionJournal boundaries onto additive H0
/// HistoryLoad values without changing production cadence behavior.
/// </summary>
public static class HistoryLoadProjector {
    public static HistoryLoadProjection Measure(
        SJ.SessionHistoryPlanningWindow window,
        EventAddress baselineAddress,
        IHistoryUnitLoadEstimator estimator
    ) {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(estimator);
        string estimatorId =
            HistoryLoadMeasurementEngine.RequireEstimatorId(estimator);
        HistoryLoadWindowValidator.ValidateCollections(window);
        HistoryLoadBaseline baseline =
            HistoryLoadBaselineResolver.Resolve(
                window.StartExclusive,
                window.Units.Count,
                window.ReplaySafeBoundaries,
                baselineAddress
            );
        int baselineCompletedUnitCount =
            baseline.CompletedUnitCount;
        int firstOutputBoundaryIndex =
            baseline.FirstLaterBoundaryIndex;
        _ = HistoryLoadWindowValidator.Validate(window);

        int suffixUnitCount =
            window.Units.Count - baselineCompletedUnitCount;
        var loadPrefix = new long[suffixUnitCount + 1];
        var bytePrefix = new int[suffixUnitCount + 1];
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
                HistoryLoadMeasurementEngine.MeasureUnit(
                    estimator,
                    unit
                );
            try {
                loadPrefix[suffixIndex + 1] = checked(
                    loadPrefix[suffixIndex]
                    + measured.Load.Value
                );
                renderedWindowBytes = checked(
                    renderedWindowBytes
                    + measured.RenderedUtf8Bytes
                );
                bytePrefix[suffixIndex + 1] =
                    renderedWindowBytes;
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

        var projected = new List<HistoryLoadBoundaryProjection>();
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
            projected.Add(new HistoryLoadBoundaryProjection(
                boundary.Address,
                relativeUnitCount,
                new HistoryLoadUnit(loadPrefix[relativeUnitCount]),
                bytePrefix[relativeUnitCount]
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
        return new HistoryLoadProjection(
            estimatorId,
            baselineAddress,
            baselineCompletedUnitCount,
            new HistoryLoadUnit(growth),
            renderedWindowBytes,
            projected
        );
    }

    private static HistoryLoadMeasurementException Invalid(
        string code,
        string detail,
        Exception? innerException = null
    ) => new(code, detail, innerException);

}

public sealed record HistoryLoadBaseline {
    internal HistoryLoadBaseline(
        EventAddress address,
        int completedUnitCount,
        int firstLaterBoundaryIndex
    ) {
        Address = address;
        CompletedUnitCount = completedUnitCount;
        FirstLaterBoundaryIndex = firstLaterBoundaryIndex;
    }

    public EventAddress Address { get; }
    public int CompletedUnitCount { get; }
    public int FirstLaterBoundaryIndex { get; }
}

public static class HistoryLoadBaselineResolver {
    public static HistoryLoadBaseline Resolve(
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
            return new HistoryLoadBaseline(
                baselineAddress,
                completedUnitCount: 0,
                firstLaterBoundaryIndex: 0
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
        return new HistoryLoadBaseline(
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
