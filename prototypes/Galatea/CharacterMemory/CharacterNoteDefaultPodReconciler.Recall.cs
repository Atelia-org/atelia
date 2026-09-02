using Atelia.Completion.Abstractions;
using Atelia.MemoPod;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterNoteDefaultPodReconciler {
    internal async Task<MemoRecallResult> RecallSettledDefaultPodAsync(
        ICompletionClient completionClient,
        string modelId,
        string query,
        MemoRecallOptions options,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(completionClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(options);

        ICharacterNoteDefaultPodHandle settledPod;
        await _podMutationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try {
            CharacterMemoryStatusSnapshot status =
                _store.ReadStatusSnapshot();
            RequireRecallReady(status);

            if (status.ActiveDerivedInfoWork is { } activeDerivedInfo) {
                if (activeDerivedInfo.State
                        is not CharacterMemoryDerivedInfoState.Planned) {
                    throw new InvalidDataException(
                        "An active Character Note DerivedInfo mutation must be Planned before recall."
                    );
                }
                CharacterNoteDerivedInfoReconcileResult recovered =
                    await ReconcileDerivedInfoWithPodGateOwnedAsync(
                            activeDerivedInfo
                        )
                        .ConfigureAwait(false);
                RequireRecallRecoverySettled(recovered);
                status = _store.ReadStatusSnapshot();
                RequireRecallReady(status);
                if (status.ActiveDerivedInfoWork is not null) {
                    throw new InvalidDataException(
                        "Character Note DerivedInfo recovery did not settle before recall."
                    );
                }
            }

            PodOpenResult observed = OpenDefaultPod();
            if (observed is PodOpenResult.Unavailable unavailable) {
                throw unavailable.Exception;
            }
            if (observed is not PodOpenResult.Available available) {
                throw new InvalidDataException(
                    "The settled Character Note Default Pod is unavailable or invalid."
                );
            }
            if (!string.Equals(
                    available.Identity,
                    status.SettledDefaultPodStateIdentity,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "The opened Character Note Default Pod does not match settled Character Memory authority."
                );
            }
            settledPod = available.Pod;
        }
        finally {
            _podMutationGate.Release();
        }

        return await settledPod.RecallAsync(
                completionClient,
                modelId,
                query,
                options,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static void RequireRecallReady(
        CharacterMemoryStatusSnapshot status
    ) {
        if (status.StoreState is CharacterMemoryStoreState.Quarantined) {
            throw new CharacterMemoryStoreQuarantinedException(
                status.QuarantineCode!
            );
        }
        if (status.StoreState is not CharacterMemoryStoreState.Ready) {
            throw new InvalidDataException(
                "An attached Character Memory store must be Ready for recall."
            );
        }
        if (status.ActiveCapture is not null) {
            throw new InvalidDataException(
                "Character Note recall observed an unsettled ExactText capture."
            );
        }
        if (string.IsNullOrWhiteSpace(
                status.SettledDefaultPodStateIdentity)) {
            throw new InvalidDataException(
                "Character Note recall requires settled Default Pod authority."
            );
        }
    }

    private static void RequireRecallRecoverySettled(
        CharacterNoteDerivedInfoReconcileResult result
    ) {
        switch (result) {
            case CharacterNoteDerivedInfoReconcileResult.Applied:
            case CharacterNoteDerivedInfoReconcileResult.NoWork:
                return;
            case CharacterNoteDerivedInfoReconcileResult.Deferred deferred:
                throw new CharacterNoteDefaultPodAccessException(
                    CharacterNoteDefaultPodFailureKind.IoFailure,
                    "Character Note recall is waiting for DerivedInfo settlement: "
                        + deferred.Code
                );
            case CharacterNoteDerivedInfoReconcileResult.Quarantined
                    quarantined:
                throw new CharacterMemoryStoreQuarantinedException(
                    quarantined.Code
                );
            case CharacterNoteDerivedInfoReconcileResult.Rejected rejected:
                throw new InvalidDataException(
                    "Active Character Note DerivedInfo recovery was rejected: "
                        + rejected.Code
                );
            default:
                throw new InvalidDataException(
                    "Unknown Character Note DerivedInfo reconciliation result."
                );
        }
    }
}
