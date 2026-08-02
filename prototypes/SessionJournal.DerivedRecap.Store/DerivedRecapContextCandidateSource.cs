using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Adapts one event-addressed Recap Store to SessionJournal's neutral
/// two-phase context-candidate contract. Raw lineage and setup authority are
/// always read from the bound SessionJournalEngine; the Store never opens or
/// mutates the raw journal.
/// </summary>
public sealed class DerivedRecapContextCandidateSource
    : ICoherentContextCandidateSource {
    private const string HandlePrefix = "eadr4";

    private readonly DerivedRecapStore _store;
    private readonly SessionJournalEngine _engine;

    public DerivedRecapContextCandidateSource(
        DerivedRecapStore store,
        SessionJournalEngine engine
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        string storePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(store.SessionRepositoryPath)
        );
        string enginePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(engine.Path)
        );
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(storePath, enginePath, comparison)
            || store.RefId != engine.BranchRefId) {
            throw new ArgumentException(
                "DerivedRecap Store and SessionJournalEngine must bind "
                + "the same repository and RefId."
            );
        }
    }

    public async ValueTask<SessionContextCandidateSelection> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateShape();
        DerivedRecapLineageView lineage =
            DerivedRecapLineageView.Capture(
                _store,
                _engine,
                cancellationToken
            );
        if (lineage.CapturedHead != request.CompletionBoundary) {
            throw new InvalidOperationException(
                "DerivedRecap selection request is stale."
            );
        }
        DerivedRecapSelection result =
            await lineage.SelectNthPreviousAsync(
                    request.NthPrevious,
                    cancellationToken
                )
                .ConfigureAwait(false);
        RequireCurrentBoundary(request.CompletionBoundary);
        switch (result) {
            case DerivedRecapSelection.Selected selected:
                SessionContextAnchorSetupReferences setups =
                    _engine.ResolveContextAnchorSetupReferences(
                        selected.Descriptor.SetAdmissionAnchor,
                        cancellationToken
                    );
                RequireCurrentBoundary(request.CompletionBoundary);
                return new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.Selected,
                    new SessionContextCandidateDescriptor(
                        FormatHandle(
                            selected.Descriptor,
                            request.CompletionBoundary
                        ),
                        selected.Descriptor.EnvelopeSha256,
                        selected.Descriptor.SetAdmissionAnchor,
                        setups
                    )
                );
            case DerivedRecapSelection.EmptyLineage:
                return new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.EmptyLineage,
                    null
                );
            case DerivedRecapSelection.OrdinalUnavailable:
                return new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus
                        .OrdinalUnavailable,
                    null
                );
            case DerivedRecapSelection.ExactPublishedSetInvalid invalid:
                return new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus
                        .ExactPublishedSetInvalid,
                    null,
                    FormatDefects(invalid.Defects)
                );
            case DerivedRecapSelection.BeyondPrefix beyond:
                return SessionContextCandidateSelection.BeyondPrefix(
                    FormatBeyondPrefix(beyond.Evidence)
                );
            case DerivedRecapSelection.StoreUnavailable unavailable:
                return new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus
                        .StoreUnavailable,
                    null,
                    unavailable.Reason
                );
            default:
                throw new InvalidDataException(
                    $"Unknown DerivedRecap selection '{result.GetType().Name}'."
                );
        }
    }

    private static string FormatBeyondPrefix(
        SessionCurrentLineageBeyondPrefix evidence
    ) => "RequiredAnchor=" + evidence.RequiredAnchor
        + ";CapturedHead=" + evidence.CapturedHead
        + ";HeaderCount=" + evidence.HeaderCount
        + ";NextAddress=" + evidence.NextAddress;

    public async ValueTask<SessionContextCandidate> MaterializeAsync(
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);
        ParsedHandle handle = ParseHandle(descriptor.Handle);
        if (handle.RefId != _store.RefId
            || handle.SetAdmissionAnchor
                != descriptor.SetAdmissionAnchor
            || !string.Equals(
                handle.EnvelopeSha256,
                descriptor.SnapshotToken,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "DerivedRecap neutral descriptor does not match its "
                + "opaque handle."
            );
        }
        RequireCurrentBoundary(handle.CompletionBoundary);
        SessionContextAnchorSetupReferences currentSetups =
            _engine.ResolveContextAnchorSetupReferences(
                descriptor.SetAdmissionAnchor,
                cancellationToken
            );
        if (currentSetups != descriptor.AnchorSetups) {
            throw new InvalidDataException(
                "DerivedRecap descriptor setup references are stale "
                + "or forged."
            );
        }
        DerivedRecapMaterialization materialization =
            await _store.MaterializeAsync(
                    new PublishedRecapDescriptor(
                        handle.RefId,
                        handle.SetAdmissionAnchor,
                        handle.EnvelopeSha256
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        RequireCurrentBoundary(handle.CompletionBoundary);
        return new SessionContextCandidate(
            materialization.SetAdmissionAnchor,
            descriptor.AnchorSetups,
            materialization.Contributions
        );
    }

    private void RequireCurrentBoundary(EventAddress expected) {
        EventAddress? observed = _engine.ReadCurrentHead();
        if (observed != expected) {
            throw new InvalidOperationException(
                "DerivedRecap candidate operation became stale. "
                + $"Expected current head '{expected}', observed "
                + $"'{observed}'."
            );
        }
    }

    private static string FormatHandle(
        PublishedRecapDescriptor descriptor,
        EventAddress completionBoundary
    ) => string.Join(
        ':',
        HandlePrefix,
        descriptor.RefId.ToHexString(),
        EventAddressFileNameCodec.Format(
            descriptor.SetAdmissionAnchor
        ),
        EventAddressFileNameCodec.Format(completionBoundary),
        descriptor.EnvelopeSha256
    );

    private static ParsedHandle ParseHandle(string handle) {
        if (string.IsNullOrWhiteSpace(handle)) {
            throw new InvalidDataException(
                "DerivedRecap descriptor handle is empty."
            );
        }
        string[] parts = handle.Split(':');
        if (parts.Length != 5
            || !string.Equals(
                parts[0],
                HandlePrefix,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "DerivedRecap descriptor handle shape is invalid."
            );
        }
        var refResult = RefId.ParseHex(parts[1]);
        if (refResult.IsFailure
            || !EventAddressFileNameCodec.TryParse(
                parts[2],
                out EventAddress anchor
            )
            || !EventAddressFileNameCodec.TryParse(
                parts[3],
                out EventAddress boundary
            )) {
            throw new InvalidDataException(
                "DerivedRecap descriptor handle identity is invalid."
            );
        }
        DerivedRecapCodec.ValidateSha256(
            parts[4],
            "descriptor handle envelope"
        );
        return new ParsedHandle(
            refResult.Unwrap(),
            anchor,
            boundary,
            parts[4]
        );
    }

    private static string FormatDefects(
        IReadOnlyList<RecapStructuralDefect> defects
    ) => string.Join(
        "; ",
        defects.Select(
            static defect => $"{defect.Code}: {defect.Detail}"
        )
    );

    private sealed record ParsedHandle(
        RefId RefId,
        EventAddress SetAdmissionAnchor,
        EventAddress CompletionBoundary,
        string EnvelopeSha256
    );
}
