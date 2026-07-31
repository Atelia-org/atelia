using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed partial class SessionJournalEngine {
    /// <summary>
    /// Inspects the exact current raw head and returns only the non-secret
    /// runtime identity required to continue it. This method does not
    /// reconstruct a prepared request, create a runtime, dispatch external
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
                CreateFrozenCompletionRequirement(recovery),
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

    private static SessionRuntimeRecoveryRequirements
        CreateFrozenCompletionRequirement(
        SessionExecutionRecovery recovery
    ) {
        SessionPreparedRuntimeRecoverySnapshot snapshot =
            recovery.PreparedRuntime
                ?? throw new InvalidDataException(
                    "Prepared completion recovery requires a sanitized "
                    + "runtime identity snapshot."
                );
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

    private static EventAddress RequireHead(
        SessionExecutionRecovery recovery
    ) => recovery.Head
        ?? throw new InvalidDataException(
            $"Session phase '{recovery.State.Phase}' requires a raw head."
        );
}
