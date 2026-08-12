namespace Atelia.SessionJournal.HistoryTimeline;

internal sealed record HistoryTimelineCoordinatorTestHooks(
    Action<Atelia.EventJournal.EventAddress>?
        BeforeOfflineReconcileBoundaryProbe = null,
    int? RecentReserveForwardRangeEventCap = null,
    int? RecentReserveInitialForwardRangeEventCount = null,
    int? OnlineRawCaptureLimit = null
);

internal interface IHistoryTimelineEstimatorResolver {
    IHistoryUnitLoadEstimator? Resolve(string estimatorId);
}

internal sealed class HistoryTimelineEstimatorRegistry
    : IHistoryTimelineEstimatorResolver {
    private readonly IReadOnlyDictionary<
        string,
        IHistoryUnitLoadEstimator
    > _estimators;

    internal HistoryTimelineEstimatorRegistry(
        IEnumerable<IHistoryUnitLoadEstimator> estimators
    ) {
        ArgumentNullException.ThrowIfNull(estimators);
        var values = new Dictionary<
            string,
            IHistoryUnitLoadEstimator
        >(StringComparer.Ordinal);
        foreach (IHistoryUnitLoadEstimator estimator in estimators) {
            string id = HistoryLoadMeasurementEngine
                .RequireEstimatorId(estimator);
            if (!values.TryAdd(id, estimator)) {
                throw new ArgumentException(
                    $"HistoryLoad estimator ID '{id}' is duplicated.",
                    nameof(estimators)
                );
            }
        }
        _estimators = values;
    }

    public IHistoryUnitLoadEstimator? Resolve(string estimatorId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(estimatorId);
        return _estimators.GetValueOrDefault(estimatorId);
    }
}
