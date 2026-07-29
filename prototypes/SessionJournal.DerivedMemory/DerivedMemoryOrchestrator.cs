using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedMemory;

/// <summary>
/// Coordinates one exact epoch across independently provisioned maintainers. Producer calls never
/// run under the repository write lock; immutable artifacts and role settlements are durable
/// before the single ArtifactSet CAS publication boundary.
/// </summary>
public sealed class DerivedMemoryOrchestrator {
    private readonly DerivedMemoryRepository _repository;
    private readonly DerivedMemoryMaintainerRunner _runner;

    public DerivedMemoryOrchestrator(
        DerivedMemoryRepository repository
    ) {
        _repository = repository
            ?? throw new ArgumentNullException(nameof(repository));
        _runner = new DerivedMemoryMaintainerRunner(repository);
    }

    public async ValueTask<DerivedMemoryOrchestrationResult> RunAsync(
        SessionJournalEngine engine,
        DerivedMemoryOrchestrationRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Policy);
        ArgumentNullException.ThrowIfNull(request.Roles);
        RequireMatchingRepository(engine);

        DerivedArtifactEpochPlan epoch =
            await ReadAndValidateEpochAuthorityAsync(
                    engine,
                    request.EpochId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        DerivedMemoryRoleExecution[] executions =
            ValidateAndCanonicalizeExecutions(
                request.Policy,
                request.Roles
            );
        DerivedMemoryRoleProvisioning[] provisioning = [
            .. executions.Select(static role => role.Provisioning)
        ];
        DerivedMemoryOrchestrationTransaction transaction =
            await _repository.Orchestrations.GetOrCreateAsync(
                    epoch,
                    request.Policy,
                    provisioning,
                    cancellationToken
                )
                .ConfigureAwait(false);
        DerivedMemoryOrchestrationFinalization? existingFinalization =
            await _repository.Orchestrations.TryReadFinalizationAsync(
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (existingFinalization is not null) {
            IReadOnlyList<DerivedMemoryRoleSettlement>
                finalizedSettlements =
                    await _repository.Orchestrations
                        .ReadSettlementsAsync(
                            transaction,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
            DerivedArtifactSet finalizedSet =
                await CompleteFinalizationAsync(
                        engine,
                        request.Policy,
                        transaction,
                        existingFinalization,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            return new DerivedMemoryOrchestrationResult(
                DerivedMemoryOrchestrationStatus.Published,
                transaction,
                finalizedSettlements,
                Array.Empty<DerivedMemoryRoleFailure>(),
                finalizedSet
            );
        }
        DerivedMemoryMaintainerSnapshot snapshot =
            await _runner.PrepareAsync(
                    engine,
                    epoch.EpochId,
                    cancellationToken
                )
                .ConfigureAwait(false);

        IReadOnlyList<DerivedMemoryRoleSettlement> existing =
            await _repository.Orchestrations.ReadSettlementsAsync(
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
        var settledRoles = existing.ToDictionary(
            static item => item.RoleId,
            StringComparer.Ordinal
        );
        Task<RoleAttempt>[] tasks = [
            .. executions
                .Where(execution => !settledRoles.ContainsKey(
                    execution.Provisioning.RoleId
                ))
                .Select(execution => ExecuteRoleAsync(
                    transaction,
                    snapshot,
                    execution,
                    cancellationToken
                ))
        ];
        RoleAttempt[] attempts = await Task.WhenAll(tasks)
            .ConfigureAwait(false);

        IReadOnlyList<DerivedMemoryRoleSettlement> settlements =
            await _repository.Orchestrations.ReadSettlementsAsync(
                    transaction,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        var failures = attempts
            .Where(static attempt => attempt.Failure is not null)
            .Select(static attempt => attempt.Failure!)
            .OrderBy(static failure => failure.RoleId, StringComparer.Ordinal)
            .ToArray();
        var settlementRoles = settlements
            .Select(static item => item.RoleId)
            .ToHashSet(StringComparer.Ordinal);
        bool requiredComplete = transaction.Roles
            .Where(static role => role.Required)
            .All(role => settlementRoles.Contains(role.RoleId));
        if (!requiredComplete || cancellationToken.IsCancellationRequested) {
            return new DerivedMemoryOrchestrationResult(
                DerivedMemoryOrchestrationStatus.Incomplete,
                transaction,
                settlements,
                Array.AsReadOnly(failures),
                null
            );
        }

        var publication = new DerivedArtifactSetPublicationRequest(
            request.Policy,
            transaction,
            snapshot.AnchorSetups,
            settlements.Select(static settlement =>
                new DerivedArtifactSetMemberSelection(
                    settlement.RoleId,
                    settlement.ArtifactId
                )).ToArray(),
            epoch.InputSetId
        );
        DerivedArtifactSet preparedSet =
            await _repository.ArtifactSets.PreparePublicationAsync(
                    engine,
                    publication,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        DerivedMemoryOrchestrationFinalization finalization =
            await _repository.Orchestrations
                .GetOrCreateFinalizationAsync(
                    transaction,
                    snapshot.AnchorSetups,
                    settlements,
                    preparedSet.SetId,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        DerivedArtifactSet set = await CompleteFinalizationAsync(
                engine,
                request.Policy,
                transaction,
                finalization,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        return new DerivedMemoryOrchestrationResult(
            DerivedMemoryOrchestrationStatus.Published,
            transaction,
            settlements,
            Array.AsReadOnly(failures),
            set
        );
    }

    private async ValueTask<DerivedArtifactSet>
        CompleteFinalizationAsync(
        SessionJournalEngine engine,
        DerivedArtifactSetPolicy policy,
        DerivedMemoryOrchestrationTransaction transaction,
        DerivedMemoryOrchestrationFinalization finalization,
        CancellationToken cancellationToken
    ) {
        DerivedArtifactSet? existing =
            await _repository.ArtifactSets.TryReadExactAsync(
                    finalization.ExpectedSetId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (existing is not null) {
            ValidateFinalizedSet(
                transaction,
                finalization,
                existing
            );
            DerivedArtifactSet? latest =
                await _repository.ArtifactSets.TryReadLatestAsync(
                        policy,
                        transaction.BranchRefId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (latest is null) {
                latest = await _repository.ArtifactSets
                    .RebuildLatestPointerAsync(
                        engine,
                        policy,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            if (latest is not null
                && (string.Equals(
                        latest.SetId,
                        existing.SetId,
                        StringComparison.Ordinal
                    )
                    || await IsDescendantOfAsync(
                            latest,
                            existing.SetId,
                            policy,
                            transaction.BranchRefId,
                            cancellationToken
                        )
                        .ConfigureAwait(false))) {
                return existing;
            }
            if (!string.Equals(
                    latest?.SetId,
                    transaction.InputSetId,
                    StringComparison.Ordinal
                )) {
                throw new DerivedArtifactSetConcurrencyException(
                    $"Finalized set '{existing.SetId}' is not the latest set or an ancestor of it."
                );
            }
        }
        var publication = new DerivedArtifactSetPublicationRequest(
            policy,
            transaction,
            finalization.AnchorSetups,
            finalization.IncludedRoles.Select(
                static settlement =>
                    new DerivedArtifactSetMemberSelection(
                        settlement.RoleId,
                        settlement.ArtifactId
                    )
            ).ToArray(),
            transaction.InputSetId
        );
        DerivedArtifactSet published =
            await _repository.ArtifactSets.PublishAsync(
                    engine,
                    publication,
                    cancellationToken
                )
                .ConfigureAwait(false);
        ValidateFinalizedSet(transaction, finalization, published);
        return published;
    }

    private async ValueTask<bool> IsDescendantOfAsync(
        DerivedArtifactSet descendant,
        string ancestorSetId,
        DerivedArtifactSetPolicy policy,
        RefId branchRefId,
        CancellationToken cancellationToken
    ) {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        DerivedArtifactSet current = descendant;
        while (current.PreviousSetId is { } previousSetId) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current.SetId)) {
                throw new InvalidDataException(
                    "ArtifactSet lineage contains a cycle."
                );
            }
            if (string.Equals(
                    previousSetId,
                    ancestorSetId,
                    StringComparison.Ordinal
                )) {
                return true;
            }
            current = await _repository.ArtifactSets.TryReadAsync(
                    previousSetId,
                    policy,
                    branchRefId,
                    cancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"ArtifactSet '{current.SetId}' references missing previous set '{previousSetId}'."
                );
        }
        return false;
    }

    private static void ValidateFinalizedSet(
        DerivedMemoryOrchestrationTransaction transaction,
        DerivedMemoryOrchestrationFinalization finalization,
        DerivedArtifactSet set
    ) {
        if (!string.Equals(
                set.SetId,
                finalization.ExpectedSetId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                set.TransactionId,
                transaction.TransactionId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                set.JobFingerprint,
                transaction.JobFingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                set.EpochId,
                transaction.EpochId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                set.EpochPlanFingerprint,
                transaction.EpochPlanFingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                set.PreviousSetId,
                transaction.InputSetId,
                StringComparison.Ordinal
            )
            || set.AnchorSetups != finalization.AnchorSetups) {
            throw new InvalidDataException(
                $"Finalized set '{set.SetId}' does not match transaction '{transaction.TransactionId}'."
            );
        }
        DerivedArtifactSetMember[] members = [
            .. set.Members.OrderBy(
                static member => member.RoleId,
                StringComparer.Ordinal
            )
        ];
        DerivedMemoryFinalizedRole[] finalizedRoles = [
            .. finalization.IncludedRoles.OrderBy(
                static role => role.RoleId,
                StringComparer.Ordinal
            )
        ];
        if (members.Length != finalizedRoles.Length
            || members.Where((member, index) =>
                    !string.Equals(
                        member.RoleId,
                        finalizedRoles[index].RoleId,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        member.ArtifactId,
                        finalizedRoles[index].ArtifactId,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        member.Outcome,
                        finalizedRoles[index].ArtifactOutcome,
                        StringComparison.Ordinal
                    ))
                .Any()) {
            throw new InvalidDataException(
                $"Finalized set '{set.SetId}' members do not match its intent."
            );
        }
    }

    private async Task<RoleAttempt> ExecuteRoleAsync(
        DerivedMemoryOrchestrationTransaction transaction,
        DerivedMemoryMaintainerSnapshot snapshot,
        DerivedMemoryRoleExecution execution,
        CancellationToken cancellationToken
    ) {
        DerivedMemoryRoleProvisioning provision = execution.Provisioning;
        try {
            DerivedMemoryArtifact artifact;
            if (string.Equals(
                    provision.ExecutionMode,
                    DerivedMemoryRoleExecutionModes.SelectExisting,
                    StringComparison.Ordinal
                )) {
                artifact =
                    await _repository.Artifacts.TryReadArtifactAsync(
                            provision.SelectedArtifactId!,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Selected artifact '{provision.SelectedArtifactId}' is missing."
                    );
            }
            else {
                IMemoryBlockMaintainer maintainer =
                    execution.Maintainer
                    ?? new IdentityMaintainer(
                        provision.ProfileId,
                        provision.Target
                    );
                DerivedMemoryMaintainerRunResult run =
                    await _runner.RunPreparedAsync(
                            snapshot,
                            new DerivedMemoryMaintainerRunRequest(
                                transaction.EpochId,
                                provision.RoleId,
                                provision.ProfileId,
                                provision.Producer,
                                provision.ProducerFingerprint,
                                provision.PromptFingerprint,
                                provision.ModelFingerprint,
                                provision.CandidateId,
                                provision.AttemptId,
                                string.Equals(
                                    provision.ExecutionMode,
                                    DerivedMemoryRoleExecutionModes.Identity,
                                    StringComparison.Ordinal
                                )
                                    ? DerivedMemoryArtifactOutcomes.Identity
                                    : null
                            ),
                            maintainer,
                            execution.CaptureCallLogPaths,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                artifact = run.Artifact;
            }
            var settlement = new DerivedMemoryRoleSettlement(
                transaction.TransactionId,
                provision.RoleId,
                artifact.ArtifactId,
                artifact.Outcome
            );
            DerivedMemoryRoleSettlement durable =
                await _repository.Orchestrations.SettleAsync(
                        transaction,
                        settlement,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            return new RoleAttempt(durable, null);
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
                and not OutOfMemoryException
        ) {
            return new RoleAttempt(
                null,
                new DerivedMemoryRoleFailure(
                    provision.RoleId,
                    exception.GetType().FullName
                        ?? exception.GetType().Name,
                    exception.Message
                )
            );
        }
    }

    private async ValueTask<DerivedArtifactEpochPlan>
        ReadAndValidateEpochAuthorityAsync(
        SessionJournalEngine engine,
        string epochId,
        CancellationToken cancellationToken
    ) {
        DerivedArtifactEpochPlan epoch =
            await _repository.EpochPlanner.TryReadEpochAsync(
                    epochId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Derived artifact epoch '{epochId}' does not exist."
            );
        DerivedArtifactPlannerConfig config =
            await _repository.EpochPlanner.TryReadConfigAsync(
                    epoch.ConfigId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Epoch planner config '{epoch.ConfigId}' is missing."
            );
        _ = DerivedMemoryEngineReadGate.Run(
            engine,
            () => _repository.EpochPlanner
                .ValidateRawAuthorityDetailed(
                    engine,
                    [epoch],
                    [config],
                    cancellationToken
                )
        );
        return epoch;
    }

    private static DerivedMemoryRoleExecution[]
        ValidateAndCanonicalizeExecutions(
        DerivedArtifactSetPolicy policy,
        IReadOnlyList<DerivedMemoryRoleExecution> executions
    ) {
        ArgumentNullException.ThrowIfNull(executions);
        DerivedMemoryRoleProvisioning[] roles =
            DerivedMemoryOrchestrationStore.ValidateAndCanonicalize(
                policy,
                executions.Select(static item => {
                    ArgumentNullException.ThrowIfNull(item);
                    return item.Provisioning;
                }).ToArray()
            );
        var byRole = executions.ToDictionary(
            static item => item.Provisioning.RoleId,
            StringComparer.Ordinal
        );
        foreach (DerivedMemoryRoleProvisioning role in roles) {
            DerivedMemoryRoleExecution execution = byRole[role.RoleId];
            bool produce = string.Equals(
                role.ExecutionMode,
                DerivedMemoryRoleExecutionModes.Produce,
                StringComparison.Ordinal
            );
            if (produce && execution.Maintainer is null
                || !produce && execution.Maintainer is not null) {
                throw new ArgumentException(
                    $"Role '{role.RoleId}' runtime maintainer does not match execution mode.",
                    nameof(executions)
                );
            }
            if (execution.Maintainer is not null
                && (!string.Equals(
                        execution.Maintainer.Id,
                        role.ProfileId,
                        StringComparison.Ordinal
                    )
                    || execution.Maintainer.Target != role.Target)) {
                throw new ArgumentException(
                    $"Role '{role.RoleId}' runtime maintainer identity does not match provisioning.",
                    nameof(executions)
                );
            }
        }
        return [.. roles.Select(role => byRole[role.RoleId])];
    }

    private void RequireMatchingRepository(SessionJournalEngine engine) {
        string enginePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(engine.Path)
        );
        string repositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                _repository.SessionJournalRepositoryPath
            )
        );
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(enginePath, repositoryPath, comparison)) {
            throw new ArgumentException(
                "SessionJournal engine belongs to a different repository.",
                nameof(engine)
            );
        }
    }

    private sealed record RoleAttempt(
        DerivedMemoryRoleSettlement? Settlement,
        DerivedMemoryRoleFailure? Failure
    );

    private sealed class IdentityMaintainer(
        string id,
        MemoryPackBlockPath target
    ) : IMemoryBlockMaintainer {
        public string Id { get; } = id;
        public MemoryPackBlockPath Target { get; } = target;

        public ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
            MemoryBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new MemoryBlockMaintenanceResult(
                    Id,
                    Target,
                    request.OldBlock
                )
            );
        }
    }
}
