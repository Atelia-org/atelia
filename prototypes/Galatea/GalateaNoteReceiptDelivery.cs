using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>
/// Reconciles the SQLite receipt outbox against exact raw Observation evidence.
/// The caller owns TurnLock across proof and transition. Delivery means durable
/// append, not successful Completion, and is not undone by subsequent rewind.
/// </summary>
internal static class GalateaNoteReceiptDelivery {
    internal static void Reconcile(
        CharacterNoteDefaultPodReconciler? memory,
        SessionJournalEngine engine,
        CancellationToken cancellationToken = default
    ) {
        if (memory?.ReadBoundReceiptDelivery() is not { } bound) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        EventAddress head = engine.ReadView.ReadCurrentHead()
            ?? throw Invalid("A bound receipt requires a non-empty journal.");
        var request = new SessionExpectedObservationTurnRequest(
            head,
            EventAddressTextCodec.Parse(bound.ExpectedSessionHead!),
            bound.RenderedObservation!,
            bound.ObservationAddress is { } address
                ? EventAddressTextCodec.Parse(address)
                : null
        );
        SessionExpectedObservationTurnReadResult proof = engine.ReadView
            .ProveExpectedObservationTurnAtSelectedHead(request, cancellationToken);
        switch (proof) {
            case SessionExpectedObservationTurnReadResult.NotAppended:
                _ = memory.RollbackReceiptDelivery(
                    bound.SourceActionAddress, bound.StateRevision);
                return;
            case SessionExpectedObservationTurnReadResult.InProgress progress:
                Complete(progress.Evidence.ObservationAddress);
                return;
            case SessionExpectedObservationTurnReadResult.Terminal terminal:
                Complete(terminal.Evidence.ObservationAddress);
                return;
            default:
                throw Invalid(
                    $"Receipt delivery requires exact Observation evidence ({proof.GetType().Name}).");
        }

        void Complete(EventAddress observation) => memory.CompleteReceiptDelivery(
            bound.SourceActionAddress,
            bound.StateRevision,
            EventAddressTextCodec.Format(observation)
        );
    }

    internal static void Bind(
        CharacterNoteDefaultPodReconciler memory,
        SessionJournalEngine engine,
        CharacterNoteReceiptDeliverySnapshot receipt,
        EventAddress exactBaseHead,
        string renderedObservation
    ) {
        if (engine.ReadView.ReadCurrentHead() != exactBaseHead) {
            throw Invalid("Receipt delivery base head changed before binding.");
        }
        _ = memory.BindReceiptDelivery(
            receipt.SourceActionAddress,
            receipt.StateRevision,
            EventAddressTextCodec.Format(exactBaseHead),
            renderedObservation
        );
    }

    private static GalateaTurnException Invalid(string message) => new(
        message, "character-note-receipt-evidence-invalid");
}
