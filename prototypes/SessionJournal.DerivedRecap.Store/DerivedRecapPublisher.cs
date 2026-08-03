using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Engine-lifetime-bound publication authority for one exact Building plan.
/// Public callers present the metadata-issued BuildingPlanHandle; raw lineage
/// and the final current-head check always come from the bound read view.
/// </summary>
public sealed class DerivedRecapPublisher {
    private readonly DerivedRecapStore _store;
    private readonly SessionJournalReadView _readView;

    public DerivedRecapPublisher(
        DerivedRecapStore store,
        SessionJournalReadView readView
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readView = readView
            ?? throw new ArgumentNullException(nameof(readView));
        RequireSameBinding(store, readView);
    }

    public PreparedRecapPublication Prepare(
        BuildingPlanHandle handle,
        EventAddress expectedRawHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(handle);
        DerivedRecapLineageView lineage =
            DerivedRecapLineageView.Capture(
                _store,
                _readView,
                cancellationToken
            );
        RequireCurrentHead(expectedRawHead, lineage.CapturedHead);
        _ = lineage.ResolveAdmission(
            handle.Descriptor.SetAdmissionAnchor,
            cancellationToken
        );
        RequireCurrentHead(expectedRawHead);
        return new PreparedRecapPublication(
            this,
            handle,
            lineage,
            expectedRawHead
        );
    }

    internal ValueTask<RecapPublishability> CanPublishAsync(
        BuildingPlanHandle handle,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(handle);
        DerivedRecapLineageView lineage =
            DerivedRecapLineageView.Capture(
                _store,
                _readView,
                cancellationToken
            );
        return CanPublishAsync(
            CreatePrepared(
                handle,
                lineage,
                lineage.CapturedHead,
                cancellationToken
            ),
            cancellationToken
        );
    }

    public async ValueTask<RecapPublishability> CanPublishAsync(
        PreparedRecapPublication publication,
        CancellationToken cancellationToken = default
    ) {
        ValidatePrepared(publication);
        RequireCurrentHead(publication.ExpectedRawHead);
        RecapPublishability result =
            await _store.DiagnosePublishabilityAsync(
                    publication.Handle,
                    publication.Lineage,
                    cancellationToken
                )
                .ConfigureAwait(false);
        RequireCurrentHead(publication.ExpectedRawHead);
        return result;
    }

    internal ValueTask<PublishRecapResult> PublishAsync(
        BuildingPlanHandle handle,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(handle);
        DerivedRecapLineageView lineage =
            DerivedRecapLineageView.Capture(
                _store,
                _readView,
                cancellationToken
            );
        return PublishAsync(
            CreatePrepared(
                handle,
                lineage,
                lineage.CapturedHead,
                cancellationToken
            ),
            cancellationToken
        );
    }

    public ValueTask<PublishRecapResult> PublishAsync(
        PreparedRecapPublication publication,
        CancellationToken cancellationToken = default
    ) {
        ValidatePrepared(publication);
        RequireCurrentHead(publication.ExpectedRawHead);
        return _store.PublishTrustedAsync(
            publication.Handle,
            publication.Lineage,
            publication.ExpectedRawHead,
            () => _readView.ReadCurrentHead(),
            cancellationToken
        );
    }

    internal async ValueTask<RecapPublishability> CanPublishAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken = default
    ) {
        try {
            BuildingPlanReadResult read =
                await _store.ReadBuildingPlanAsync(
                        admissionAnchor,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            return read switch {
                BuildingPlanReadResult.Available available =>
                    await CanPublishAsync(
                            available.Snapshot.Handle,
                            cancellationToken
                        )
                        .ConfigureAwait(false),
                BuildingPlanReadResult.Invalid invalid =>
                    new RecapPublishability.NotPublishable(
                        invalid.Defects
                    ),
                BuildingPlanReadResult.Missing =>
                    new RecapPublishability.NotPublishable([
                        new RecapStructuralDefect(
                            "BuildingMissing",
                            "Exact Building directory is missing."
                        )
                    ]),
                _ => throw new InvalidDataException(
                    "Unknown Building plan read result."
                )
            };
        }
        catch (Exception exception)
            when (IsStoreAvailabilityException(exception)) {
            return new RecapPublishability.StoreUnavailable(
                exception.Message
            );
        }
    }

    internal async ValueTask<PublishRecapResult> PublishAsync(
        EventAddress admissionAnchor,
        CancellationToken cancellationToken = default
    ) {
        try {
            BuildingPlanReadResult read =
                await _store.ReadBuildingPlanAsync(
                        admissionAnchor,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            return read switch {
                BuildingPlanReadResult.Available available =>
                    await PublishAsync(
                            available.Snapshot.Handle,
                            cancellationToken
                        )
                        .ConfigureAwait(false),
                BuildingPlanReadResult.Invalid invalid =>
                    new PublishRecapResult.NotPublishable(
                        invalid.Defects
                    ),
                BuildingPlanReadResult.Missing =>
                    new PublishRecapResult.NotPublishable([
                        new RecapStructuralDefect(
                            "BuildingMissing",
                            "Exact Building directory is missing."
                        )
                    ]),
                _ => throw new InvalidDataException(
                    "Unknown Building plan read result."
                )
            };
        }
        catch (Exception exception)
            when (IsStoreAvailabilityException(exception)) {
            return new PublishRecapResult.StoreUnavailable(
                exception.Message
            );
        }
    }

    private static bool IsStoreAvailabilityException(
        Exception exception
    ) => exception is InvalidDataException
        or IOException
        or UnauthorizedAccessException
        or System.Security.SecurityException
        or NotSupportedException;

    private PreparedRecapPublication CreatePrepared(
        BuildingPlanHandle handle,
        DerivedRecapLineageView lineage,
        EventAddress expectedRawHead,
        CancellationToken cancellationToken
    ) {
        _ = lineage.ResolveAdmission(
            handle.Descriptor.SetAdmissionAnchor,
            cancellationToken
        );
        return new PreparedRecapPublication(
            this,
            handle,
            lineage,
            expectedRawHead
        );
    }

    private void ValidatePrepared(PreparedRecapPublication publication) {
        ArgumentNullException.ThrowIfNull(publication);
        if (!ReferenceEquals(publication.Owner, this)) {
            throw new ArgumentException(
                "Prepared publication belongs to another Publisher.",
                nameof(publication)
            );
        }
    }

    private void RequireCurrentHead(EventAddress expected) {
        EventAddress? observed = _readView.ReadCurrentHead();
        RequireCurrentHead(expected, observed);
    }

    private static void RequireCurrentHead(
        EventAddress expected,
        EventAddress? observed
    ) {
        if (observed == expected) {
            return;
        }
        throw new InvalidOperationException(
            "Raw SessionJournal head changed during Recap publication "
            + $"preparation or diagnosis. Expected '{expected}', observed "
            + $"'{observed}'."
        );
    }

    internal static void RequireSameBinding(
        DerivedRecapStore store,
        SessionJournalReadView readView
    ) {
        string storePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(store.SessionRepositoryPath)
        );
        string readViewPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(readView.Path)
        );
        if (!string.Equals(
                storePath,
                readViewPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
            || store.RefId != readView.BranchRefId) {
            throw new ArgumentException(
                "DerivedRecap Store and SessionJournalReadView must bind "
                + "the same repository and RefId."
            );
        }
    }
}
