namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed record HistoryUnitLoadEstimatorResolutionDefect(
    string Code,
    string Detail
);

public static class HistoryUnitLoadEstimatorResolutionDefectCodes {
    public const string EstimatorIdMissing = nameof(EstimatorIdMissing);
    public const string UnknownEstimator = nameof(UnknownEstimator);
}

public abstract record HistoryUnitLoadEstimatorResolutionResult {
    private HistoryUnitLoadEstimatorResolutionResult() {
    }

    public sealed record Resolved(
        string EstimatorId,
        IHistoryUnitLoadEstimator Estimator
    ) : HistoryUnitLoadEstimatorResolutionResult;

    public sealed record Invalid(
        HistoryUnitLoadEstimatorResolutionDefect Defect
    ) : HistoryUnitLoadEstimatorResolutionResult;
}

/// <summary>
/// Code-owned estimator registry used by Host composition when resolving
/// the active HistoryLoad config.
/// </summary>
public static class HistoryUnitLoadEstimatorRegistry {
    private static readonly IHistoryUnitLoadEstimator O200kBaseV1 =
        new O200kBaseHistoryUnitLoadEstimator();

    public static HistoryUnitLoadEstimatorResolutionResult Resolve(
        string? estimatorId
    ) {
        if (string.IsNullOrWhiteSpace(estimatorId)) {
            return Invalid(
                HistoryUnitLoadEstimatorResolutionDefectCodes
                    .EstimatorIdMissing,
                "History-unit load estimator ID cannot be empty."
            );
        }
        if (string.Equals(
                estimatorId,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                StringComparison.Ordinal
            )) {
            return new HistoryUnitLoadEstimatorResolutionResult.Resolved(
                estimatorId,
                O200kBaseV1
            );
        }
        return Invalid(
            HistoryUnitLoadEstimatorResolutionDefectCodes
                .UnknownEstimator,
            $"Unknown history-unit load estimator '{estimatorId}'."
        );
    }

    private static HistoryUnitLoadEstimatorResolutionResult.Invalid
        Invalid(
        string code,
        string detail
    ) => new(new HistoryUnitLoadEstimatorResolutionDefect(code, detail));
}
