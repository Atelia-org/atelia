using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

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

public sealed record RecapHistoryLoadMeasurement {
    public RecapHistoryLoadMeasurement(
        string estimatorId,
        EventAddress baselineAddress,
        int baselineCompletedUnitCount,
        HistoryLoadUnit growth,
        int renderedUtf8Bytes,
        IReadOnlyList<RecapHistoryLoadBoundary>
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
    public IReadOnlyList<RecapHistoryLoadBoundary>
        ReplaySafeBoundaries { get; }
}

public sealed record RecapHistoryLoadBoundary {
    public RecapHistoryLoadBoundary(
        EventAddress address,
        int historyUnitCountSinceBaseline,
        HistoryLoadUnit absorbedSinceBaseline
    ) {
        if (historyUnitCountSinceBaseline < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(historyUnitCountSinceBaseline)
            );
        }
        Address = address;
        HistoryUnitCountSinceBaseline =
            historyUnitCountSinceBaseline;
        AbsorbedSinceBaseline = absorbedSinceBaseline;
    }

    public EventAddress Address { get; }
    public int HistoryUnitCountSinceBaseline { get; }
    public HistoryLoadUnit AbsorbedSinceBaseline { get; }
}

/// <summary>
/// Code-owned bounds for H0 history-load measurement.
/// </summary>
public static class HistoryLoadMeasurementSafety {
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
