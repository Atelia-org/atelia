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
    string AttemptId
);

public sealed record DerivedMemoryMaintainerRunResult(
    DerivedArtifactEpochPlan Epoch,
    DerivedMemoryArtifact Artifact,
    MemoryPackBlock OldBlock,
    MemoryBlockMaintenanceResult MaintenanceResult,
    SessionHistoryPlanningDiagnostics ReadDiagnostics
);

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
        ValidateRequest(request);
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

        RequireMatchingRepository(engine);
        DerivedArtifactEpochPlan epoch =
            await _repository.EpochPlanner.TryReadEpochAsync(
                    request.EpochId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Derived artifact epoch '{request.EpochId}' does not exist."
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
                request.RoleId,
                maintainer.Target,
                cancellationToken
            )
            .ConfigureAwait(false);

        SessionHistoryPlanningSeed planningSeed =
            engine.CreateHistoryPlanningSeed(
                epoch.SourceStartExclusive,
                epoch.RawStartSetups,
                cancellationToken
            );
        SessionHistoryPlanningWindow window =
            engine.ReadHistoryPlanningWindowAt(
                epoch.SourceEndInclusive,
                planningSeed,
                cancellationToken
            );
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
        MemoryBlockMaintenanceResult maintenanceResult =
            await maintainer.MaintainAsync(
                    new MemoryBlockMaintenanceRequest(
                        recentHistory,
                        input.OldBlock
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        ValidateMaintenanceResult(maintainer, maintenanceResult);

        var draft = new MemoryPackDraft(input.MemoryPack);
        draft.UpsertBlock(
            maintenanceResult.Target,
            maintenanceResult.NewBlock.Text
        );
        MemoryPack updatedPack = draft.Build();
        IReadOnlyList<string> callLogPaths =
            captureCallLogPaths?.Invoke()
            ?? Array.Empty<string>();

        var artifactRequest = new DerivedMemoryArtifactWriteRequest(
            epoch.EpochId,
            GetEpochPlanFingerprint(epoch),
            request.RoleId,
            request.ProfileId,
            request.Producer,
            request.ProducerFingerprint,
            request.PromptFingerprint,
            request.ModelFingerprint,
            request.CandidateId,
            request.AttemptId,
            epoch.PlannedAtRawHead,
            epoch.SourceStartExclusive,
            epoch.SourceEndInclusive,
            epoch.SourceEndInclusive,
            epoch.RawStartSetups,
            window.EndSetups,
            epoch.InputSetId,
            input.PreviousRoleArtifact,
            input.InputMembers,
            maintainer.Target,
            updatedPack,
            maintenanceResult.Invocation,
            callLogPaths
        );
        DerivedMemoryArtifact artifact =
            await _repository.Artifacts.WriteCandidateAsync(
                    artifactRequest,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return new DerivedMemoryMaintainerRunResult(
            epoch,
            artifact,
            input.OldBlock,
            maintenanceResult,
            window.Diagnostics
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
        string roleId,
        MemoryPackBlockPath target,
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
                new MemoryPackBlock(string.Empty),
                null,
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
            || !string.Equals(
                inputSet.LineageKey,
                epoch.LineageKey,
                StringComparison.Ordinal
            )
            || !string.Equals(
                inputSet.CoherenceGroup,
                epoch.CoherenceGroup,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Epoch '{epoch.EpochId}' input set does not match its exact start boundary and planner key."
            );
        }

        DerivedArtifactSetMember? roleMember =
            inputSet.Members.SingleOrDefault(member =>
                string.Equals(
                    member.RoleId,
                    roleId,
                    StringComparison.Ordinal
                ));
        if (roleMember is not null && roleMember.Target != target) {
            throw new InvalidDataException(
                $"Input role '{roleId}' target does not match the maintainer."
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
            restored.TryGetBlock(target, out MemoryPackBlock? oldBlock)
                ? oldBlock
                : new MemoryPackBlock(string.Empty),
            roleMember?.ArtifactId,
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
        MemoryPackBlock OldBlock,
        string? PreviousRoleArtifact,
        IReadOnlyList<DerivedMemoryArtifactInputMember> InputMembers
    );
}
