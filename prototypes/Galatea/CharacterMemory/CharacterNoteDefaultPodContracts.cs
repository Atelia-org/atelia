using Atelia.EventJournal;
using Atelia.MemoPod;

namespace Atelia.Galatea.Server.CharacterMemory;

internal static class CharacterNoteDefaultPodV1 {
    internal const string PodIdText =
        "00000000000000000000000000000001";
    internal const string Topic =
        "该角色主动提交、尚未分类的长期笔记。";
    internal const string EmptyStateIdentity =
        "atelia.memo-pod.document.v2.sha256:"
        + "be71767ee748fc6382db5129cf05c407"
        + "5086a2ee8cd56aa31057b55f44f3d3f6";

    internal static MemoPodId PodId { get; } = MemoPodId.Parse(PodIdText);
}

internal static class CharacterNoteDefaultPodOutcomeCodes {
    internal const string CapacityExceeded = "DEFAULT_POD_CAPACITY";
    internal const string PodUnavailable = "DEFAULT_POD_UNAVAILABLE";
    internal const string PublishNotSettled =
        "DEFAULT_POD_PUBLISH_NOT_SETTLED";
    internal const string DurabilityUnconfirmed =
        "DEFAULT_POD_DURABILITY_UNCONFIRMED";

    internal const string ProvisionTargetUnsupported =
        "DEFAULT_POD_PROVISION_TARGET_UNSUPPORTED";
    internal const string ProvisionStateMismatch =
        "DEFAULT_POD_PROVISION_STATE_MISMATCH";
    internal const string CurrentStateMismatch =
        "DEFAULT_POD_CURRENT_STATE_MISMATCH";
    internal const string PlannedMemoIdMismatch =
        "DEFAULT_POD_PLANNED_MEMO_ID_MISMATCH";
    internal const string PlannedTargetMismatch =
        "DEFAULT_POD_PLANNED_TARGET_MISMATCH";
}

internal sealed record CharacterNoteAppliedMemo(
    string SourceActionAddress,
    int ArtifactOrdinal,
    MemoPodId PodId,
    MemoId MemoId,
    string ExactText
);

internal abstract record CharacterNoteDefaultPodReconcileResult {
    private CharacterNoteDefaultPodReconcileResult() { }

    internal sealed record BaselineCovered(EventAddress SourceAction)
        : CharacterNoteDefaultPodReconcileResult;

    internal sealed record ZeroCaptured(EventAddress SourceAction)
        : CharacterNoteDefaultPodReconcileResult;

    internal sealed record AppliedNow(
        EventAddress SourceAction,
        IReadOnlyList<CharacterNoteAppliedMemo> Memos
    ) : CharacterNoteDefaultPodReconcileResult;

    internal sealed record AlreadyApplied(EventAddress SourceAction)
        : CharacterNoteDefaultPodReconcileResult;

    internal sealed record Rejected(
        EventAddress SourceAction,
        string Code
    ) : CharacterNoteDefaultPodReconcileResult;

    internal sealed record DeferredAfterCapture(
        EventAddress SourceAction,
        string Code
    ) : CharacterNoteDefaultPodReconcileResult;

    internal sealed record Quarantined(string Code)
        : CharacterNoteDefaultPodReconcileResult;

    internal sealed record SelectedHeadChanged(
        EventAddress ExpectedHead,
        EventAddress? ObservedHead
    ) : CharacterNoteDefaultPodReconcileResult;
}

internal abstract record CharacterNotePendingReconcileResult {
    private CharacterNotePendingReconcileResult() { }

    internal sealed record NoPending : CharacterNotePendingReconcileResult;

    internal sealed record Reconciled(
        CharacterNoteDefaultPodReconcileResult Result
    ) : CharacterNotePendingReconcileResult;
}

internal interface ICharacterNoteDefaultPodHandle {
    MemoPodId PodId { get; }
    MemoPodPhase Phase { get; }
    int ActiveMemoCount { get; }
    int ActiveExactTextUtf8Bytes { get; }
    MemoId Append(string exactText);
    void ResumeEditing();
    string ComputeStateIdentity();
    Task FreezeAsync(CancellationToken cancellationToken = default);
    void ConfirmCurrentDocumentDurability();
}

internal interface ICharacterNoteDefaultPodAccess {
    ICharacterNoteDefaultPodHandle Create(
        string rootPath,
        MemoPodId podId,
        string topic
    );

    ICharacterNoteDefaultPodHandle Open(
        string rootPath,
        MemoPodId podId
    );
}

internal enum CharacterNoteDefaultPodFailureKind {
    NotFound,
    UnsafePath,
    InvalidDocument,
    IoFailure,
}

internal sealed class CharacterNoteDefaultPodAccessException(
    CharacterNoteDefaultPodFailureKind kind,
    string message,
    Exception? innerException = null
) : IOException(message, innerException) {
    internal CharacterNoteDefaultPodFailureKind Kind { get; } = kind;
}

internal sealed class CharacterNoteMemoPodAccess
    : ICharacterNoteDefaultPodAccess {
    internal static CharacterNoteMemoPodAccess Instance { get; } = new();

    private CharacterNoteMemoPodAccess() { }

    public ICharacterNoteDefaultPodHandle Create(
        string rootPath,
        MemoPodId podId,
        string topic
    ) {
        try {
            return new Handle(global::Atelia.MemoPod.MemoPod.Create(
                rootPath,
                podId,
                topic
            ));
        }
        catch (MemoPodPersistenceException exception) {
            throw Map(exception);
        }
    }

    public ICharacterNoteDefaultPodHandle Open(
        string rootPath,
        MemoPodId podId
    ) {
        try {
            return new Handle(global::Atelia.MemoPod.MemoPod.Open(
                rootPath,
                podId
            ));
        }
        catch (MemoPodPersistenceException exception) {
            throw Map(exception);
        }
    }

    private static CharacterNoteDefaultPodAccessException Map(
        MemoPodPersistenceException exception
    ) => new(
        exception.FailureKind switch {
            MemoPodPersistenceFailureKind.NotFound =>
                CharacterNoteDefaultPodFailureKind.NotFound,
            MemoPodPersistenceFailureKind.UnsafePath =>
                CharacterNoteDefaultPodFailureKind.UnsafePath,
            MemoPodPersistenceFailureKind.InvalidDocument =>
                CharacterNoteDefaultPodFailureKind.InvalidDocument,
            _ => CharacterNoteDefaultPodFailureKind.IoFailure,
        },
        "MemoPod operation failed during Character Note reconciliation.",
        exception
    );

    private sealed class Handle(global::Atelia.MemoPod.MemoPod pod)
        : ICharacterNoteDefaultPodHandle {
        public MemoPodId PodId => pod.PodId;
        public MemoPodPhase Phase => pod.Phase;
        public int ActiveMemoCount => pod.List().Length;
        public int ActiveExactTextUtf8Bytes => pod.List().Sum(
            static memo => TextExtractorUtf8.GetByteCount(memo.ExactText)
        );
        public MemoId Append(string exactText) => pod.Append(exactText);
        public void ResumeEditing() => pod.ResumeEditing();
        public string ComputeStateIdentity() => pod.ComputeStateIdentity();
        public async Task FreezeAsync(
            CancellationToken cancellationToken = default
        ) {
            try {
                await pod.FreezeAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MemoPodPersistenceException exception) {
                throw Map(exception);
            }
        }
        public void ConfirmCurrentDocumentDurability() {
            try {
                pod.ConfirmCurrentDocumentDurability();
            }
            catch (MemoPodPersistenceException exception) {
                throw Map(exception);
            }
        }
    }
}
