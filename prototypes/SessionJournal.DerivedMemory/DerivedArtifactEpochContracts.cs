using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedMemory;

public sealed record DerivedArtifactPlannerKey(
    string LineageKey,
    string CoherenceGroup
);

public sealed record DerivedArtifactPlannerConfigDefinition(
    string LineageKey,
    string CoherenceGroup,
    string TopologyVersion,
    long MinimumRecentTokens,
    long EpochTriggerTokens,
    long SchedulingHeadroomTokens,
    long HardLimitTokens,
    string TokenEstimatorId =
        DerivedArtifactEpochPlanner.TokenEstimatorId,
    string BoundaryPolicyId =
        DerivedArtifactEpochPlanner.BoundaryPolicyId,
    string HardLimitPolicyId =
        DerivedArtifactEpochPlanner.HardLimitPolicyId,
    string GenesisPolicyId =
        DerivedArtifactEpochPlanner.GenesisPolicyId
);

public sealed record DerivedArtifactPlannerConfig(
    string ConfigId,
    string LineageKey,
    string CoherenceGroup,
    string? PreviousConfigId,
    string TopologyVersion,
    long MinimumRecentTokens,
    long EpochTriggerTokens,
    long SchedulingHeadroomTokens,
    long HardLimitTokens,
    string TokenEstimatorId,
    string BoundaryPolicyId,
    string HardLimitPolicyId,
    string GenesisPolicyId
) {
    public DerivedArtifactPlannerKey Key =>
        new(LineageKey, CoherenceGroup);
}

public sealed record DerivedArtifactPlannerConfigPointer(
    string LineageKey,
    string CoherenceGroup,
    string ConfigId
);

public sealed record DerivedArtifactEpochPlanningDiagnostics(
    long HeaderVisits,
    long PayloadReads,
    long DecodedPayloadBytes,
    int DecodedEventCount,
    int DependencyClosedUnitCount,
    int ReplaySafeBoundaryCount,
    long TotalTokens,
    long EligibleTokens,
    long RetainedRecentTokens
);

public sealed record DerivedArtifactEpochPlan(
    string EpochId,
    string LineageKey,
    string CoherenceGroup,
    string TopologyVersion,
    string ConfigId,
    string? PreviousEpochId,
    string? InputSetId,
    EventAddress PlannedAtRawHead,
    EventAddress SourceStartExclusive,
    EventAddress SourceEndInclusive,
    SessionContextAnchorSetupReferences RawStartSetups,
    long MeasuredTokens,
    DerivedArtifactEpochPlanningDiagnostics PlanningDiagnostics
) {
    public DerivedArtifactPlannerKey Key =>
        new(LineageKey, CoherenceGroup);
}

public sealed record DerivedArtifactEpochLatestPointer(
    string LineageKey,
    string CoherenceGroup,
    string EpochId
);

public sealed record DerivedArtifactEpochInventory(
    IReadOnlyList<DerivedArtifactPlannerConfig> Configs,
    IReadOnlyList<DerivedArtifactPlannerConfigPointer> CurrentConfigs,
    IReadOnlyList<DerivedArtifactEpochPlan> Epochs,
    IReadOnlyList<DerivedArtifactEpochLatestPointer> LatestEpochs
);

public enum DerivedArtifactEpochPlanningStatus {
    Planned,
    AlreadyPlanned,
    BelowTrigger
}

public sealed record DerivedArtifactEpochPlanningResult(
    DerivedArtifactEpochPlanningStatus Status,
    DerivedArtifactPlannerConfig Config,
    DerivedArtifactEpochPlan? Epoch,
    DerivedArtifactEpochPlanningDiagnostics Diagnostics
);

public sealed record DerivedArtifactEpochPlanningRequest(
    string LineageKey,
    string CoherenceGroup,
    string? ExpectedPreviousEpochId,
    string? InputSetId
);

public sealed class DerivedArtifactEpochConcurrencyException
    : InvalidOperationException {
    public DerivedArtifactEpochConcurrencyException(string message)
        : base(message) {
    }
}

public sealed class DerivedArtifactEpochBackpressureException
    : InvalidOperationException {
    public DerivedArtifactEpochBackpressureException(string message)
        : base(message) {
    }
}
