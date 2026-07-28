namespace Atelia.SessionJournal.DerivedMemory;

/// <summary>
/// Successful strict validation summary. Validation throws <see cref="InvalidDataException"/>
/// for malformed, inconsistent, forked, incomplete, or stale derived-memory state.
/// </summary>
public sealed record DerivedMemoryValidationReport(
    int ArtifactCount,
    int ArtifactSetCount,
    int LatestPointerCount,
    int ExactArtifactSetKeyCount,
    int PlannerConfigCount = 0,
    int CurrentPlannerConfigCount = 0,
    int ArtifactEpochCount = 0,
    int LatestArtifactEpochCount = 0,
    int OrchestrationTransactionCount = 0,
    int RoleSettlementCount = 0,
    int OrchestrationFinalizationCount = 0
);
