using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed partial class SessionJournalEngine {
    /// <summary>
    /// Reconciles Host intent only at the exact captured Idle head. Runtime setup is appended
    /// before system prompt setup and preserves the governing Schema and DerivedContext values.
    /// A race returns Retryable. If the second append cannot win its CAS, the first append remains
    /// durable and a later retry idempotently completes the prompt update.
    /// </summary>
    public SessionDesiredSetupReconciliationResult ReconcileDesiredSetup(
        EventAddress? expectedHead,
        SessionDesiredSetup desired,
        CancellationToken cancellationToken = default
    ) {
        using MutationLease mutation = EnterMutation(
            nameof(ReconcileDesiredSetup)
        );
        ThrowIfReadOnlyMutation(nameof(ReconcileDesiredSetup));
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(desired);
        ValidateRequired(desired.ModelId, nameof(desired.ModelId));
        ValidateRequired(
            desired.CompletionSurfaceId,
            nameof(desired.CompletionSurfaceId)
        );
        if (desired.SystemPrompt is null) {
            throw new ArgumentNullException(nameof(desired.SystemPrompt));
        }

        EventAddress? observedHead = _journal.GetHead(_branchRefId);
        if (observedHead != expectedHead) {
            return new SessionDesiredSetupReconciliationResult.Retryable(
                expectedHead,
                observedHead
            );
        }

        SessionExecutionRecovery recovery = expectedHead is { } head
            ? ResolveExecutionTail(head, cancellationToken)
            : ResolveExecutionTail(cancellationToken);
        if (recovery.Head != expectedHead) {
            return new SessionDesiredSetupReconciliationResult.Retryable(
                expectedHead,
                recovery.Head
            );
        }
        if (recovery.State.Phase == SessionExecutionPhase.Empty) {
            return new SessionDesiredSetupReconciliationResult.Unavailable(
                recovery.Head,
                recovery.State.Phase,
                SessionDesiredSetupUnavailableReason.Unprovisioned
            );
        }
        if (recovery.State.Phase == SessionExecutionPhase.TurnFailed) {
            return new SessionDesiredSetupReconciliationResult.Unavailable(
                recovery.Head,
                recovery.State.Phase,
                SessionDesiredSetupUnavailableReason
                    .FailedTurnMustBeAbandoned
            );
        }
        if (recovery.State.Phase != SessionExecutionPhase.Idle) {
            return new SessionDesiredSetupReconciliationResult.Unavailable(
                recovery.Head,
                recovery.State.Phase,
                SessionDesiredSetupUnavailableReason.ActiveTurn
            );
        }

        EventAddress currentHead = expectedHead
            ?? throw new InvalidDataException(
                "An Idle SessionJournal requires a non-empty head."
            );
        SessionGoverningSetup governing = ResolveGoverningSetup(
            currentHead,
            cancellationToken
        );
        bool runtimeChanged = !string.Equals(
                governing.RuntimeConfig.ModelId,
                desired.ModelId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                governing.RuntimeConfig.CompletionSurfaceId,
                desired.CompletionSurfaceId,
                StringComparison.Ordinal
            );
        bool promptChanged = !string.Equals(
            governing.SystemPrompt,
            desired.SystemPrompt,
            StringComparison.Ordinal
        );

        if (runtimeChanged) {
            cancellationToken.ThrowIfCancellationRequested();
            SessionRuntimeConfiguration replacement =
                governing.RuntimeConfig with {
                    ModelId = desired.ModelId,
                    CompletionSurfaceId = desired.CompletionSurfaceId
                };
            SessionDesiredSetupReconciliationResult.Retryable? retry =
                TryAppendDesiredSetup(
                    SessionEventKind.RuntimeConfigSetup,
                    replacement,
                    currentHead,
                    out EventAddress committed
                );
            if (retry is not null) {
                return retry;
            }
            currentHead = committed;
        }

        if (promptChanged) {
            cancellationToken.ThrowIfCancellationRequested();
            SessionDesiredSetupReconciliationResult.Retryable? retry =
                TryAppendDesiredSetup(
                    SessionEventKind.SystemPromptSetup,
                    new SystemPromptSetupBody(desired.SystemPrompt),
                    currentHead,
                    out EventAddress committed
                );
            if (retry is not null) {
                return retry;
            }
            currentHead = committed;
        }

        return new SessionDesiredSetupReconciliationResult.Ready(
            ResolveGoverningSetup(currentHead, cancellationToken),
            runtimeChanged,
            promptChanged
        );
    }

    private SessionDesiredSetupReconciliationResult.Retryable?
        TryAppendDesiredSetup(
        SessionEventKind kind,
        object body,
        EventAddress expectedHead,
        out EventAddress committed
    ) {
        committed = default;
        try {
            committed = AppendExpected(
                kind,
                body,
                expectedHead,
                requireBoundSetupCursor: false
            );
            return null;
        }
        catch {
            EventAddress? observedHead = _journal.GetHead(_branchRefId);
            if (observedHead != expectedHead) {
                return new SessionDesiredSetupReconciliationResult.Retryable(
                    expectedHead,
                    observedHead
                );
            }
            throw;
        }
    }
}
