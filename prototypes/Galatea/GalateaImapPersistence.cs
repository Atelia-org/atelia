using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal enum GalateaExternalMailInboxState {
    Pending,
    ObservationBound,
    Observed,
    Rejected,
    Quarantined
}

internal sealed record GalateaImapCheckpointSnapshot(
    string AccountReference,
    uint UidValidity,
    uint ScannedThroughUid,
    long BaselineAtUnixTimeMilliseconds,
    string? BlockedCode,
    long Revision
);

internal sealed record GalateaExternalMailInboxSnapshot(
    long InboxId,
    string AccountReference,
    uint UidValidity,
    uint Uid,
    string MessageId,
    string TargetCharacterId,
    string TargetSessionRepositoryId,
    string TargetCharacterName,
    string? From,
    string? Subject,
    string? Body,
    int AttachmentCount,
    GalateaExternalMailInboxState State,
    string? ExpectedSessionHead,
    SessionInputContent? BoundInput,
    string? ObservationAddress,
    string? Code,
    long Revision
) {
    internal SessionInputContent? ObservationContent => BoundInput;
}

internal sealed record GalateaImapInboxStatus(int PendingCount, int BoundCount, bool HasQuarantined);

internal static class GalateaImapPersistenceBounds {
    internal const int MaximumPendingCount = 128;
    internal const int MaximumPendingUtf8Bytes = 8 * 1024 * 1024;
    internal const int MaximumBodyUtf8Bytes = 64 * 1024;
    internal const int MaximumSubjectUtf8Bytes = 4 * 1024;
}
