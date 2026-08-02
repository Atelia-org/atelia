using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Engine-bound authority for the final Published Restore envelope gate.
/// Component writes remain pending until this facade proves the same
/// caller-frozen raw head and commits the envelope last.
/// </summary>
public sealed class DerivedRecapRestorer {
    private readonly DerivedRecapStore _store;
    private readonly SessionJournalEngine _engine;

    public DerivedRecapRestorer(
        DerivedRecapStore store,
        SessionJournalEngine engine
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        DerivedRecapPublisher.RequireSameBinding(store, engine);
    }

    public ValueTask<PublishedEnvelopeCommitResult>
        CommitEnvelopeAsync(
        PublishedRestoreHandle handle,
        IReadOnlyDictionary<RecapBlockId, string>
            expectedFinalStateTokens,
        EventAddress expectedRawHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(expectedFinalStateTokens);
        DerivedRecapLineageView lineage =
            DerivedRecapLineageView.Capture(
                _store,
                _engine,
                cancellationToken
            );
        if (lineage.CapturedHead != expectedRawHead) {
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
            handle,
            expectedFinalStateTokens,
            lineage,
            expectedRawHead,
            () => _engine.ReadCurrentHead(),
            cancellationToken
        );
    }
}
