using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedMemory.Tests;

public sealed class DerivedMemoryMaintainerRunnerTests : IDisposable {
    private readonly List<string> _paths = [];
    private static readonly string Fingerprint =
        "sha256:" + new string('a', 64);

    [Fact]
    public async Task GenesisRun_ReadsExactEpochAndAlternativesAreAppendOnly() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateSession(path);
        EventAddress runtimeSetup = engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration(
                "model-B",
                "surface-B",
                SessionJournalDefaults.Schema
            )
        );
        EventAddress promptSetup =
            engine.AppendSystemPromptSetup("system-B");
        AppendTurns(engine, 5, "turn");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactEpochPlan epoch = await PlanGenesisAsync(
            repository,
            engine
        );
        Directory.CreateDirectory(
            repository.Artifacts.ArtifactsDirectory
        );
        await File.WriteAllTextAsync(
            Path.Combine(
                repository.Artifacts.ArtifactsDirectory,
                "unrelated-malformed.json"
            ),
            "{broken"
        );
        var maintainer = new CapturingMaintainer(
            "test-profile",
            new MemoryPackBlockPath(
                MemoryPackCarrier.Action,
                "memory.test"
            ),
            "candidate text"
        );
        var runner = new DerivedMemoryMaintainerRunner(repository);

        DerivedMemoryMaintainerRunResult first =
            await runner.RunAsync(
                engine,
                Request(epoch, "candidate-a"),
                maintainer
            );
        DerivedMemoryMaintainerRunResult alternative =
            await runner.RunAsync(
                engine,
                Request(epoch, "candidate-b"),
                maintainer
            );

        Assert.Equal(string.Empty, first.OldBlock.Text);
        Assert.Equal(
            epoch.SourceStartExclusive,
            first.Artifact.SourceStartExclusive
        );
        Assert.Equal(
            epoch.SourceEndInclusive,
            first.Artifact.SourceEndInclusive
        );
        Assert.Equal(
            epoch.SourceEndInclusive,
            first.Artifact.AnchorRawEvent
        );
        Assert.Equal(
            runtimeSetup,
            first.Artifact.AnchorSetups.RuntimeConfig.Address
        );
        Assert.Equal(
            promptSetup,
            first.Artifact.AnchorSetups.SystemPrompt.Address
        );
        Assert.Equal(epoch.RawStartSetups, first.Artifact.RawStartSetups);
        Assert.NotEqual(
            first.Artifact.RawStartSetups,
            first.Artifact.AnchorSetups
        );
        Assert.NotEqual(
            first.Artifact.ArtifactId,
            alternative.Artifact.ArtifactId
        );
        Assert.Null(first.Artifact.InputSetId);
        Assert.Null(first.Artifact.PreviousRoleArtifact);
        Assert.False(Directory.Exists(
            repository.ArtifactSets.LatestPointersDirectory
        ));
    }

    [Fact]
    public async Task NewRoleUsesOtherInputMembersButStartsWithEmptyOldBlock() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactEpochPlan first = await PlanGenesisAsync(
            repository,
            engine
        );
        var oldTarget = new MemoryPackBlockPath(
            MemoryPackCarrier.Observation,
            "memory.old"
        );
        DerivedMemoryArtifact old =
            (await new DerivedMemoryMaintainerRunner(repository)
                .RunAsync(
                    engine,
                    Request(
                        first,
                        "candidate-old",
                        roleId: "old-role"
                    ) with {
                        ProfileId = "old-profile"
                    },
                    new CapturingMaintainer(
                        "old-profile",
                        oldTarget,
                        "old role memory"
                    )
                )).Artifact;
        var policy = new DerivedArtifactSetPolicy(
            "test-policy",
            "test-policy-v1",
            first.CoherenceGroup,
            [
                new DerivedArtifactSetRoleRequirement(
                    "old-role",
                    oldTarget
                )
            ]
        );
        DerivedMemoryOrchestrationTransaction transaction =
            await DerivedArtifactSetTestFactory
                .CreateSettledTransactionAsync(
                    repository,
                    first,
                    policy,
                    [old]
                );
        DerivedArtifactSet inputSet =
            await DerivedArtifactSetTestFactory.FinalizeAndPublishAsync(
                repository,
                engine,
                policy,
                transaction,
                old.AnchorSetups,
                [
                    new DerivedArtifactSetMemberSelection(
                        "old-role",
                        old.ArtifactId
                    )
                ]
            );
        AppendTurns(engine, 7, "second");
        DerivedArtifactEpochPlan second =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new(
                    first.CoherenceGroup,
                    first.EpochId,
                    inputSet.SetId
                )
            )).Epoch!;
        var newMaintainer = new CapturingMaintainer(
            "test-profile",
            new MemoryPackBlockPath(
                MemoryPackCarrier.Action,
                "memory.new"
            ),
            "new role memory"
        );

        DerivedMemoryMaintainerRunResult result =
            await new DerivedMemoryMaintainerRunner(repository)
                .RunAsync(
                    engine,
                    Request(second, "candidate-new"),
                    newMaintainer
                );

        Assert.Equal(string.Empty, result.OldBlock.Text);
        Assert.Null(result.Artifact.PreviousRoleArtifact);
        Assert.Equal(inputSet.SetId, result.Artifact.InputSetId);
        Assert.Single(result.Artifact.InputMembers);
        Assert.Contains(
            "old role memory",
            newMaintainer.CapturedRequest!.RecentHistory
                .PriorContext.ObservationMessage,
            StringComparison.Ordinal
        );
        DerivedMemoryValidationReport validation =
            await repository.ValidateBranchAgainstOpenEngineAsync(engine);
        Assert.Equal(2, validation.ArtifactCount);
    }

    [Theory]
    [InlineData("raw-start")]
    [InlineData("anchor")]
    public async Task GlobalValidationAcceptsEpochOrphanAndRejectsSelfConsistentDrift(
        string drift
    ) {
        string path = NewPath();
        using SessionJournalEngine engine = CreateSession(path);
        AppendTurns(engine, 5, "turn");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactEpochPlan epoch = await PlanGenesisAsync(
            repository,
            engine
        );
        var target = new MemoryPackBlockPath(
            MemoryPackCarrier.Action,
            "memory.test"
        );
        var maintainer = new CapturingMaintainer(
            "test-profile",
            target,
            "candidate text"
        );
        DerivedMemoryMaintainerRunResult result =
            await new DerivedMemoryMaintainerRunner(repository)
                .RunAsync(
                    engine,
                    Request(epoch, "candidate-valid"),
                    maintainer
                );

        DerivedMemoryValidationReport valid =
            await repository.ValidateBranchAgainstOpenEngineAsync(engine);
        Assert.Equal(1, valid.ArtifactCount);
        Assert.Equal(0, valid.ArtifactSetCount);

        SessionContextAnchorSetupReferences driftedRawStart =
            result.Artifact.RawStartSetups with {
                RuntimeConfig =
                    result.Artifact.RawStartSetups.RuntimeConfig with {
                        PayloadSha256 = new string('f', 64)
                    }
            };
        SessionContextAnchorSetupReferences driftedAnchor =
            result.Artifact.AnchorSetups with {
                SystemPrompt =
                    result.Artifact.AnchorSetups.SystemPrompt with {
                        PayloadSha256 = new string('e', 64)
                    }
            };
        _ = await repository.Artifacts.WriteCandidateAsync(
            new DerivedMemoryArtifactWriteRequest(
                result.Artifact.EpochId,
                result.Artifact.EpochPlanFingerprint,
                result.Artifact.RoleId,
                result.Artifact.ProfileId,
                result.Artifact.Producer,
                result.Artifact.ProducerFingerprint,
                result.Artifact.PromptFingerprint,
                result.Artifact.ModelFingerprint,
                "candidate-drift",
                result.Artifact.AttemptId,
                result.Artifact.SourceRawHead,
                result.Artifact.SourceStartExclusive,
                result.Artifact.SourceEndInclusive,
                result.Artifact.AnchorRawEvent,
                drift == "raw-start"
                    ? driftedRawStart
                    : result.Artifact.RawStartSetups,
                drift == "anchor"
                    ? driftedAnchor
                    : result.Artifact.AnchorSetups,
                result.Artifact.InputSetId,
                result.Artifact.PreviousRoleArtifact,
                result.Artifact.InputMembers,
                result.Artifact.Target,
                result.Artifact.MemoryPack
            )
        );

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await repository.ValidateBranchAgainstOpenEngineAsync(engine)
            );
        Assert.Contains(
            drift == "raw-start"
                ? "durable epoch identity"
                : "anchor setup references",
            error.Message
        );
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup.
            }
        }
    }

    private static DerivedMemoryMaintainerRunRequest Request(
        DerivedArtifactEpochPlan epoch,
        string candidateId,
        string roleId = "new-role"
    ) => new(
        epoch.EpochId,
        roleId,
        "test-profile",
        "tests",
        Fingerprint,
        Fingerprint,
        Fingerprint,
        candidateId,
        "attempt-1"
    );

    private static async ValueTask<DerivedArtifactEpochPlan>
        PlanGenesisAsync(
        DerivedMemoryRepository repository,
        SessionJournalEngine engine
    ) {
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            new DerivedArtifactPlannerConfigDefinition(
                "memory-pack",
                "topology-v1",
                MinimumRecentTokens: 10,
                EpochTriggerTokens: 10,
                SchedulingHeadroomTokens: 10,
                HardLimitTokens: 1_000
            ),
            null
        );
        return (await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        )).Epoch!;
    }

    private static SessionJournalEngine CreateSession(string path) =>
        SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );

    private static void AppendTurns(
        SessionJournalEngine engine,
        int count,
        string prefix
    ) {
        for (int index = 0; index < count; index++) {
            _ = engine.AppendObservation(
                $"{prefix}-observation-{index}-with-token-cost"
            );
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text(
                        $"{prefix}-answer-{index}-with-token-cost"
                    )
                ]),
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-A"
                )
            );
        }
    }

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-maintainer-runner-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class CapturingMaintainer(
        string id,
        MemoryPackBlockPath target,
        string output
    ) : IMemoryBlockMaintainer {
        public string Id { get; } = id;
        public MemoryPackBlockPath Target { get; } = target;
        public MemoryBlockMaintenanceRequest? CapturedRequest { get; private set; }

        public ValueTask<MemoryBlockMaintenanceResult> MaintainAsync(
            MemoryBlockMaintenanceRequest request,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedRequest = request;
            return ValueTask.FromResult(
                new MemoryBlockMaintenanceResult(
                    Id,
                    Target,
                    new MemoryPackBlock(output)
                )
            );
        }
    }
}
