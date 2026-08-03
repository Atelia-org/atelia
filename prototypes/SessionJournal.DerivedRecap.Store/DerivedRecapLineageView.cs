using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// One immutable, engine-lifetime-bound raw-lineage authority. The current
/// prefix is bounded to the DerivedRecap contract limit; exact
/// admission-relative prefixes can only be produced through the same bound
/// read view.
/// </summary>
public sealed class DerivedRecapLineageView {
    internal const int MaxHeaderCount = 513;

    private readonly DerivedRecapStore _store;
    private readonly SessionJournalReadView _readView;
    private readonly object _admissionLock = new();
    private readonly Dictionary<
        EventAddress,
        DerivedRecapAdmissionLineageResolution
    > _admissions = [];

    private DerivedRecapLineageView(
        DerivedRecapStore store,
        SessionJournalReadView readView,
        SessionCurrentLineagePrefix currentPrefix
    ) {
        _store = store;
        _readView = readView;
        CurrentPrefix = currentPrefix;
    }

    internal SessionCurrentLineagePrefix CurrentPrefix { get; }

    public SessionCurrentLineagePrefix Prefix {
        get {
            EnsureOwnerAlive();
            return CurrentPrefix;
        }
    }

    public EventAddress CapturedHead {
        get {
            EnsureOwnerAlive();
            return CurrentPrefix.CapturedHead;
        }
    }

    public static DerivedRecapLineageView Capture(
        DerivedRecapStore store,
        SessionJournalReadView readView,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(readView);
        DerivedRecapPublisher.RequireSameBinding(store, readView);
        return new DerivedRecapLineageView(
            store,
            readView,
            readView.ReadCurrentLineagePrefix(
                MaxHeaderCount,
                cancellationToken
            )
        );
    }

    internal DerivedRecapAdmissionLineageResolution ResolveAdmission(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken
    ) {
        EnsureOwnerAlive();
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
                                _readView.ReadLineagePrefixAt(
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
    ) {
        EnsureOwnerAlive();
        return _store.SelectNthPreviousAsync(
            this,
            nthPrevious,
            cancellationToken
        );
    }

    public ValueTask<CurrentLineageBuildingSelection>
        SelectCurrentBuildingAsync(
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _store.SelectCurrentLineageBuildingAsync(
            this,
            cancellationToken
        );
    }

    public ValueTask<PublishedRestoreInspectionResult>
        InspectPublishedForOfflineDiagnosticsAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _store.InspectPublishedForOfflineDiagnosticsAsync(
            admissionAnchor,
            this,
            cancellationToken
        );
    }

    public ValueTask<PublishedRestoreInspectionResult>
        InspectPublishedForRestoreAsync(
        PublishedRestorePlanAuthority authority,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _store.InspectPublishedForRestoreAsync(
            authority,
            this,
            cancellationToken
        );
    }

    private void EnsureOwnerAlive() {
        _ = _readView.BranchRefId;
    }
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
