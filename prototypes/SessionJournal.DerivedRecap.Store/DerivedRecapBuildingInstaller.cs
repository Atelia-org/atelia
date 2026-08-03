using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Engine-lifetime-bound Building installation authority. The raw head is
/// rechecked while the Store lock is held, after every source has been reread
/// and immediately before the manifest becomes visible.
/// </summary>
public sealed class DerivedRecapBuildingInstaller {
    private readonly DerivedRecapStore _store;
    private readonly SessionJournalReadView _readView;

    public DerivedRecapBuildingInstaller(
        DerivedRecapStore store,
        SessionJournalReadView readView
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readView = readView
            ?? throw new ArgumentNullException(nameof(readView));
        DerivedRecapPublisher.RequireSameBinding(store, readView);
    }

    public ValueTask<CreateBuildingResult> InstallAsync(
        DerivedRecapSetManifest manifest,
        EventAddress expectedRawHead,
        CancellationToken cancellationToken = default
    ) {
        DerivedRecapLineageView lineage =
            DerivedRecapLineageView.Capture(
                _store,
                _readView,
                cancellationToken
            );
        if (lineage.CapturedHead != expectedRawHead) {
            return ValueTask.FromResult<CreateBuildingResult>(
                new CreateBuildingResult.RawHeadChanged(
                    expectedRawHead,
                    lineage.CapturedHead
                )
            );
        }
        return _store.CreateBuildingTrustedAsync(
            manifest,
            expectedRawHead,
            lineage,
            () => _readView.ReadCurrentHead(),
            cancellationToken
        );
    }

}
