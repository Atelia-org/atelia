namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterNoteDefaultPodReconciler {
    internal CharacterNoteReceiptDeliverySnapshot? ReadPendingReceiptDelivery() {
        ThrowIfDisposed();
        return _store.ReadPendingReceiptDelivery();
    }

    internal CharacterNoteReceiptDeliverySnapshot? ReadBoundReceiptDelivery() {
        ThrowIfDisposed();
        return _store.ReadBoundReceiptDelivery();
    }

    internal CharacterNoteReceiptDeliverySnapshot? ReadReceiptDeliveryExact(string source) {
        ThrowIfDisposed();
        return _store.ReadReceiptDeliveryExact(source);
    }

    internal CharacterNoteReceiptDeliverySnapshot BindReceiptDelivery(
        string source, long expectedRevision, string expectedHead, string renderedObservation
    ) {
        ThrowIfDisposed();
        return _store.BindReceiptDelivery(source, expectedRevision, expectedHead, renderedObservation);
    }

    internal CharacterNoteReceiptDeliverySnapshot RollbackReceiptDelivery(string source, long expectedRevision) {
        ThrowIfDisposed();
        return _store.RollbackReceiptDelivery(source, expectedRevision);
    }

    internal CharacterNoteReceiptDeliverySnapshot CompleteReceiptDelivery(
        string source, long expectedRevision, string observationAddress
    ) {
        ThrowIfDisposed();
        return _store.CompleteReceiptDelivery(source, expectedRevision, observationAddress);
    }
}
