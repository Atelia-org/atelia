using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed partial class SessionJournalEngine {
    /// <summary>Closes only a safe pending generation frontier; never rewinds input or tool facts.</summary>
    public SessionTurnEndResult EndPendingTurn(EventAddress expectedHead, SessionTurnEndReason reason,
        CancellationToken cancellationToken = default) {
        using MutationLease mutation = EnterMutation(nameof(EndPendingTurn));
        ThrowIfReadOnlyMutation(nameof(EndPendingTurn));
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRetractionHead(expectedHead, nameof(expectedHead));
        if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); }
        EventAddress? observed = _journal.GetHead(_branchRefId);
        if (observed != expectedHead) { return new SessionTurnEndResult.Retryable(expectedHead, observed); }
        SessionExecutionRecovery recovery = ResolveExecutionTail(expectedHead, cancellationToken);
        if (!SessionOperationalSemantics.CanEndTurn(recovery.State.Phase)
            || (recovery.State.Phase == SessionExecutionPhase.TurnFailed && reason != SessionTurnEndReason.Stopped)) {
            return new SessionTurnEndResult.Unavailable(new(expectedHead, recovery.State.Phase, recovery.State.HeadKind));
        }
        EventAddress address = AppendExpected(SessionEventKind.TurnEnded, new TurnEndedBody(reason), expectedHead,
            requireBoundSetupCursor: false);
        return new SessionTurnEndResult.Ended(new(address, reason));
    }
}
