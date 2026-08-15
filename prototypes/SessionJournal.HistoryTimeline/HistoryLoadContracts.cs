using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>
/// A non-negative, estimator-scoped internal history-load value.
/// It is not a provider or model token count.
/// </summary>
public readonly record struct HistoryLoadUnit {
    public HistoryLoadUnit(long value) {
        if (value < 0) {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        Value = value;
    }

    public long Value { get; }
}

/// <summary>
/// Measures one dependency-closed SessionJournal history unit.
/// </summary>
public interface IHistoryUnitLoadEstimator {
    string Id { get; }

    HistoryUnitLoadMeasurement Measure(
        SJ.SessionHistoryPlanningUnit unit,
        int maxRenderedUtf8Bytes
    );
}

public sealed record HistoryUnitLoadMeasurement(
    HistoryLoadUnit Load,
    int RenderedUtf8Bytes
);

public sealed record HistoryLoadProjection {
    public HistoryLoadProjection(
        string estimatorId,
        EventAddress baselineAddress,
        int baselineCompletedUnitCount,
        HistoryLoadUnit growth,
        int renderedUtf8Bytes,
        IReadOnlyList<HistoryLoadBoundaryProjection>
            replaySafeBoundaries
    ) {
        EstimatorId = string.IsNullOrWhiteSpace(estimatorId)
            ? throw new ArgumentException(
                "Estimator ID cannot be empty.",
                nameof(estimatorId)
            )
            : estimatorId;
        if (baselineCompletedUnitCount < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(baselineCompletedUnitCount)
            );
        }
        if (renderedUtf8Bytes < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(renderedUtf8Bytes)
            );
        }
        ArgumentNullException.ThrowIfNull(replaySafeBoundaries);
        if (replaySafeBoundaries.Any(
                static boundary => boundary is null
            )) {
            throw new ArgumentException(
                "Replay-safe load boundaries cannot contain null.",
                nameof(replaySafeBoundaries)
            );
        }

        BaselineAddress = baselineAddress;
        BaselineCompletedUnitCount =
            baselineCompletedUnitCount;
        Growth = growth;
        RenderedUtf8Bytes = renderedUtf8Bytes;
        ReplaySafeBoundaries = Array.AsReadOnly([
            .. replaySafeBoundaries
        ]);
    }

    public string EstimatorId { get; }
    public EventAddress BaselineAddress { get; }
    public int BaselineCompletedUnitCount { get; }
    public HistoryLoadUnit Growth { get; }
    public int RenderedUtf8Bytes { get; }
    public IReadOnlyList<HistoryLoadBoundaryProjection>
        ReplaySafeBoundaries { get; }
}

public sealed record HistoryLoadBoundaryProjection {
    public HistoryLoadBoundaryProjection(
        EventAddress address,
        int historyUnitCountSinceBaseline,
        HistoryLoadUnit absorbedSinceBaseline,
        int cumulativeRenderedUtf8Bytes
    ) {
        if (historyUnitCountSinceBaseline < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(historyUnitCountSinceBaseline)
            );
        }
        if (cumulativeRenderedUtf8Bytes < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(cumulativeRenderedUtf8Bytes)
            );
        }
        Address = address;
        HistoryUnitCountSinceBaseline =
            historyUnitCountSinceBaseline;
        AbsorbedSinceBaseline = absorbedSinceBaseline;
        CumulativeRenderedUtf8Bytes =
            cumulativeRenderedUtf8Bytes;
    }

    public EventAddress Address { get; }
    public int HistoryUnitCountSinceBaseline { get; }
    public HistoryLoadUnit AbsorbedSinceBaseline { get; }
    public int CumulativeRenderedUtf8Bytes { get; }
}

/// <summary>
/// Code-owned bounds for H0 history-load measurement.
/// </summary>
internal static class HistoryLoadMeasurementSafety {
    public static class V1 {
        public const int MaxRenderedHistoryUnitUtf8Bytes =
            4 * 1024 * 1024;
        public const int MaxBaselineRelativeWindowUtf8Bytes =
            32 * 1024 * 1024;
    }
}

public static class HistoryLoadMeasurementDefectCodes {
    public const string InvalidUnicode = nameof(InvalidUnicode);
    public const string UnsupportedHistoryMessage =
        nameof(UnsupportedHistoryMessage);
    public const string UnsupportedHistoryBlock =
        nameof(UnsupportedHistoryBlock);
    public const string HistoryLoadInputTooLarge =
        nameof(HistoryLoadInputTooLarge);
    public const string MeasurementInvalid =
        nameof(MeasurementInvalid);
    public const string MeasurementOverflow =
        nameof(MeasurementOverflow);
    public const string EstimatorFailed = nameof(EstimatorFailed);
    public const string CadenceBaselineInvalid =
        nameof(CadenceBaselineInvalid);
    public const string PlanningWindowInvalid =
        nameof(PlanningWindowInvalid);
}

/// <summary>
/// A typed H0 measurement failure that a later Planner vertical can map to
/// planning-unavailable without mutating Store or calling a Maintainer.
/// </summary>
public sealed class HistoryLoadMeasurementException
    : Exception {
    public HistoryLoadMeasurementException(
        string code,
        string message,
        Exception? innerException = null
    ) : base(message, innerException) {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException(
                "Defect code cannot be empty.",
                nameof(code)
            )
            : code;
    }

    public string Code { get; }
}
