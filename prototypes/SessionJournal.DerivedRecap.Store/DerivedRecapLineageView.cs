using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// One immutable, engine-bound raw-lineage authority. The current prefix is
/// bounded to the DerivedRecap contract limit; exact admission-relative
/// prefixes can only be produced through the same bound engine.
/// </summary>
public sealed class DerivedRecapLineageView {
    internal const int MaxHeaderCount = 513;

    private readonly DerivedRecapStore _store;
    private readonly SessionJournalEngine _engine;
    private readonly object _admissionLock = new();
    private readonly Dictionary<
        EventAddress,
        DerivedRecapAdmissionLineageResolution
    > _admissions = [];

    private DerivedRecapLineageView(
        DerivedRecapStore store,
        SessionJournalEngine engine,
        SessionCurrentLineagePrefix currentPrefix
    ) {
        _store = store;
        _engine = engine;
        CurrentPrefix = currentPrefix;
    }

    internal SessionCurrentLineagePrefix CurrentPrefix { get; }

    public SessionCurrentLineagePrefix Prefix => CurrentPrefix;

    public EventAddress CapturedHead => CurrentPrefix.CapturedHead;

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
            engine,
            engine.ReadCurrentLineagePrefix(
                MaxHeaderCount,
                cancellationToken
            )
        );
    }

    internal DerivedRecapAdmissionLineageResolution ResolveAdmission(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken
    ) {
        lock (_admissionLock) {
            if (_admissions.TryGetValue(
                    admissionAnchor,
                    out DerivedRecapAdmissionLineageResolution? cached
                )) {
                return cached;
            }
            DerivedRecapAdmissionLineageResolution resolved =
                CurrentPrefix.Lookup(admissionAnchor) switch {
                    SessionCurrentLineageAnchorLookup.Found found =>
                        new DerivedRecapAdmissionLineageResolution
                            .Available(
                                found.Index,
                                _engine.ReadLineagePrefixAt(
                                    admissionAnchor,
                                    MaxHeaderCount,
                                    cancellationToken
                                )
                            ),
                    SessionCurrentLineageAnchorLookup.OffLineage =>
                        new DerivedRecapAdmissionLineageResolution
                            .OffLineage(
                                admissionAnchor,
                                CapturedHead
                            ),
                    SessionCurrentLineageAnchorLookup.BeyondPrefix beyond =>
                        new DerivedRecapAdmissionLineageResolution
                            .BeyondPrefix(beyond.Evidence),
                    _ => throw new InvalidOperationException(
                        "Unknown bounded-lineage lookup result."
                    )
                };
            _admissions.Add(admissionAnchor, resolved);
            return resolved;
        }
    }

    public ValueTask<DerivedRecapSelection> SelectNthPreviousAsync(
        int nthPrevious,
        CancellationToken cancellationToken = default
    ) => _store.SelectNthPreviousAsync(
        this,
        nthPrevious,
        cancellationToken
    );

    public ValueTask<CurrentLineageBuildingSelection>
        SelectCurrentBuildingAsync(
        CancellationToken cancellationToken = default
    ) => _store.SelectCurrentLineageBuildingAsync(
        this,
        cancellationToken
    );

    public ValueTask<PublishedRestoreInspectionResult>
        InspectPublishedForOfflineDiagnosticsAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken = default
    ) => _store.InspectPublishedForOfflineDiagnosticsAsync(
        admissionAnchor,
        this,
        cancellationToken
    );

    public ValueTask<PublishedRestoreInspectionResult>
        InspectPublishedForRestoreAsync(
        PublishedRestorePlanAuthority authority,
        CancellationToken cancellationToken = default
    ) => _store.InspectPublishedForRestoreAsync(
        authority,
        this,
        cancellationToken
    );
}

internal abstract record DerivedRecapAdmissionLineageResolution {
    private DerivedRecapAdmissionLineageResolution() {
    }

    internal sealed record Available(
        int CurrentIndex,
        SessionCurrentLineagePrefix AdmissionPrefix
    ) : DerivedRecapAdmissionLineageResolution;

    internal sealed record OffLineage(
        EventAddress RequiredAnchor,
        EventAddress CapturedHead
    ) : DerivedRecapAdmissionLineageResolution;

    internal sealed record BeyondPrefix(
        SessionCurrentLineageBeyondPrefix Evidence
    ) : DerivedRecapAdmissionLineageResolution;
}
