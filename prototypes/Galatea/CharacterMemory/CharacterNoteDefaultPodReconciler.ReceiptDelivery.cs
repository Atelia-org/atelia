namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterNoteDefaultPodReconciler {
    internal IActionReceiptDeliveryStore ReceiptDeliveryStore => _store;

    internal ActionReceiptDeliverySnapshot? ReadPendingReceiptDelivery() {
        ThrowIfDisposed();
        return _store.ReadPendingReceiptDelivery();
    }

    internal ActionReceiptDeliverySnapshot? ReadBoundReceiptDelivery() {
        ThrowIfDisposed();
        return _store.ReadBoundReceiptDelivery();
    }

    internal ActionReceiptDeliverySnapshot? ReadReceiptDeliveryExact(string source) {
        ThrowIfDisposed();
        return _store.ReadReceiptDeliveryExact(source);
    }

    internal ActionReceiptDeliverySnapshot BindReceiptDelivery(
        string source, long expectedRevision, string expectedHead, Atelia.SessionJournal.SessionInputContent observation
    ) {
        ThrowIfDisposed();
        return _store.BindReceiptDelivery(source, expectedRevision, expectedHead, observation);
    }

    internal ActionReceiptDeliverySnapshot RollbackReceiptDelivery(string source, long expectedRevision) {
        ThrowIfDisposed();
        return _store.RollbackReceiptDelivery(source, expectedRevision);
    }

    internal ActionReceiptDeliverySnapshot CompleteReceiptDelivery(
        string source, long expectedRevision, string observationAddress
    ) {
        ThrowIfDisposed();
        return _store.CompleteReceiptDelivery(source, expectedRevision, observationAddress);
    }
}
