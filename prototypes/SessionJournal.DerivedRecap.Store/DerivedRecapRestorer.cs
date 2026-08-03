using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Engine-lifetime-bound authority for the final Published Restore envelope gate.
/// Component writes remain pending until this facade proves the same
/// caller-frozen raw head and commits the envelope last.
/// </summary>
public sealed class DerivedRecapRestorer {
    private readonly DerivedRecapStore _store;
    private readonly SessionJournalReadView _readView;

    public DerivedRecapRestorer(
        DerivedRecapStore store,
        SessionJournalReadView readView
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readView = readView
            ?? throw new ArgumentNullException(nameof(readView));
        DerivedRecapPublisher.RequireSameBinding(store, readView);
    }

    public ValueTask<PublishedEnvelopeCommitResult>
        CommitEnvelopeAsync(
        PublishedEnvelopeCommitAuthority authority,
        EventAddress expectedRawHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(authority);
        EventAddress? currentHead = _readView.ReadCurrentHead();
        if (currentHead != expectedRawHead) {
            return ValueTask.FromResult<
                PublishedEnvelopeCommitResult
            >(
                new PublishedEnvelopeCommitResult.Stale(
                    "RawHeadChanged",
                    "Current raw lineage does not match the "
                    + "caller-frozen expected head."
                )
            );
        }
        return _store.CommitPublishedEnvelopeTrustedAsync(
            authority,
            expectedRawHead,
            () => _readView.ReadCurrentHead(),
            cancellationToken
        );
    }
}
