using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace Atelia.SessionJournal.DerivedMemory.Tests;

public sealed class DerivedMemoryBranchScopeTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void RawOrchestrationMutationsAreNotPublic() {
        const BindingFlags publicInstance =
            BindingFlags.Public | BindingFlags.Instance;
        Type store = typeof(DerivedMemoryOrchestrationStore);

        Assert.Null(store.GetMethod(
            "GetOrCreateAsync",
            publicInstance
        ));
        Assert.Null(store.GetMethod("SettleAsync", publicInstance));
        Assert.Null(store.GetMethod(
            "GetOrCreateFinalizationAsync",
            publicInstance
        ));
        Assert.NotNull(typeof(DerivedArtifactSetStore).GetMethod(
            nameof(DerivedArtifactSetStore.FinalizeAndPublishAsync),
            publicInstance
        ));
    }

    [Fact]
    public async Task BranchScopedPlannerAndSetIndexesAreIndependentAndGloballyValid() {
        string path = NewPath();
        EventAddress forkPoint;
        RefId mainRef;
        using (var created = CreateSession(path)) {
            AppendTurns(created, "shared");
            mainRef = created.BranchRefId;
            forkPoint = created.InspectExecutionBoundary().Head!.Value;
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            _ = journal.ForkBranch(
                "branch-b",
                mainRef,
                forkPoint
            ).Unwrap();
        }

        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactEpochPlan mainEpoch;
        DerivedArtifactSet mainSet;
        DerivedMemoryBranchScope mainScope;
        using (var main = SessionJournalEngine.Open(path, "main")) {
            mainScope = repository.Bind(main);
            mainEpoch = await ConfigurePlanAsync(
                repository,
                main,
                mainScope
            );
            mainSet = await PublishOneRoleSetAsync(
                repository,
                main,
                mainEpoch,
                "main"
            );
        }

        DerivedArtifactEpochPlan branchEpoch;
        DerivedArtifactSet branchSet;
        DerivedMemoryBranchScope branchScope;
        using (var branch = SessionJournalEngine.Open(path, "branch-b")) {
            branchScope = repository.Bind(branch);
            branchEpoch = await ConfigurePlanAsync(
                repository,
                branch,
                branchScope
            );
            branchSet = await PublishOneRoleSetAsync(
                repository,
                branch,
                branchEpoch,
                "branch"
            );
        }

        Assert.NotEqual(mainScope.BranchRefId, branchScope.BranchRefId);
        Assert.NotEqual(mainEpoch.EpochId, branchEpoch.EpochId);
        Assert.NotEqual(mainSet.SetId, branchSet.SetId);
        Assert.Equal(
            mainSet.SetId,
            (await repository.ArtifactSets.TryReadLatestAsync(
                Policy,
                mainScope
            ))!.SetId
        );
        Assert.Equal(
            branchSet.SetId,
            (await repository.ArtifactSets.TryReadLatestAsync(
                Policy,
                branchScope
            ))!.SetId
        );

        var mainSource = new DerivedArtifactSetContextCandidateSource(
            repository,
            Policy,
            mainScope
        );
        var branchSource = new DerivedArtifactSetContextCandidateSource(
            repository,
            Policy,
            branchScope
        );
        Assert.Equal(
            mainSet.SetId,
            Assert.Single((await mainSource.DiscoverAsync(
                SelectionRequest(mainSet.CommonAnchor),
                CancellationToken.None
            )).Candidates).Handle
        );
        Assert.Equal(
            branchSet.SetId,
            Assert.Single((await branchSource.DiscoverAsync(
                SelectionRequest(branchSet.CommonAnchor),
                CancellationToken.None
            )).Candidates).Handle
        );

        DerivedMemoryValidationReport report =
            await repository.ValidateAllActiveBranchesAsync();
        Assert.Equal(2, report.ArtifactEpochCount);
        Assert.Equal(2, report.ArtifactSetCount);
        Assert.Equal(2, report.LatestPointerCount);

        string branchPointerPath = Directory.EnumerateFiles(
                repository.ArtifactSets.LatestPointersDirectory
            )
            .Single(path => string.Equals(
                JsonNode.Parse(File.ReadAllText(path))!["branchRefId"]!
                    .GetValue<string>(),
                branchScope.BranchRefId.ToHexString(),
                StringComparison.Ordinal
            ));
        File.Delete(branchPointerPath);
        using (var main = SessionJournalEngine.OpenReadOnly(
                   path,
                   "main"
               )) {
            DerivedMemoryValidationReport mainOnly =
                await repository.ValidateBranchAsync(main);
            Assert.Equal(1, mainOnly.ArtifactSetCount);
        }
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await repository.ValidateAllActiveBranchesAsync()
        );
    }

    [Fact]
    public async Task ForkAndRecreatedBranchNameDoNotInheritDerivedState() {
        string path = NewPath();
        EventAddress mainHead;
        RefId mainRef;
        using (var created = CreateSession(path)) {
            AppendTurns(created, "main");
            mainRef = created.BranchRefId;
            mainHead = created.InspectExecutionBoundary().Head!.Value;
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            _ = journal.ForkBranch(
                "feature",
                mainRef,
                mainHead
            ).Unwrap();
        }

        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        using (var feature = SessionJournalEngine.Open(path, "feature")) {
            DerivedMemoryBranchScope oldScope = repository.Bind(feature);
            _ = await repository.EpochPlanner.ConfigureAsync(
                oldScope,
                Definition,
                null
            );
        }

        RefId newFeatureRef;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId oldFeatureRef = journal.OpenBranch("feature").Unwrap();
            EventAddress featureHead = journal.GetHead(oldFeatureRef)!.Value;
            Assert.True(
                journal.ArchiveRef(oldFeatureRef, featureHead).Unwrap()
            );
            newFeatureRef = journal.CreateBranch(
                "feature",
                mainHead
            ).Unwrap();
            Assert.NotEqual(oldFeatureRef, newFeatureRef);
        }

        using (var recreated = SessionJournalEngine.Open(path, "feature")) {
            DerivedMemoryBranchScope recreatedScope =
                repository.Bind(recreated);
            Assert.Equal(newFeatureRef, recreatedScope.BranchRefId);
            Assert.Null(
                await repository.EpochPlanner.TryReadCurrentConfigAsync(
                    recreatedScope,
                    Policy.CoherenceGroup
                )
            );
            Assert.Null(
                await repository.ArtifactSets.TryReadLatestAsync(
                    Policy,
                    recreatedScope
                )
            );
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await repository.ValidateAllActiveBranchesAsync()
        );
    }

    [Fact]
    public async Task ForeignScopeAndWrongBranchEngineFailBeforeMutation() {
        string path = NewPath();
        EventAddress forkPoint;
        RefId mainRef;
        using (var created = CreateSession(path)) {
            AppendTurns(created, "main");
            mainRef = created.BranchRefId;
            forkPoint = created.InspectExecutionBoundary().Head!.Value;
        }
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            _ = journal.ForkBranch(
                "branch-b",
                mainRef,
                forkPoint
            ).Unwrap();
        }

        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactEpochPlan mainEpoch;
        using (var main = SessionJournalEngine.Open(path, "main")) {
            mainEpoch = await ConfigurePlanAsync(
                repository,
                main,
                repository.Bind(main)
            );
        }
        IReadOnlyDictionary<string, string> before =
            SnapshotFiles(repository.MemoryRoot);

        using var branch = SessionJournalEngine.Open(path, "branch-b");
        SessionJournalReadDiagnostics readsBefore =
            branch.CaptureReadDiagnostics();
        var runner = new DerivedMemoryMaintainerRunner(repository);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await runner.PrepareAsync(
                branch,
                mainEpoch.EpochId
            )
        );
        Assert.Equal(readsBefore, branch.CaptureReadDiagnostics());
        Assert.Equal(before, SnapshotFiles(repository.MemoryRoot));

        string otherPath = NewPath();
        using var other = CreateSession(otherPath);
        DerivedMemoryRepository otherRepository =
            DerivedMemoryRepository.Open(otherPath);
        DerivedMemoryBranchScope foreignScope =
            otherRepository.Bind(other);
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await repository.EpochPlanner.ConfigureAsync(
                foreignScope,
                Definition,
                null
            )
        );
        Assert.Equal(before, SnapshotFiles(repository.MemoryRoot));
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

    private static readonly DerivedArtifactPlannerConfigDefinition Definition =
        new(
            "memory-pack",
            "topology-v1",
            1,
            1,
            1,
            10_000
        );

    private static readonly MemoryPackBlockPath Target = new(
        MemoryPackCarrier.Observation,
        "memory.test"
    );

    private static readonly DerivedArtifactSetPolicy Policy = new(
        "branch-policy",
        "branch-policy-v1",
        "memory-pack",
        [new("memory", Target)]
    );

    private static SessionContextSelectionRequest SelectionRequest(
        EventAddress completionBoundary
    ) => new(
        completionBoundary,
        SessionContextSelectionMode.Latest,
        "memory-pack"
    );

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-memory-branch-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static SessionJournalEngine CreateSession(string path) =>
        SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );

    private static void AppendTurns(
        SessionJournalEngine engine,
        string prefix
    ) {
        for (int index = 0; index < 4; index++) {
            engine.AppendObservation($"{prefix} observation {index}");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text(
                        $"{prefix} answer {index}"
                    )
                ]),
                new CompletionDescriptor(
                    "import",
                    "v1",
                    "model-a"
                )
            );
        }
    }

    private static async ValueTask<DerivedArtifactEpochPlan>
        ConfigurePlanAsync(
        DerivedMemoryRepository repository,
        SessionJournalEngine engine,
        DerivedMemoryBranchScope scope
    ) {
        _ = await repository.EpochPlanner.ConfigureAsync(
            scope,
            Definition,
            null
        );
        return (await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        )).Epoch!;
    }

    private static async ValueTask<DerivedArtifactSet>
        PublishOneRoleSetAsync(
        DerivedMemoryRepository repository,
        SessionJournalEngine engine,
        DerivedArtifactEpochPlan epoch,
        string text
    ) {
        const string fingerprint =
            "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        SessionContextAnchorSetupReferences setups =
            engine.ResolveContextAnchorSetupReferences(
                epoch.SourceEndInclusive
            );
        var role = new DerivedMemoryRoleProvisioning(
            "memory",
            "profile",
            Target,
            true,
            "tests",
            fingerprint,
            fingerprint,
            fingerprint,
            DerivedMemoryRoleExecutionModes.Produce,
            $"candidate-{text}",
            $"attempt-{text}"
        );
        var draft = new MemoryPackDraft(new MemoryPack());
        draft.UpsertBlock(Target, text);
        DerivedMemoryArtifact artifact =
            await repository.Artifacts.WriteCandidateAsync(
                new(
                    epoch.EpochId,
                    DerivedMemoryMaintainerRunner
                        .GetEpochPlanFingerprint(epoch),
                    role.RoleId,
                    role.ProfileId,
                    role.Producer,
                    role.ProducerFingerprint,
                    role.PromptFingerprint,
                    role.ModelFingerprint,
                    role.CandidateId,
                    role.AttemptId,
                    epoch.PlannedAtRawHead,
                    epoch.SourceStartExclusive,
                    epoch.SourceEndInclusive,
                    epoch.SourceEndInclusive,
                    epoch.RawStartSetups,
                    setups,
                    epoch.InputSetId,
                    null,
                    [],
                    Target,
                    draft.Build()
                )
            );
        DerivedMemoryOrchestrationTransaction transaction =
            await DerivedArtifactSetTestFactory
                .CreateSettledTransactionAsync(
                    repository,
                    epoch,
                    Policy,
                    [artifact]
                );
        return await DerivedArtifactSetTestFactory.FinalizeAndPublishAsync(
            repository,
            engine,
            Policy,
            transaction,
            setups,
            [new("memory", artifact.ArtifactId)]
        );
    }

    private static IReadOnlyDictionary<string, string> SnapshotFiles(
        string root
    ) => !Directory.Exists(root)
        ? new Dictionary<string, string>(StringComparer.Ordinal)
        : Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories
            )
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllText,
                StringComparer.Ordinal
            );
}
