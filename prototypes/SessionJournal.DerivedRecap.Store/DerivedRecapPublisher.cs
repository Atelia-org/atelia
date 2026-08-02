using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Engine-bound publication authority for one exact Building plan. Public
/// callers present the metadata-issued BuildingPlanHandle; raw lineage and
/// the final current-head check always come from the bound engine.
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
        BuildingPlanHandle handle,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(handle);
        DerivedRecapLineageView lineage =
            DerivedRecapLineageView.Capture(
                _store,
                _engine,
                cancellationToken
            );
        RecapPublishability result =
            await _store.DiagnosePublishabilityAsync(
                    handle,
                    lineage,
                    cancellationToken
                )
                .ConfigureAwait(false);
        RequireCurrentHead(lineage.CapturedHead);
        return result;
    }

    public ValueTask<PublishRecapResult> PublishAsync(
        BuildingPlanHandle handle,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(handle);
        DerivedRecapLineageView lineage =
            DerivedRecapLineageView.Capture(
                _store,
                _engine,
                cancellationToken
            );
        return _store.PublishTrustedAsync(
            handle,
            lineage,
            () => _engine.ReadCurrentHead(),
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
