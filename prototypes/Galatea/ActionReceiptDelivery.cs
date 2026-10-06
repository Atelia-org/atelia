using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal enum ActionReceiptDeliveryState { Pending, ObservationBound, Delivered }

internal sealed record ActionReceiptDeliverySnapshot(
    string SourceActionAddress,
    ActionReceiptDeliveryState State,
    long CreatedRevision,
    long StateRevision,
    ActionReceiptBatch? FrozenBatch,
    string? ExpectedSessionHead,
    SessionInputContent? BoundInput,
    string? ObservationAddress
);

/// <summary>Each domain owns its confirmation transaction and delivery rows.</summary>
internal interface IActionReceiptDeliveryStore {
    ActionReceiptDeliverySnapshot? ReadPendingReceiptDelivery();
    ActionReceiptDeliverySnapshot? ReadBoundReceiptDelivery();
    ActionReceiptDeliverySnapshot? ReadReceiptDeliveryExact(string source);
    ActionReceiptDeliverySnapshot BindReceiptDelivery(
        string source, long expectedRevision, string expectedHead, SessionInputContent input);
    ActionReceiptDeliverySnapshot RollbackReceiptDelivery(string source, long expectedRevision);
    ActionReceiptDeliverySnapshot CompleteReceiptDelivery(
        string source, long expectedRevision, string observationAddress);
}

/// <summary>
/// The caller holds TurnLock across proof and transition. Receipt delivery
/// means exact Journal append, independently of provider or reply completion.
/// </summary>
internal static class ActionReceiptDelivery {
    internal static void Reconcile(
        IActionReceiptDeliveryStore store, SessionJournalEngine engine,
        CancellationToken cancellationToken = default
    ) {
        if (store.ReadBoundReceiptDelivery() is not { } bound) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        EventAddress head = engine.ReadView.ReadCurrentHead()
            ?? throw Invalid("A bound receipt requires a non-empty journal.");
        var request = new SessionExpectedObservationTurnRequest(
            head,
            EventAddressTextCodec.Parse(bound.ExpectedSessionHead!),
            bound.BoundInput ?? throw Invalid("Bound receipt has no input evidence."),
            bound.ObservationAddress is { } address ? EventAddressTextCodec.Parse(address) : null);
        SessionExpectedObservationTurnReadResult proof = engine.ReadView
            .ProveExpectedObservationTurnAtSelectedHead(request, cancellationToken);
        switch (proof) {
            case SessionExpectedObservationTurnReadResult.NotAppended:
                _ = store.RollbackReceiptDelivery(bound.SourceActionAddress, bound.StateRevision);
                return;
            case SessionExpectedObservationTurnReadResult.InProgress progress:
                Complete(progress.Evidence.ObservationAddress);
                return;
            case SessionExpectedObservationTurnReadResult.Terminal terminal:
                Complete(terminal.Evidence.ObservationAddress);
                return;
            case SessionExpectedObservationTurnReadResult.Terminated terminated:
                Complete(terminated.Evidence.ObservationAddress);
                return;
            default:
                throw Invalid($"Receipt delivery requires exact Observation evidence ({proof.GetType().Name}).");
        }

        void Complete(EventAddress observation) => store.CompleteReceiptDelivery(
            bound.SourceActionAddress, bound.StateRevision, EventAddressTextCodec.Format(observation));
    }

    internal static void Bind(
        IActionReceiptDeliveryStore store, SessionJournalEngine engine,
        ActionReceiptDeliverySnapshot receipt, EventAddress exactBaseHead, SessionInputContent input
    ) {
        if (engine.ReadView.ReadCurrentHead() != exactBaseHead) {
            throw Invalid("Receipt delivery base head changed before binding.");
        }
        _ = store.BindReceiptDelivery(receipt.SourceActionAddress, receipt.StateRevision,
            EventAddressTextCodec.Format(exactBaseHead), input);
    }

    private static GalateaTurnException Invalid(string message) => new(
        message, "action-receipt-evidence-invalid");
}
