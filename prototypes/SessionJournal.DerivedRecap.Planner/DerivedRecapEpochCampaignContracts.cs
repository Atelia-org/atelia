using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed record RecapEpochPlanningConfiguration {
    public RecapEpochPlanningConfiguration(
        IReadOnlyList<RecapBlockCatalogEntry> orderedCatalog,
        RecapCadenceConfig cadence,
        IHistoryUnitLoadEstimator historyUnitLoadEstimator,
        IRecapEpochPlanningPolicy? policy = null
    ) {
        ArgumentNullException.ThrowIfNull(orderedCatalog);
        OrderedCatalog = Array.AsReadOnly(orderedCatalog.ToArray());
        if (OrderedCatalog.Count == 0
            || OrderedCatalog.Any(static item => item is null)) {
            throw new ArgumentException(
                "Shared-epoch catalog must be non-empty.",
                nameof(orderedCatalog)
            );
        }
        if (OrderedCatalog.Select(static item => item.RecapBlockId)
                .Distinct().Count() != OrderedCatalog.Count
            || OrderedCatalog.Select(static item => item.Target)
                .Distinct().Count() != OrderedCatalog.Count) {
            throw new ArgumentException(
                "Shared-epoch catalog IDs and targets must be unique.",
                nameof(orderedCatalog)
            );
        }
        for (int index = 1; index < OrderedCatalog.Count; index++) {
            if (CompareTargets(
                    OrderedCatalog[index - 1].Target,
                    OrderedCatalog[index].Target
                ) >= 0) {
                throw new ArgumentException(
                    "Shared-epoch catalog must use canonical target order.",
                    nameof(orderedCatalog)
                );
            }
        }
        Cadence = cadence
            ?? throw new ArgumentNullException(nameof(cadence));
        HistoryUnitLoadEstimator = historyUnitLoadEstimator
            ?? throw new ArgumentNullException(
                nameof(historyUnitLoadEstimator)
            );
        if (!string.Equals(
                cadence.HistoryUnitLoadEstimatorId,
                historyUnitLoadEstimator.Id,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "Cadence and HistoryLoad estimator IDs differ.",
                nameof(historyUnitLoadEstimator)
            );
        }
        Policy = policy ?? new MaintainCompleteRosterEpochPolicy();
    }

    public IReadOnlyList<RecapBlockCatalogEntry> OrderedCatalog { get; }
    public RecapCadenceConfig Cadence { get; }
    public IHistoryUnitLoadEstimator HistoryUnitLoadEstimator { get; }
    public IRecapEpochPlanningPolicy Policy { get; }

    private static int CompareTargets(
        ContextHeaderBlockPath left,
        ContextHeaderBlockPath right
    ) {
        int carrier = left.Carrier.CompareTo(right.Carrier);
        return carrier != 0
            ? carrier
            : StringComparer.Ordinal.Compare(
                left.BlockKey,
                right.BlockKey
            );
    }
}

public sealed record RecapEpochOperationLimits {
    public RecapEpochOperationLimits(
        int maxRawGrowthEventCount,
        int maxRawEventsPerEpoch,
        int maxMaintainerCallsPerEpoch,
        int maxEpochsPerOperation,
        int maxMaintainerCallsPerOperation,
        int maxRecapBlockCount,
        int maxRebuildForwardRangeEventCount =
            SessionSelectedLineageAuditLimits
                .MaximumForwardRangeEventCount
    ) {
        MaxRawGrowthEventCount = Positive(
            maxRawGrowthEventCount,
            nameof(maxRawGrowthEventCount)
        );
        MaxRawEventsPerEpoch = Positive(
            maxRawEventsPerEpoch,
            nameof(maxRawEventsPerEpoch)
        );
        MaxMaintainerCallsPerEpoch = Positive(
            maxMaintainerCallsPerEpoch,
            nameof(maxMaintainerCallsPerEpoch)
        );
        MaxEpochsPerOperation = Positive(
            maxEpochsPerOperation,
            nameof(maxEpochsPerOperation)
        );
        MaxMaintainerCallsPerOperation = Positive(
            maxMaintainerCallsPerOperation,
            nameof(maxMaintainerCallsPerOperation)
        );
        MaxRecapBlockCount = Positive(
            maxRecapBlockCount,
            nameof(maxRecapBlockCount)
        );
        MaxRebuildForwardRangeEventCount = Positive(
            maxRebuildForwardRangeEventCount,
            nameof(maxRebuildForwardRangeEventCount)
        );
        if (MaxRawEventsPerEpoch > MaxRawGrowthEventCount
            || MaxRecapBlockCount
                > SessionContextContributionContract
                    .MaxContributionCount) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawEventsPerEpoch),
                "Epoch/raw/catalog limits are internally inconsistent."
            );
        }
        if (MaxRebuildForwardRangeEventCount < MaxRawEventsPerEpoch
            || MaxRebuildForwardRangeEventCount
                > SessionSelectedLineageAuditLimits
                    .MaximumForwardRangeEventCount) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRebuildForwardRangeEventCount)
            );
        }
    }

    public int MaxRawGrowthEventCount { get; }
    public int MaxRawEventsPerEpoch { get; }
    public int MaxMaintainerCallsPerEpoch { get; }
    public int MaxEpochsPerOperation { get; }
    public int MaxMaintainerCallsPerOperation { get; }
    public int MaxRecapBlockCount { get; }
    public int MaxRebuildForwardRangeEventCount { get; }

    private static int Positive(int value, string name)
        => value > 0
            ? value
            : throw new ArgumentOutOfRangeException(name);
}

public sealed record RecapEpochPlanningFacts(
    SessionHistoryPlanningWindow Window,
    RecapHistoryLoadMeasurement HistoryLoad,
    RecapCadenceConfig Cadence,
    int MaxRawEventsPerEpoch
);

public interface IRecapEpochPlanningPolicy {
    string Id { get; }

    RecapEpochPlanningDecision Decide(
        RecapEpochPlanningFacts facts
    );
}

public abstract record RecapEpochPlanningDecision {
    private RecapEpochPlanningDecision() {
    }

    public sealed record NoBuild(string Reason)
        : RecapEpochPlanningDecision;

    public sealed record Build(EventAddress AdmissionBoundary)
        : RecapEpochPlanningDecision;
}

public sealed class MaintainCompleteRosterEpochPolicy
    : IRecapEpochPlanningPolicy {
    public const string PolicyId = "maintain-complete-roster-epoch-v1";

    public string Id => PolicyId;

    public RecapEpochPlanningDecision Decide(
        RecapEpochPlanningFacts facts
    ) {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.HistoryLoad.Growth.Value
            < facts.Cadence.BuildThresholdHistoryLoad.Value) {
            return new RecapEpochPlanningDecision.NoBuild(
                RecapPlanReasons.BelowCadenceThreshold
            );
        }

        Dictionary<EventAddress, int> rawPositions = facts.Window
            .RawAddresses
            .Select((address, index) => (address, index))
            .ToDictionary(
                static pair => pair.address,
                static pair => pair.index
            );
        RecapHistoryLoadBoundary? selected = facts.HistoryLoad
            .ReplaySafeBoundaries
            .Where(boundary =>
                rawPositions.TryGetValue(
                    boundary.Address,
                    out int rawIndex
                )
                && rawIndex + 1 <= facts.MaxRawEventsPerEpoch)
            .Where(boundary =>
                boundary.AbsorbedSinceBaseline.Value
                    >= facts.Cadence
                        .RecapBuildIntervalHistoryLoad.Value
                && checked(
                    facts.HistoryLoad.Growth.Value
                    - boundary.AbsorbedSinceBaseline.Value
                ) >= facts.Cadence
                    .MinimumRecentHistoryLoad.Value)
            .LastOrDefault();
        return selected is null
            ? new RecapEpochPlanningDecision.NoBuild(
                RecapPlanReasons.AwaitingReplaySafeAdmission
            )
            : new RecapEpochPlanningDecision.Build(selected.Address);
    }
}

public enum RecapEpochFullRebuildReason {
    BoundedRawAuthorityInsufficient = 1,
    RawGrowthLimitExceeded = 2,
    TopologyChanged = 3,
}

public abstract record DerivedRecapEpochOperationResult {
    private DerivedRecapEpochOperationResult() {
    }

    public sealed record Fresh(
        PublishedRecapEpochDescriptor? Latest,
        int EpochsPublished,
        int MaintainerCalls,
        string Reason
    ) : DerivedRecapEpochOperationResult;

    public sealed record MoreWorkPending(
        PublishedRecapEpochDescriptor Latest,
        int EpochsPublished,
        int MaintainerCalls
    ) : DerivedRecapEpochOperationResult;

    public sealed record FullRebuildRequired(
        RecapEpochFullRebuildReason Reason,
        EventAddress CapturedRawHead,
        string Detail
    ) : DerivedRecapEpochOperationResult;

    public sealed record ConfigurationLimit(string Detail)
        : DerivedRecapEpochOperationResult;

    public sealed record Unavailable(string Code, string Detail)
        : DerivedRecapEpochOperationResult;

    public sealed record BlockFailed(
        EventAddress AdmissionBoundary,
        RecapBlockId RecapBlockId,
        string Code,
        string Detail
    ) : DerivedRecapEpochOperationResult;
}
