using Atelia.EventJournal;

namespace Atelia.Galatea.Server.CharacterMemory;

internal static class CharacterMemoryStoreBounds {
    internal const int MaximumIdentityUtf8Bytes = 1024;
    internal const int MaximumPodStateIdentityUtf8Bytes = 256;
    internal const int MaximumCodeUtf8Bytes = 128;
    internal const int MaximumOriginLookupSourceCount = 65_536;
}

internal enum CharacterMemoryStoreState {
    Provisioning,
    Ready,
    Quarantined,
}

internal enum CharacterMemoryCaptureState {
    ZeroCaptured,
    Captured,
    Planned,
    Applied,
    Rejected,
}

internal sealed record CharacterMemoryStoreOwner(
    string UserId,
    string SessionRepositoryId
);

internal sealed record CharacterMemoryStoreBaseline(
    EventJournalPhysicalAppendFrontier CaptureFromPhysicalFrontier,
    string? SelectedHead
);

internal sealed record CharacterMemoryCaptureRequest(
    string SourceActionAddress,
    string VisibleActionSha256,
    int VisibleActionUtf8Bytes,
    string ExtractorContractId,
    IReadOnlyList<string> ExactTexts
);

internal sealed record CharacterMemoryPlanRequest(
    string SourceActionAddress,
    string ExtractionCommitment,
    string BasePodStateIdentity,
    string TargetPodStateIdentity,
    IReadOnlyList<string> MemoIds
);

internal sealed record CharacterMemorySettleRequest(
    string SourceActionAddress,
    string ExtractionCommitment,
    string TargetPodStateIdentity
);

internal sealed record CharacterMemoryRejectRequest(
    string SourceActionAddress,
    string ExtractionCommitment,
    string RejectionCode
);

internal sealed record CharacterMemoryQuarantineRequest(
    long ExpectedStoreRevision,
    string QuarantineCode,
    string? ObservedPodStateIdentity = null
);

internal sealed record CharacterMemoryNoteSnapshot(
    int ArtifactOrdinal,
    string ExactText,
    string? MemoId
);

internal sealed record CharacterMemoryCaptureSnapshot(
    string SourceActionAddress,
    string VisibleActionSha256,
    int VisibleActionUtf8Bytes,
    string ExtractorContractId,
    string ExtractionCommitment,
    int ArtifactCount,
    CharacterMemoryCaptureState State,
    string? BasePodStateIdentity,
    string? TargetPodStateIdentity,
    string? RejectionCode,
    long StateRevision,
    IReadOnlyList<CharacterMemoryNoteSnapshot> Notes
);

internal sealed record CharacterMemoryStatusSnapshot(
    CharacterMemoryStoreOwner Owner,
    CharacterMemoryStoreBaseline Baseline,
    CharacterMemoryStoreState StoreState,
    string ProvisionTargetPodStateIdentity,
    string? SettledDefaultPodStateIdentity,
    string? ActiveSourceAction,
    string? QuarantineCode,
    string? QuarantineObservedPodStateIdentity,
    long StoreRevision,
    CharacterMemoryCaptureSnapshot? ActiveCapture
);

internal sealed record CharacterMemoryCaptureBatchSnapshot(
    CharacterMemoryStatusSnapshot Status,
    IReadOnlyList<CharacterMemoryCaptureSnapshot> Captures
);

internal enum CharacterMemoryCaptureDisposition {
    BaselineCovered,
    ZeroCaptured,
    Captured,
    AlreadyCaptured,
}

internal sealed record CharacterMemoryCaptureResult(
    CharacterMemoryCaptureDisposition Disposition,
    long StoreRevision,
    CharacterMemoryCaptureSnapshot? Capture
);

internal enum CharacterMemoryProvisionDisposition {
    Recorded,
    AlreadyRecorded,
}

internal sealed record CharacterMemoryProvisionResult(
    CharacterMemoryProvisionDisposition Disposition,
    long StoreRevision
);

internal enum CharacterMemoryPlanDisposition {
    Planned,
    AlreadyPlanned,
    AlreadyApplied,
}

internal sealed record CharacterMemoryPlanResult(
    CharacterMemoryPlanDisposition Disposition,
    long StoreRevision,
    CharacterMemoryCaptureSnapshot Capture
);

internal enum CharacterMemorySettleDisposition {
    Applied,
    AlreadyApplied,
}

internal sealed record CharacterMemorySettleResult(
    CharacterMemorySettleDisposition Disposition,
    long StoreRevision,
    CharacterMemoryCaptureSnapshot Capture
);

internal enum CharacterMemoryRejectDisposition {
    Rejected,
    AlreadyRejected,
}

internal sealed record CharacterMemoryRejectResult(
    CharacterMemoryRejectDisposition Disposition,
    long StoreRevision,
    CharacterMemoryCaptureSnapshot Capture
);

internal enum CharacterMemoryQuarantineDisposition {
    Quarantined,
    AlreadyQuarantined,
}

internal sealed record CharacterMemoryQuarantineResult(
    CharacterMemoryQuarantineDisposition Disposition,
    long StoreRevision
);

internal sealed record CharacterMemoryStoreTestHooks(
    Action<string>? BeforeCommit = null,
    Action<string>? AfterCommitBeforeReturn = null
) {
    internal static CharacterMemoryStoreTestHooks None { get; } = new();
}

internal sealed class CharacterMemoryStoreConflictException(
    string message
) : InvalidOperationException(message);

internal sealed class CharacterMemoryStoreQuarantinedException(
    string code
) : InvalidOperationException(
    $"Character Memory store is quarantined with code '{code}'."
) {
    internal string Code { get; } = code;
}

internal sealed class CharacterMemoryStoreCommitOutcomeException(
    string operation,
    Exception innerException
) : IOException(
    $"Character Memory store operation '{operation}' did not publish its exact post-state.",
    innerException
) {
    internal string Operation { get; } = operation;
}
