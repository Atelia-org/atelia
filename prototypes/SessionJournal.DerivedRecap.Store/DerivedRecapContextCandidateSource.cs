using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Projects v8 direct-final publications into SessionJournal's neutral,
/// two-phase context-candidate contract.
/// </summary>
public sealed class DerivedRecapContextCandidateSource
    : ICoherentContextCandidateSource {
    private const string HandlePrefix = "eadr8";
    private const int MaximumOnlineLineageHeaders = 513;

    private readonly DerivedRecapEpochStore _store;
    private readonly SessionJournalReadView _readView;

    public DerivedRecapContextCandidateSource(
        DerivedRecapEpochStore store,
        SessionJournalReadView readView
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readView = readView
            ?? throw new ArgumentNullException(nameof(readView));
        RequireSameBinding(store, readView);
    }

    public async ValueTask<SessionContextCandidateSelection> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateShape();
        SessionCurrentLineagePrefix prefix =
            _readView.ReadCurrentLineagePrefix(
                MaximumOnlineLineageHeaders,
                cancellationToken
            );
        if (prefix.CapturedHead != request.CompletionBoundary) {
            throw new InvalidOperationException(
                "DerivedRecap selection request is stale."
            );
        }
        EventAddress[] headToRoot = [
            .. prefix.HeadToOldest.Select(static item => item.Address)
        ];
        RecapEpochSelectionResult selected =
            await _store.SelectNthPublishedAsync(
                    headToRoot,
                    request.NthPrevious,
                    cancellationToken
                )
                .ConfigureAwait(false);
        RequireCurrentBoundary(request.CompletionBoundary);
        if (selected is RecapEpochSelectionResult.Invalid invalid) {
            return new SessionContextCandidateSelection(
                SessionContextCandidateSelectionStatus
                    .ExactPublishedSetInvalid,
                null,
                invalid.Detail
            );
        }
        if (selected is RecapEpochSelectionResult.Empty) {
            if (!prefix.IsComplete) {
                return SessionContextCandidateSelection.BeyondPrefix(
                    "Published recap selection exceeds the bounded online lineage prefix."
                );
            }
            if (request.NthPrevious > 0) {
                RecapEpochSelectionResult latest =
                    await _store.SelectLatestAsync(
                            headToRoot,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                RequireCurrentBoundary(request.CompletionBoundary);
                if (latest is RecapEpochSelectionResult.Selected) {
                    return new SessionContextCandidateSelection(
                        SessionContextCandidateSelectionStatus
                            .OrdinalUnavailable,
                        null
                    );
                }
                if (latest is RecapEpochSelectionResult.Invalid latestInvalid) {
                    return new SessionContextCandidateSelection(
                        SessionContextCandidateSelectionStatus
                            .ExactPublishedSetInvalid,
                        null,
                        latestInvalid.Detail
                    );
                }
            }
            return new SessionContextCandidateSelection(
                SessionContextCandidateSelectionStatus.EmptyLineage,
                null
            );
        }

        PublishedRecapEpochDescriptor descriptor =
            ((RecapEpochSelectionResult.Selected)selected).Descriptor;
        RecapEpochStoreSnapshot snapshot = await ReadExactPublishedAsync(
                descriptor,
                cancellationToken
            )
            .ConfigureAwait(false);
        RequireCurrentBoundary(request.CompletionBoundary);
        return new SessionContextCandidateSelection(
            SessionContextCandidateSelectionStatus.Selected,
            new SessionContextCandidateDescriptor(
                FormatHandle(descriptor, request.CompletionBoundary),
                descriptor.EnvelopeSha256,
                descriptor.AdmissionAnchor,
                snapshot.EpochInput.AdmissionBoundary.Setups
            )
        );
    }

    public async ValueTask<SessionContextCandidate> MaterializeAsync(
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);
        ParsedHandle handle = ParseHandle(descriptor.Handle);
        if (handle.RefId != _store.RefId
            || handle.AdmissionAnchor != descriptor.SetAdmissionAnchor
            || !string.Equals(
                handle.EnvelopeSha256,
                descriptor.SnapshotToken,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "DerivedRecap candidate descriptor does not match its v8 handle."
            );
        }
        RequireCurrentBoundary(handle.CompletionBoundary);
        var published = new PublishedRecapEpochDescriptor(
            handle.RefId,
            handle.AdmissionAnchor,
            handle.EnvelopeSha256
        );
        RecapEpochStoreSnapshot snapshot = await ReadExactPublishedAsync(
                published,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (snapshot.EpochInput.AdmissionBoundary.Setups
            != descriptor.AnchorSetups) {
            throw new InvalidDataException(
                "DerivedRecap candidate setup references are stale or forged."
            );
        }
        DerivedRecapMaterialization materialization =
            await _store.MaterializeAsync(
                    published,
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

    private async ValueTask<RecapEpochStoreSnapshot>
        ReadExactPublishedAsync(
        PublishedRecapEpochDescriptor descriptor,
        CancellationToken cancellationToken
    ) {
        RecapEpochStoreReadResult read =
            await _store.ReadPublishedForRepairAsync(
                    descriptor.AdmissionAnchor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (read is not RecapEpochStoreReadResult.Available available
            || available.Snapshot.Publication is not { } publication
            || publication.RefId != descriptor.RefId
            || publication.AdmissionAnchor != descriptor.AdmissionAnchor
            || !string.Equals(
                publication.EnvelopeSha256,
                descriptor.EnvelopeSha256,
                StringComparison.Ordinal
            )
            || available.Snapshot.Blocks.Any(static block =>
                block.Final is not RecapEpochFinalHealth.Healthy)) {
            throw new InvalidDataException(
                "Selected v8 Published recap changed or is incomplete."
            );
        }
        return available.Snapshot;
    }

    private void RequireCurrentBoundary(EventAddress expected) {
        EventAddress? observed = _readView.ReadCurrentHead();
        if (observed != expected) {
            throw new InvalidOperationException(
                "DerivedRecap candidate operation became stale. "
                + $"Expected '{expected}', observed '{observed}'."
            );
        }
    }

    private static string FormatHandle(
        PublishedRecapEpochDescriptor descriptor,
        EventAddress completionBoundary
    ) => string.Join(
        ':',
        HandlePrefix,
        descriptor.RefId.ToHexString(),
        EventAddressFileNameCodec.Format(descriptor.AdmissionAnchor),
        EventAddressFileNameCodec.Format(completionBoundary),
        descriptor.EnvelopeSha256
    );

    private static ParsedHandle ParseHandle(string handle) {
        if (string.IsNullOrWhiteSpace(handle)) {
            throw new InvalidDataException(
                "DerivedRecap candidate handle is empty."
            );
        }
        string[] parts = handle.Split(':');
        var parsedRef = parts.Length == 5
            ? RefId.ParseHex(parts[1])
            : default;
        if (parts.Length != 5
            || !string.Equals(parts[0], HandlePrefix,
                StringComparison.Ordinal)
            || parsedRef.IsFailure
            || !EventAddressFileNameCodec.TryParse(
                parts[2],
                out EventAddress admission
            )
            || !EventAddressFileNameCodec.TryParse(
                parts[3],
                out EventAddress boundary
            )
            || parts[4].Length != 64
            || parts[4].Any(static ch =>
                !((ch >= '0' && ch <= '9')
                  || (ch >= 'a' && ch <= 'f')))) {
            throw new InvalidDataException(
                "DerivedRecap candidate handle shape is invalid."
            );
        }
        return new ParsedHandle(
            parsedRef.Value,
            admission,
            boundary,
            parts[4]
        );
    }

    private static void RequireSameBinding(
        DerivedRecapEpochStore store,
        SessionJournalReadView readView
    ) {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (store.RefId != readView.BranchRefId
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(store.SessionRepositoryPath)
                ),
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(readView.Path)
                ),
                comparison
            )) {
            throw new ArgumentException(
                "DerivedRecap candidate Store and raw view must share repository and RefId."
            );
        }
    }

    private sealed record ParsedHandle(
        RefId RefId,
        EventAddress AdmissionAnchor,
        EventAddress CompletionBoundary,
        string EnvelopeSha256
    );
}
