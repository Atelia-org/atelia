using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// One immutable raw-lineage capture produced by a bound
/// <see cref="SessionJournalEngine"/>. Store lineage-dependent reads accept
/// authority only through this view, never through a caller-constructed
/// <see cref="SessionCurrentLineageSnapshot"/>.
/// </summary>
public sealed class DerivedRecapLineageView {
    private readonly DerivedRecapStore _store;

    private DerivedRecapLineageView(
        DerivedRecapStore store,
        SessionCurrentLineageSnapshot snapshot
    ) {
        _store = store;
        Snapshot = snapshot;
    }

    public SessionCurrentLineageSnapshot Snapshot { get; }

    public EventAddress CapturedHead => Snapshot.CapturedHead;

    public static DerivedRecapLineageView Capture(
        DerivedRecapStore store,
        SessionJournalEngine engine,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engine);
        DerivedRecapPublisher.RequireSameBinding(store, engine);
        return new DerivedRecapLineageView(
            store,
            engine.ReadCurrentLineageHeaders(cancellationToken)
        );
    }

    public ValueTask<DerivedRecapSelection> SelectNthPreviousAsync(
        int nthPrevious,
        CancellationToken cancellationToken = default
    ) => _store.SelectNthPreviousAsync(
        Snapshot,
        nthPrevious,
        cancellationToken
    );

    public ValueTask<CurrentLineageBuildingSelection>
        SelectCurrentBuildingAsync(
        CancellationToken cancellationToken = default
    ) => _store.SelectCurrentLineageBuildingAsync(
        Snapshot,
        cancellationToken
    );

    public ValueTask<PublishedRestoreInspectionResult>
        InspectPublishedForRestoreAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken = default
    ) => _store.InspectPublishedForRestoreAsync(
        admissionAnchor,
        Snapshot,
        cancellationToken
    );
}
