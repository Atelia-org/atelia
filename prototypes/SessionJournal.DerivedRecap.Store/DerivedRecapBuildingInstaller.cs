using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Engine-bound Building installation authority. The raw head is rechecked
/// while the Store lock is held, after every source has been reread and
/// immediately before the manifest becomes visible.
/// </summary>
public sealed class DerivedRecapBuildingInstaller {
    private readonly DerivedRecapStore _store;
    private readonly SessionJournalEngine _engine;

    public DerivedRecapBuildingInstaller(
        DerivedRecapStore store,
        SessionJournalEngine engine
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        RequireSameBinding(store, engine);
    }

    public ValueTask<CreateBuildingResult> InstallAsync(
        DerivedRecapSetManifest manifest,
        EventAddress expectedRawHead,
        CancellationToken cancellationToken = default
    ) => _store.CreateBuildingTrustedAsync(
        manifest,
        expectedRawHead,
        () => _engine.ReadCurrentHead(),
        cancellationToken
    );

    private static void RequireSameBinding(
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
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
            || store.RefId != engine.BranchRefId) {
            throw new ArgumentException(
                "DerivedRecap Store and SessionJournalEngine must bind "
                + "the same repository and RefId."
            );
        }
    }
}
