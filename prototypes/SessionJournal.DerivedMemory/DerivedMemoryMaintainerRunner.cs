using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedMemory;

public sealed record DerivedMemoryMaintainerRunRequest(
    string EpochId,
    string RoleId,
    string ProfileId,
    string Producer,
    string ProducerFingerprint,
    string PromptFingerprint,
    string ModelFingerprint,
    string CandidateId,
    string AttemptId,
    string? OutcomeOverride = null
);

public sealed record DerivedMemoryMaintainerRunResult(
    DerivedArtifactEpochPlan Epoch,
    DerivedMemoryArtifact Artifact,
    MemoryPackBlock OldBlock,
    MemoryBlockMaintenanceResult MaintenanceResult,
    SessionHistoryPlanningDiagnostics ReadDiagnostics
);

public sealed class DerivedMemoryMaintainerSnapshot {
    internal DerivedMemoryMaintainerSnapshot(
        DerivedArtifactEpochPlan epoch,
        MemoryPack inputMemoryPack,
        IReadOnlyList<DerivedMemoryArtifactInputMember> inputMembers,
        RecentHistorySlice recentHistory,
        SessionContextAnchorSetupReferences anchorSetups,
        SessionHistoryPlanningDiagnostics readDiagnostics
    ) {
        Epoch = epoch;
        InputMemoryPack = inputMemoryPack;
        InputMembers = inputMembers;
        RecentHistory = recentHistory;
        AnchorSetups = anchorSetups;
        ReadDiagnostics = readDiagnostics;
    }

    public DerivedArtifactEpochPlan Epoch { get; }
    public RecentHistorySlice RecentHistory { get; }
    public SessionContextAnchorSetupReferences AnchorSetups { get; }
    public SessionHistoryPlanningDiagnostics ReadDiagnostics { get; }
    internal MemoryPack InputMemoryPack { get; }
    internal IReadOnlyList<DerivedMemoryArtifactInputMember> InputMembers {
        get;
    }
}

/// <summary>
/// Runs one concrete maintainer against one immutable durable epoch. It neither plans/splits
/// history nor advances an epoch or ArtifactSet pointer.
/// </summary>
public sealed class DerivedMemoryMaintainerRunner {
    private readonly DerivedMemoryRepository _repository;

    public DerivedMemoryMaintainerRunner(
        DerivedMemoryRepository repository
    ) {
        _repository = repository
            ?? throw new ArgumentNullException(nameof(repository));
    }

    public async ValueTask<DerivedMemoryMaintainerRunResult> RunAsync(
        SessionJournalEngine engine,
        DerivedMemoryMaintainerRunRequest request,
        IMemoryBlockMaintainer maintainer,
        Func<IReadOnlyList<string>>? captureCallLogPaths = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(maintainer);
        DerivedMemoryMaintainerSnapshot snapshot = await PrepareAsync(
                engine,
                request.EpochId,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await RunPreparedAsync(
                snapshot,
                request,
                maintainer,
                captureCallLogPaths,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<DerivedMemoryMaintainerSnapshot> PrepareAsync(
        SessionJournalEngine engine,
        string epochId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        RequireToken(epochId, nameof(epochId));
        RequireMatchingRepository(engine);
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
                $"Epoch '{epoch.EpochId}' planner config '{epoch.ConfigId}' is missing."
            );
        if (config.Key != epoch.Key
            || !string.Equals(
                config.TopologyVersion,
                epoch.TopologyVersion,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Epoch '{epoch.EpochId}' planner config does not match its durable identity."
            );
        }

        InputSnapshot input = await RestoreInputSnapshotAsync(
                epoch,
                cancellationToken
            )
            .ConfigureAwait(false);

        SessionHistoryPlanningWindow window =
            DerivedMemoryEngineReadGate.Run(engine, () => {
                SessionHistoryPlanningSeed planningSeed =
                    engine.CreateHistoryPlanningSeed(
                        epoch.SourceStartExclusive,
                        epoch.RawStartSetups,
                        cancellationToken
                    );
                return engine.ReadHistoryPlanningWindowAt(
                    epoch.SourceEndInclusive,
                    planningSeed,
                    cancellationToken
                );
            });
        ValidateExactWindow(epoch, window);

        var recentHistory = new RecentHistorySlice(
            ContextHeaderSnapshot.FromRenderedMemoryPack(
                input.MemoryPack.Render()
            ),
            Array.AsReadOnly([
                .. window.Units.Select(static unit => unit.Message)
            ]),
            SourceId: epoch.EpochId,
            EstimatedTokens: checked((ulong)epoch.MeasuredTokens)
        );
        return new DerivedMemoryMaintainerSnapshot(
            epoch,
            input.MemoryPack,
            input.InputMembers,
            recentHistory,
            window.EndSetups,
            window.Diagnostics
        );
    }

    public async ValueTask<DerivedMemoryMaintainerRunResult>
        RunPreparedAsync(
        DerivedMemoryMaintainerSnapshot snapshot,
        DerivedMemoryMaintainerRunRequest request,
        IMemoryBlockMaintainer maintainer,
        Func<IReadOnlyList<string>>? captureCallLogPaths = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(maintainer);
        ValidateRequest(request);
        if (!string.Equals(
                snapshot.Epoch.EpochId,
                request.EpochId,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "Prepared snapshot does not match the requested epoch.",
                nameof(snapshot)
            );
        }
        if (!string.Equals(
                maintainer.Id,
                request.ProfileId,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "Maintainer id does not match the requested profile id.",
                nameof(maintainer)
            );
        }
        DerivedMemoryArtifactInputMember? previousRole =
            snapshot.InputMembers.SingleOrDefault(member =>
                string.Equals(
                    member.RoleId,
                    request.RoleId,
                    StringComparison.Ordinal
                ));
        if (previousRole is not null
            && previousRole.Target != maintainer.Target) {
            throw new InvalidDataException(
                $"Input role '{request.RoleId}' target does not match the maintainer."
            );
        }
        MemoryPackBlock oldBlock = snapshot.InputMemoryPack.TryGetBlock(
            maintainer.Target,
            out MemoryPackBlock? found
        )
            ? found
            : new MemoryPackBlock(string.Empty);
        MemoryBlockMaintenanceResult maintenanceResult =
            await maintainer.MaintainAsync(
                    new MemoryBlockMaintenanceRequest(
                        snapshot.RecentHistory,
                        oldBlock
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        ValidateMaintenanceResult(maintainer, maintenanceResult);

        var draft = new MemoryPackDraft(snapshot.InputMemoryPack);
        draft.UpsertBlock(
            maintenanceResult.Target,
            maintenanceResult.NewBlock.Text
        );
        MemoryPack updatedPack = draft.Build();
        IReadOnlyList<string> callLogPaths =
            captureCallLogPaths?.Invoke()
            ?? Array.Empty<string>();

        string outcome = request.OutcomeOverride
            ?? (string.Equals(
                    oldBlock.Text,
                    maintenanceResult.NewBlock.Text,
                    StringComparison.Ordinal
                )
                ? DerivedMemoryArtifactOutcomes.Unchanged
                : DerivedMemoryArtifactOutcomes.Changed);
        var artifactRequest = new DerivedMemoryArtifactWriteRequest(
            snapshot.Epoch.EpochId,
            GetEpochPlanFingerprint(snapshot.Epoch),
            request.RoleId,
            request.ProfileId,
            request.Producer,
            request.ProducerFingerprint,
            request.PromptFingerprint,
            request.ModelFingerprint,
            request.CandidateId,
            request.AttemptId,
            snapshot.Epoch.PlannedAtRawHead,
            snapshot.Epoch.SourceStartExclusive,
            snapshot.Epoch.SourceEndInclusive,
            snapshot.Epoch.SourceEndInclusive,
            snapshot.Epoch.RawStartSetups,
            snapshot.AnchorSetups,
            snapshot.Epoch.InputSetId,
            previousRole?.ArtifactId,
            snapshot.InputMembers,
            maintainer.Target,
            updatedPack,
            maintenanceResult.Invocation,
            callLogPaths
        ) with {
            Outcome = outcome
        };
        DerivedMemoryArtifact artifact =
            await _repository.Artifacts.WriteCandidateAsync(
                    artifactRequest,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return new DerivedMemoryMaintainerRunResult(
            snapshot.Epoch,
            artifact,
            oldBlock,
            maintenanceResult,
            snapshot.ReadDiagnostics
        );
    }

    public static string GetEpochPlanFingerprint(
        DerivedArtifactEpochPlan epoch
    ) {
        ArgumentNullException.ThrowIfNull(epoch);
        if (epoch.EpochId.Length != 68
            || !epoch.EpochId.StartsWith("dae_", StringComparison.Ordinal)) {
            throw new ArgumentException(
                "Epoch has an invalid durable identity.",
                nameof(epoch)
            );
        }
        return $"sha256:{epoch.EpochId[4..]}";
    }

    private async ValueTask<InputSnapshot> RestoreInputSnapshotAsync(
        DerivedArtifactEpochPlan epoch,
        CancellationToken cancellationToken
    ) {
        if (epoch.InputSetId is null) {
            if (epoch.PreviousEpochId is not null) {
                throw new InvalidDataException(
                    $"Non-genesis epoch '{epoch.EpochId}' has no input set."
                );
            }
            return new InputSnapshot(
                new MemoryPack(),
                Array.Empty<DerivedMemoryArtifactInputMember>()
            );
        }

        DerivedArtifactSet inputSet =
            await _repository.ArtifactSets.TryReadExactAsync(
                    epoch.InputSetId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Epoch '{epoch.EpochId}' input set '{epoch.InputSetId}' is missing."
            );
        if (inputSet.CommonAnchor != epoch.SourceStartExclusive
            || inputSet.AnchorSetups != epoch.RawStartSetups
            || inputSet.BranchRefId != epoch.BranchRefId
            || !string.Equals(
                inputSet.CoherenceGroup,
                epoch.CoherenceGroup,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Epoch '{epoch.EpochId}' input set does not match its exact start boundary and planner key."
            );
        }

        var memoryPack = new MemoryPack();
        var draft = new MemoryPackDraft(memoryPack);
        var inputMembers =
            new List<DerivedMemoryArtifactInputMember>(
                inputSet.Members.Count
            );
        foreach (DerivedArtifactSetMember member in inputSet.Members
                     .OrderBy(
                         static member => member.RoleId,
                         StringComparer.Ordinal
                     )) {
            DerivedMemoryArtifact artifact =
                await _repository.Artifacts.TryReadArtifactAsync(
                        member.ArtifactId,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Input member artifact '{member.ArtifactId}' is missing."
                );
            if (!string.Equals(
                    artifact.Content,
                    artifact.MemoryPack.TryGetBlock(
                        member.Target,
                        out MemoryPackBlock? block
                    )
                        ? block.Text
                        : null,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Input member artifact '{member.ArtifactId}' content is inconsistent."
                );
            }
            draft.UpsertBlock(member.Target, artifact.Content);
            inputMembers.Add(new DerivedMemoryArtifactInputMember(
                member.RoleId,
                member.ArtifactId,
                member.Target,
                member.ContentSha256
            ));
        }
        MemoryPack restored = draft.Build();
        return new InputSnapshot(
            restored,
            inputMembers.AsReadOnly()
        );
    }

    private static void ValidateExactWindow(
        DerivedArtifactEpochPlan epoch,
        SessionHistoryPlanningWindow window
    ) {
        if (window.ObservedRawHead != epoch.SourceEndInclusive
            || window.StartExclusive != epoch.SourceStartExclusive
            || window.StartSetups != epoch.RawStartSetups
            || window.Units.Count == 0
            || window.Units[^1].SourceEndInclusive
                != epoch.SourceEndInclusive) {
            throw new InvalidDataException(
                $"Epoch '{epoch.EpochId}' exact raw range could not be reproduced."
            );
        }
    }

    private static void ValidateMaintenanceResult(
        IMemoryBlockMaintainer maintainer,
        MemoryBlockMaintenanceResult result
    ) {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(
                result.MaintainerId,
                maintainer.Id,
                StringComparison.Ordinal
            )
            || result.Target != maintainer.Target) {
            throw new InvalidDataException(
                $"Maintainer '{maintainer.Id}' returned mismatched identity."
            );
        }
    }

    private static void ValidateRequest(
        DerivedMemoryMaintainerRunRequest request
    ) {
        RequireToken(request.RoleId, nameof(request.RoleId));
        RequireToken(request.ProfileId, nameof(request.ProfileId));
        RequireToken(request.Producer, nameof(request.Producer));
        RequireToken(request.CandidateId, nameof(request.CandidateId));
        RequireToken(request.AttemptId, nameof(request.AttemptId));
        if (request.OutcomeOverride is not null
            && !string.Equals(
                request.OutcomeOverride,
                DerivedMemoryArtifactOutcomes.Identity,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "Only the explicit identity outcome may override producer result classification.",
                nameof(request.OutcomeOverride)
            );
        }
        RequireFingerprint(
            request.ProducerFingerprint,
            nameof(request.ProducerFingerprint)
        );
        RequireFingerprint(
            request.PromptFingerprint,
            nameof(request.PromptFingerprint)
        );
        RequireFingerprint(
            request.ModelFingerprint,
            nameof(request.ModelFingerprint)
        );
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
        if (!string.Equals(
                enginePath,
                repositoryPath,
                comparison
            )) {
            throw new ArgumentException(
                "SessionJournal engine belongs to a different repository.",
                nameof(engine)
            );
        }
    }

    private static void RequireToken(string value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Contains('\0', StringComparison.Ordinal)) {
            throw new ArgumentException(
                "Identity token must contain 1 through 256 non-NUL characters.",
                parameterName
            );
        }
    }

    private static void RequireFingerprint(
        string value,
        string parameterName
    ) {
        if (value is not { Length: 71 }
            || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || !value[7..].All(
                static ch => ch is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
            )) {
            throw new ArgumentException(
                "Fingerprint must be canonical lowercase sha256.",
                parameterName
            );
        }
    }

    private sealed record InputSnapshot(
        MemoryPack MemoryPack,
        IReadOnlyList<DerivedMemoryArtifactInputMember> InputMembers
    );
}
