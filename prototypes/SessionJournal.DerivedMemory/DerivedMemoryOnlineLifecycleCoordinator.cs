using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.DerivedMemory;

/// <summary>
/// Generic online composition of the durable epoch planner and orchestration transaction. Concrete
/// role bindings are supplied by the host; this type contains no maintainer catalog or connection
/// policy. It also exposes the same exact ArtifactSet lineage through the two-phase candidate
/// provider contract.
/// </summary>
public sealed class DerivedMemoryOnlineLifecycleCoordinator
    : ISessionMemoryLifecycleCoordinator, ICoherentContextCandidateSource {
    private readonly DerivedMemoryRepository _repository;
    private readonly DerivedArtifactSetPolicy _policy;
    private readonly DerivedMemoryBranchScope _scope;
    private readonly IReadOnlyList<DerivedMemoryRoleExecution> _roles;
    private readonly DerivedMemoryOrchestrator _orchestrator;
    private readonly DerivedArtifactSetContextCandidateSource _candidates;

    public DerivedMemoryOnlineLifecycleCoordinator(
        DerivedMemoryRepository repository,
        DerivedArtifactSetPolicy policy,
        DerivedMemoryBranchScope scope,
        IReadOnlyList<DerivedMemoryRoleExecution> roles
    ) {
        _repository = repository
            ?? throw new ArgumentNullException(nameof(repository));
        _policy = policy
            ?? throw new ArgumentNullException(nameof(policy));
        _repository.RequireScope(scope);
        _scope = scope;
        ArgumentNullException.ThrowIfNull(roles);
        _roles = Array.AsReadOnly(roles.ToArray());
        DerivedMemoryOrchestrationStore.ValidateProvisioningStructure(
            policy,
            _roles.Select(static role => {
                ArgumentNullException.ThrowIfNull(role);
                return role.Provisioning;
            }).ToArray()
        );
        _orchestrator = new(repository);
        _candidates = new(repository, policy, scope);
    }

    public ValueTask<SessionContextCandidateSelection> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) => _candidates.SelectAsync(request, cancellationToken);

    public ValueTask<SessionContextCandidate> MaterializeAsync(
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken
    ) => _candidates.MaterializeAsync(descriptor, cancellationToken);

    public async ValueTask<SessionMemoryLifecycleResult> PrepareAsync(
        SessionJournalEngine engine,
        SessionMemoryLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        _repository.RequireEngine(engine, _scope);
        if (request.Boundary == default
            || request.Phase is not (
                SessionExecutionPhase.Idle
                or SessionExecutionPhase.TurnFailed
                or SessionExecutionPhase.AwaitingAgentAction
            )) {
            throw new ArgumentException(
                "Derived-memory online lifecycle requires an idle, failed, or unprepared completion boundary.",
                nameof(request)
            );
        }
        SessionExecutionBoundaryInspection recovery =
            engine.InspectExecutionBoundary(cancellationToken);
        if (recovery.Head != request.Boundary
            || recovery.Phase != request.Phase) {
            throw new InvalidOperationException(
                "Derived-memory online lifecycle request is stale."
            );
        }

        DerivedArtifactPlannerConfig? config =
            await _repository.EpochPlanner
                .TryReadCurrentConfigAsync(
                    _scope,
                    _policy.CoherenceGroup,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (config is null) {
            return new(
                SessionMemoryLifecycleStatus.Unavailable,
                "No current derived-memory planner config is provisioned."
            );
        }
        DerivedArtifactSet? latestSet =
            await ReadLatestSetStrictAsync(
                    engine,
                    cancellationToken
                )
                .ConfigureAwait(false);
        DerivedArtifactEpochPlan? latestEpoch =
            await _repository.EpochPlanner
                .TryReadLatestEpochAsync(
                    _scope,
                    _policy.CoherenceGroup,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (latestEpoch is null) {
            latestEpoch = await _repository.EpochPlanner
                .RebuildLatestEpochPointerAsync(
                    engine,
                    _policy.CoherenceGroup,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        if (latestSet is not null
            && latestEpoch is null) {
            throw new InvalidDataException(
                "A published ArtifactSet exists without its durable epoch lineage."
            );
        }

        DerivedMemoryOrchestrationResult? orchestration = null;
        if (latestEpoch is not null
            && !string.Equals(
                latestSet?.EpochId,
                latestEpoch.EpochId,
                StringComparison.Ordinal
            )) {
            orchestration = await RunEpochAsync(
                    engine,
                    latestEpoch,
                    cancellationToken
                )
                .ConfigureAwait(false);
            latestSet = orchestration.PublishedSet
                ?? latestSet;
        }
        else {
            DerivedArtifactEpochPlanningResult planned;
            try {
                planned = await _repository.EpochPlanner.PlanAsync(
                        engine,
                        new DerivedArtifactEpochPlanningRequest(
                            _policy.CoherenceGroup,
                            latestEpoch?.EpochId,
                            latestSet?.SetId
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (DerivedArtifactEpochBackpressureException exception) {
                return new(
                    SessionMemoryLifecycleStatus.Backpressure,
                    exception.Message
                );
            }
            if (planned.Epoch is { } epoch) {
                orchestration = await RunEpochAsync(
                        engine,
                        epoch,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                latestSet = orchestration.PublishedSet
                    ?? latestSet;
            }
        }

        long projectedRawTokens = MeasureProjectedRawSuffix(
            engine,
            request,
            latestSet,
            cancellationToken
        );
        if (checked(
                projectedRawTokens
                + config.SchedulingHeadroomTokens
            ) >= config.HardLimitTokens) {
            string failures = orchestration is null
                ? string.Empty
                : string.Join(
                    ", ",
                    orchestration.Failures.Select(
                        static failure => failure.RoleId
                    )
                );
            return new(
                SessionMemoryLifecycleStatus.Backpressure,
                string.IsNullOrEmpty(failures)
                    ? "Derived-memory raw suffix reached the configured hard limit."
                    : "Derived-memory maintenance is incomplete at the configured hard limit; failed roles: "
                        + failures
            );
        }
        return SessionMemoryLifecycleResult.Ready;
    }

    private async ValueTask<DerivedMemoryOrchestrationResult>
        RunEpochAsync(
        SessionJournalEngine engine,
        DerivedArtifactEpochPlan epoch,
        CancellationToken cancellationToken
    ) => await _orchestrator.RunAsync(
            engine,
            new DerivedMemoryOrchestrationRequest(
                epoch.EpochId,
                _policy,
                _roles
            ),
            cancellationToken
        )
        .ConfigureAwait(false);

    private async ValueTask<DerivedArtifactSet?>
        ReadLatestSetStrictAsync(
        SessionJournalEngine engine,
        CancellationToken cancellationToken
    ) {
        DerivedArtifactSet? set =
            await _repository.ArtifactSets.TryReadLatestAsync(
                    _policy,
                    _scope,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return set
            ?? await _repository.ArtifactSets
                .RebuildLatestPointerAsync(
                    engine,
                    _policy,
                    cancellationToken
                )
                .ConfigureAwait(false);
    }

    private static long MeasureProjectedRawSuffix(
        SessionJournalEngine engine,
        SessionMemoryLifecycleRequest request,
        DerivedArtifactSet? latestSet,
        CancellationToken cancellationToken
    ) {
        SessionHistoryPlanningWindow window;
        if (latestSet is null) {
            window = engine.ReadHistoryPlanningWindowAt(
                request.Boundary,
                startExclusive: null,
                cancellationToken
            );
        }
        else {
            SessionHistoryPlanningSeed seed =
                engine.CreateHistoryPlanningSeed(
                    latestSet.CommonAnchor,
                    latestSet.AnchorSetups,
                    cancellationToken
                );
            window = engine.ReadHistoryPlanningWindowAt(
                request.Boundary,
                seed,
                cancellationToken
            );
        }
        long total = 0;
        foreach (SessionHistoryPlanningUnit unit in window.Units) {
            total = checked(
                total
                + SessionHistoryTokenEstimator.Estimate(
                    unit.Message
                )
            );
        }
        if (request.PendingObservation is { } observation) {
            total = checked(
                total
                + SessionHistoryTokenEstimator.Estimate(
                    new ObservationMessage(observation)
                )
            );
        }
        return total;
    }
}
