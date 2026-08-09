using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed record RecapBlockCatalogEntry {
    public RecapBlockCatalogEntry(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        string maintainerId,
        string maintainerCapabilityFingerprint,
        int maxContentUtf8Bytes
    ) {
        RecapBlockId = recapBlockId
            ?? throw new ArgumentNullException(nameof(recapBlockId));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        MaintainerId = string.IsNullOrWhiteSpace(maintainerId)
            ? throw new ArgumentException(
                "MaintainerId cannot be empty.",
                nameof(maintainerId)
            )
            : maintainerId;
        MaintainerCapabilityFingerprint =
            RecapMaintainerCapabilityFingerprintSyntax.Require(
                maintainerCapabilityFingerprint,
                nameof(maintainerCapabilityFingerprint)
            );
        if (maxContentUtf8Bytes <= 0
            || maxContentUtf8Bytes
                > SessionContextContributionContract
                    .MaxContributionUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(maxContentUtf8Bytes)
            );
        }
        MaxContentUtf8Bytes = maxContentUtf8Bytes;
    }

    public RecapBlockId RecapBlockId { get; }
    public ContextHeaderBlockPath Target { get; }
    public string MaintainerId { get; }
    public string MaintainerCapabilityFingerprint { get; }
    public int MaxContentUtf8Bytes { get; }
}

public sealed record RecapCadenceConfig {
    public RecapCadenceConfig(
        string historyUnitLoadEstimatorId,
        HistoryLoadUnit minimumRecentHistoryLoad,
        HistoryLoadUnit recapBuildIntervalHistoryLoad
    ) {
        HistoryUnitLoadEstimatorId =
            string.IsNullOrWhiteSpace(historyUnitLoadEstimatorId)
                ? throw new ArgumentException(
                    "History-unit load estimator ID cannot be empty.",
                    nameof(historyUnitLoadEstimatorId)
                )
                : historyUnitLoadEstimatorId;
        if (recapBuildIntervalHistoryLoad.Value <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(recapBuildIntervalHistoryLoad)
            );
        }
        _ = new HistoryLoadUnit(checked(
            minimumRecentHistoryLoad.Value
            + recapBuildIntervalHistoryLoad.Value
        ));

        MinimumRecentHistoryLoad = minimumRecentHistoryLoad;
        RecapBuildIntervalHistoryLoad =
            recapBuildIntervalHistoryLoad;
    }

    public string HistoryUnitLoadEstimatorId { get; }
    public HistoryLoadUnit MinimumRecentHistoryLoad { get; }
    public HistoryLoadUnit RecapBuildIntervalHistoryLoad { get; }
    public HistoryLoadUnit BuildThresholdHistoryLoad => new(checked(
        MinimumRecentHistoryLoad.Value
        + RecapBuildIntervalHistoryLoad.Value
    ));
}

public static class RecapPlanReasons {
    public const string BelowCadenceThreshold =
        nameof(BelowCadenceThreshold);
    public const string AwaitingReplaySafeAdmission =
        nameof(AwaitingReplaySafeAdmission);
    public const string FrozenBuildingHandled =
        nameof(FrozenBuildingHandled);
}
