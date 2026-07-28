namespace Atelia.SessionJournal.DerivedMemory;

public static class DerivedMemoryRoleExecutionModes {
    public const string Produce = "produce";
    public const string Identity = "identity";
    public const string SelectExisting = "select-existing";

    public static bool IsDefined(string? value) =>
        value is Produce or Identity or SelectExisting;
}

public sealed record DerivedMemoryRoleProvisioning(
    string RoleId,
    string ProfileId,
    MemoryPackBlockPath Target,
    bool Required,
    string Producer,
    string ProducerFingerprint,
    string PromptFingerprint,
    string ModelFingerprint,
    string ExecutionMode,
    string CandidateId,
    string AttemptId,
    string? SelectedArtifactId = null
);

public sealed record DerivedMemoryOrchestrationTransaction(
    string TransactionId,
    string JobFingerprint,
    string EpochId,
    string EpochPlanFingerprint,
    string LineageKey,
    string CoherenceGroup,
    string TopologyVersion,
    string? InputSetId,
    string PolicyId,
    string PolicyFingerprint,
    IReadOnlyList<DerivedMemoryRoleProvisioning> Roles
);

public sealed record DerivedMemoryRoleSettlement(
    string TransactionId,
    string RoleId,
    string ArtifactId,
    string ArtifactOutcome
);

public sealed record DerivedMemoryOrchestrationFinalization(
    string TransactionId,
    string JobFingerprint,
    string EpochId,
    string EpochPlanFingerprint,
    string PolicyId,
    string PolicyFingerprint,
    string? ExpectedPreviousSetId,
    SessionContextAnchorSetupReferences AnchorSetups,
    IReadOnlyList<DerivedMemoryRoleSettlement> IncludedSettlements,
    IReadOnlyList<string> OmittedOptionalRoleIds,
    string ExpectedSetId
);

public sealed record DerivedMemoryOrchestrationInventory(
    IReadOnlyList<DerivedMemoryOrchestrationTransaction> Transactions,
    IReadOnlyList<DerivedMemoryRoleSettlement> Settlements,
    IReadOnlyList<DerivedMemoryOrchestrationFinalization> Finalizations
);

public sealed record DerivedMemoryRoleExecution(
    DerivedMemoryRoleProvisioning Provisioning,
    IMemoryBlockMaintainer? Maintainer = null,
    Func<IReadOnlyList<string>>? CaptureCallLogPaths = null
);

public sealed record DerivedMemoryOrchestrationRequest(
    string EpochId,
    DerivedArtifactSetPolicy Policy,
    IReadOnlyList<DerivedMemoryRoleExecution> Roles
);

public sealed record DerivedMemoryRoleFailure(
    string RoleId,
    string ExceptionType,
    string Message
);

public enum DerivedMemoryOrchestrationStatus {
    Published,
    Incomplete
}

public sealed record DerivedMemoryOrchestrationResult(
    DerivedMemoryOrchestrationStatus Status,
    DerivedMemoryOrchestrationTransaction Transaction,
    IReadOnlyList<DerivedMemoryRoleSettlement> Settlements,
    IReadOnlyList<DerivedMemoryRoleFailure> Failures,
    DerivedArtifactSet? PublishedSet
);
