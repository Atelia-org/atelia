using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Production v8 lifecycle: one bounded shared-epoch campaign followed by
/// coherent candidate selection from the same direct-final Store.
/// </summary>
public sealed class DerivedRecapOnlineLifecycleCoordinator
    : ISessionContextLifecycleCoordinator, ICoherentContextCandidateSource {
    private readonly SessionJournalReadView _readView;
    private readonly DerivedRecapEpochCampaignExecutor _campaign;
    private readonly DerivedRecapContextCandidateSource _candidates;

    public DerivedRecapOnlineLifecycleCoordinator(
        SessionJournalReadView readView,
        DerivedRecapEpochStore store,
        RecapEpochPlanningConfiguration configuration,
        RecapEpochOperationLimits limits,
        IRecapBlockMaintainerRegistry maintainers
    ) : this(
        readView,
        store,
        () => configuration,
        limits,
        maintainers
    ) {
        ArgumentNullException.ThrowIfNull(configuration);
    }

    public DerivedRecapOnlineLifecycleCoordinator(
        SessionJournalReadView readView,
        DerivedRecapEpochStore store,
        Func<RecapEpochPlanningConfiguration> configurationFactory,
        RecapEpochOperationLimits limits,
        IRecapBlockMaintainerRegistry maintainers
    ) : this(
        readView,
        store,
        () => new RecapEpochActiveConfiguration(
            configurationFactory(),
            limits,
            store.Limits
        ),
        limits,
        maintainers
    ) {
        ArgumentNullException.ThrowIfNull(configurationFactory);
    }

    public DerivedRecapOnlineLifecycleCoordinator(
        SessionJournalReadView readView,
        DerivedRecapEpochStore store,
        Func<RecapEpochActiveConfiguration> configurationFactory,
        RecapEpochOperationLimits recoveryLimits,
        IRecapBlockMaintainerRegistry maintainers
    ) {
        _readView = readView
            ?? throw new ArgumentNullException(nameof(readView));
        ArgumentNullException.ThrowIfNull(store);
        _campaign = new DerivedRecapEpochCampaignExecutor(
            readView,
            store,
            configurationFactory,
            recoveryLimits,
            maintainers
        );
        _candidates = new DerivedRecapContextCandidateSource(
            store,
            readView
        );
    }

    public ValueTask<SessionContextCandidateSelection> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) => _candidates.SelectAsync(request, cancellationToken);

    public ValueTask<SessionContextCandidateMaterializationResult>
        MaterializeAsync(
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken
    ) => _candidates.MaterializeAsync(descriptor, cancellationToken);

    public async ValueTask<SessionContextLifecycleResult> PrepareAsync(
        SessionJournalReadView readView,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(readView);
        ArgumentNullException.ThrowIfNull(request);
        if (!ReferenceEquals(readView, _readView)) {
            throw new ArgumentException(
                "DerivedRecap lifecycle must use its bound read view.",
                nameof(readView)
            );
        }
        request.Selection.ValidateShape();
        if (request.Boundary != readView.ReadCurrentHead()) {
            throw new InvalidOperationException(
                "DerivedRecap lifecycle request is stale."
            );
        }
        DerivedRecapEpochOperationResult result =
            await _campaign.RunOnlineAsync(cancellationToken)
                .ConfigureAwait(false);
        return MapOperationResult(result);
    }

    internal static SessionContextLifecycleResult MapOperationResult(
        DerivedRecapEpochOperationResult result
    ) {
        ArgumentNullException.ThrowIfNull(result);
        return result switch {
            DerivedRecapEpochOperationResult.Fresh fresh
                when fresh.Latest is null =>
                SessionContextLifecycleResult.RawHistoryAuthorized,
            DerivedRecapEpochOperationResult.Fresh =>
                SessionContextLifecycleResult.Ready,
            DerivedRecapEpochOperationResult.MoreWorkPending pending =>
                new SessionContextLifecycleResult(
                    SessionContextLifecycleStatus.Backpressure,
                    "DerivedRecap shared-epoch campaign has more work "
                    + $"after {pending.EpochsPublished} published epochs."
                ),
            DerivedRecapEpochOperationResult.FullRebuildRequired rebuild =>
                Unavailable(
                    "FullRebuildRequired",
                    $"{rebuild.Reason}: {rebuild.Detail}"
                ),
            DerivedRecapEpochOperationResult.ConfigurationLimit limit =>
                Unavailable("ConfigurationLimit", limit.Detail),
            DerivedRecapEpochOperationResult.Unavailable unavailable =>
                Unavailable(unavailable.Code, unavailable.Detail),
            DerivedRecapEpochOperationResult.BlockFailed failed =>
                Unavailable(
                    failed.Code,
                    $"Block '{failed.RecapBlockId}' failed: {failed.Detail}"
                ),
            _ => throw new InvalidDataException(
                "Unknown DerivedRecap shared-epoch result."
            )
        };
    }

    private static SessionContextLifecycleResult Unavailable(
        string code,
        string detail
    ) => new(
        SessionContextLifecycleStatus.Unavailable,
        $"{code}: {detail}"
    );
}
