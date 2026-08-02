using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed partial class SessionJournalEngine {
    /// <summary>
    /// Inspects the exact current raw head and returns only the non-secret
    /// runtime identity required to continue it. A Prepared/Started tail is
    /// fully reconstructed and commitment-verified before any frozen identity
    /// is exposed. This method does not create a runtime, dispatch external
    /// work, or mutate the journal.
    /// </summary>
    public SessionRuntimeRecoveryRequirements
        InspectRuntimeRecoveryRequirements(
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        SessionExecutionRecovery recovery =
            ResolveExecutionTail(cancellationToken);
        return recovery.State.Phase switch {
            SessionExecutionPhase.Empty
                or SessionExecutionPhase.Idle
                or SessionExecutionPhase.TurnFailed =>
                new SessionRuntimeRecoveryRequirements
                    .NoRuntimeRequired(
                        recovery.Head,
                        recovery.State.Phase,
                        recovery.State.HeadKind
                    ),
            SessionExecutionPhase.AwaitingAgentAction =>
                new SessionRuntimeRecoveryRequirements
                    .NewRequestRequired(
                        RequireHead(recovery),
                        recovery.State.Phase,
                        recovery.State.HeadKind
                    ),
            SessionExecutionPhase.AwaitingCompletionDispatch
                or SessionExecutionPhase.AwaitingCompletion =>
                CreateFrozenCompletionRequirement(recovery, cancellationToken),
            SessionExecutionPhase.AwaitingToolExecution =>
                new SessionRuntimeRecoveryRequirements
                    .ToolContinuationRequired(
                        RequireHead(recovery),
                        recovery.State.Phase,
                        recovery.State.HeadKind,
                        recovery.State.PendingToolRuntimeIdentity
                            ?? throw new InvalidDataException(
                                "AwaitingToolExecution requires a durable "
                                + "tool runtime identity."
                            ),
                        recovery.State.PendingToolExecutionStarted
                            ? SessionDurableDispatchState
                                .StartedOutcomeUncertain
                            : SessionDurableDispatchState.NotStarted
                    ),
            _ => throw new InvalidDataException(
                "Unknown SessionJournal runtime recovery phase "
                + $"'{recovery.State.Phase}'."
            )
        };
    }

    private SessionRuntimeRecoveryRequirements
        CreateFrozenCompletionRequirement(
        SessionExecutionRecovery recovery,
        CancellationToken cancellationToken
    ) {
        SessionPreparedRequestReconstruction reconstruction =
            ReconstructPreparedRecovery(recovery, cancellationToken);
        CompletionRequestPreparedBody manifest = reconstruction.Manifest;
        SessionPreparedRuntimeRecoverySnapshot snapshot =
            recovery.PreparedRuntime
                ?? throw new InvalidDataException(
                    "Prepared completion recovery requires a sanitized "
                    + "runtime identity snapshot."
                );
        if (snapshot.CompletionTarget != manifest.Target.Connection
            || !string.Equals(
                snapshot.ClientName,
                manifest.Target.ClientName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                snapshot.ApiSpecId,
                manifest.Target.ApiSpecId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                snapshot.VisibleToolSetSha256,
                manifest.ToolSet.Sha256,
                StringComparison.Ordinal
            )
            || snapshot.ToolRuntimeIdentity != manifest.ToolSet.RuntimeIdentity) {
            throw new InvalidDataException(
                "Prepared reconstruction does not match the resolved runtime identity snapshot."
            );
        }
        return new SessionRuntimeRecoveryRequirements
            .FrozenCompletionRequired(
                RequireHead(recovery),
                recovery.State.Phase,
                recovery.State.HeadKind,
                snapshot.CompletionTarget,
                snapshot.ClientName,
                snapshot.ApiSpecId,
                snapshot.VisibleToolSetSha256,
                snapshot.ToolRuntimeIdentity,
                recovery.State.Phase
                    == SessionExecutionPhase.AwaitingCompletion
                    ? SessionDurableDispatchState
                        .StartedOutcomeUncertain
                    : SessionDurableDispatchState.NotStarted
            );
    }

    private SessionPreparedRequestReconstruction ReconstructPreparedRecovery(
        SessionExecutionRecovery recovery,
        CancellationToken cancellationToken
    ) {
        if (!SessionOperationalSemantics.IsPreparedOrAttemptPhase(
                recovery.State.Phase
            )
            || recovery.Boundary.SourcePrepared is not {
            } sourcePreparedAddress
            || recovery.State.PendingRequestPreparedAddress !=
                sourcePreparedAddress) {
            throw new InvalidDataException(
                "Prepared recovery is missing its exact durable Prepared boundary."
            );
        }

        SessionPreparedRequestReconstruction reconstruction =
            SessionPreparedRequestReconstructor.Reconstruct(
                _reader,
                sourcePreparedAddress,
                cancellationToken
            );
        CompletionRequestPreparedBody manifest = reconstruction.Manifest;
        if (!string.Equals(
                manifest.Origin.CorrelationId,
                recovery.State.ActiveCorrelationId,
                StringComparison.Ordinal
            )
            || manifest.Execution.LastIssuedToolExecutionSequence !=
                recovery.State.ToolExecutionSequenceCheckpoint) {
            throw new InvalidDataException(
                "Prepared reconstruction does not match the resolved execution checkpoint."
            );
        }
        return reconstruction;
    }

    private static EventAddress RequireHead(
        SessionExecutionRecovery recovery
    ) => recovery.Head
        ?? throw new InvalidDataException(
            $"Session phase '{recovery.State.Phase}' requires a raw head."
        );
}
