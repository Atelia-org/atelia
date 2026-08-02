using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Engine-bound publication authority for one Recap Store. Callers choose
/// only the target admission anchor; raw lineage and the final current-head
/// check always come from the bound SessionJournalEngine.
/// </summary>
public sealed class DerivedRecapPublisher {
    private readonly DerivedRecapStore _store;
    private readonly SessionJournalEngine _engine;

    public DerivedRecapPublisher(
        DerivedRecapStore store,
        SessionJournalEngine engine
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        RequireSameBinding(store, engine);
    }

    public async ValueTask<RecapPublishability> CanPublishAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken = default
    ) {
        DerivedRecapLineageView lineage =
            DerivedRecapLineageView.Capture(
                _store,
                _engine,
                cancellationToken
            );
        RecapPublishability result =
            await _store.DiagnosePublishabilityAsync(
                    admissionAnchor,
                    lineage,
                    cancellationToken
                )
                .ConfigureAwait(false);
        RequireCurrentHead(lineage.CapturedHead);
        return result;
    }

    public ValueTask<PublishRecapResult> PublishAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken = default
    ) {
        DerivedRecapLineageView lineage =
            DerivedRecapLineageView.Capture(
                _store,
                _engine,
                cancellationToken
            );
        return _store.PublishTrustedAsync(
            admissionAnchor,
            lineage,
            () => _engine.ReadCurrentHead(),
            cancellationToken
        );
    }

    private void RequireCurrentHead(EventAddress expected) {
        EventAddress? observed = _engine.ReadCurrentHead();
        if (observed != expected) {
            throw new InvalidOperationException(
                "Raw SessionJournal head changed during Recap "
                + $"diagnosis. Expected '{expected}', observed "
                + $"'{observed}'."
            );
        }
    }

    internal static void RequireSameBinding(
        DerivedRecapStore store,
        SessionJournalEngine engine
    ) {
        string storePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(store.SessionRepositoryPath)
        );
        string enginePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(engine.Path)
        );
        if (!string.Equals(
                storePath,
                enginePath,
                StringComparison.Ordinal
            )
            || store.RefId != engine.BranchRefId) {
            throw new ArgumentException(
                "DerivedRecap Store and SessionJournalEngine must bind "
                + "the same repository and RefId."
            );
        }
    }
}
