using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedMemory.Tests;

public sealed class DerivedArtifactSetStoreTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly List<SessionJournalEngine> _engines = [];

    [Fact]
    public async Task PublishedV3SetCarriesExactTransactionAndEpoch() {
        Fixture fixture = await CreateFixtureAsync();

        DerivedArtifactSet set =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Engine,
                fixture.Publication()
            );

        Assert.Equal(
            DerivedArtifactSetStore.SetSchema,
            "atelia.session-journal.derived-artifact-set.v3"
        );
        Assert.Equal(
            fixture.Transaction.TransactionId,
            set.TransactionId
        );
        Assert.Equal(fixture.Transaction.JobFingerprint, set.JobFingerprint);
        Assert.Equal(fixture.Epoch.EpochId, set.EpochId);
        Assert.Equal(
            fixture.Transaction.EpochPlanFingerprint,
            set.EpochPlanFingerprint
        );
        Assert.Equal(fixture.Epoch.TopologyVersion, set.TopologyVersion);
        Assert.Equal(fixture.Epoch.InputSetId, set.PreviousSetId);
        Assert.Equal(fixture.Transaction.Roles, set.RoleProvisioning);
        Assert.All(set.Members, member =>
            Assert.Equal(
                DerivedMemoryArtifactOutcomes.Changed,
                member.Outcome
            ));
        DerivedArtifactSet reopened =
            await fixture.Repository.ArtifactSets.TryReadExactAsync(
                set.SetId
            ) ?? throw new Xunit.Sdk.XunitException(
                "Expected exact set."
            );
        Assert.Equal(set.SetId, reopened.SetId);
        Assert.Equal(set.RoleProvisioning, reopened.RoleProvisioning);
        Assert.Equal(set.Members, reopened.Members);
    }

    [Fact]
    public async Task PartialSettlementNeverPublishesOrMovesLatest() {
        Fixture fixture = await CreateFixtureAsync(settleSecond: false);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Engine,
                fixture.Publication()
            )
        );

        Assert.False(Directory.Exists(
            fixture.Repository.ArtifactSets.SetsDirectory
        ));
        Assert.False(Directory.Exists(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
    }

    [Fact]
    public async Task SelectionMustEqualExactDurableSettlement() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedMemoryArtifact alternative = await WriteArtifactAsync(
            fixture.Repository,
            fixture.Epoch,
            fixture.Transaction.Roles[0],
            fixture.AnchorSetups,
            "alternative",
            "alternative text"
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Engine,
                fixture.Publication() with {
                    Members = [
                        new(
                            fixture.Transaction.Roles[0].RoleId,
                            alternative.ArtifactId
                        ),
                        fixture.Selections[1]
                    ]
                }
            )
        );
        Assert.False(Directory.Exists(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
    }

    [Fact]
    public async Task ConcurrentIdenticalPublicationsConverge() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSetPublicationRequest publication =
            fixture.Publication();

        DerivedArtifactSet[] sets = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(async _ =>
                await fixture.Repository.ArtifactSets.PublishAsync(
                    fixture.Engine,
                    publication
                ).AsTask())
        );

        Assert.Single(sets.Select(static set => set.SetId).Distinct());
        Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.SetsDirectory
        ));
        Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
    }

    [Fact]
    public async Task StalePreviousCannotMoveLatest() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet published =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Engine,
                fixture.Publication()
            );

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Engine,
                fixture.Publication() with {
                    ExpectedPreviousSetId = published.SetId
                }
            )
        );

        Assert.Equal(
            published.SetId,
            (await fixture.Repository.ArtifactSets.TryReadLatestAsync(
                fixture.Policy,
                fixture.Epoch.BranchRefId
            ))!.SetId
        );
        Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.SetsDirectory
        ));
    }

    [Fact]
    public async Task RewindBeforePublicationFailsAndKeepsSettlements() {
        Fixture fixture = await CreateFixtureAsync();
        fixture.Engine.Dispose();
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.OpenExisting(fixture.Path)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            journal.MoveRef(
                main,
                fixture.Epoch.PlannedAtRawHead,
                fixture.Epoch.SourceStartExclusive
            ).Unwrap();
        }
        using SessionJournalEngine authority =
            SessionJournalEngine.Open(fixture.Path);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets.PublishAsync(
                authority,
                fixture.Publication()
            )
        );
        Assert.Equal(
            2,
            (await fixture.Repository.Orchestrations
                .ReadSettlementsAsync(fixture.Transaction)).Count
        );
        Assert.False(Directory.Exists(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
    }

    [Fact]
    public async Task MissingLatestPointerRebuildsFromUniqueV3Tip() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet published =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Engine,
                fixture.Publication()
            );
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        )));

        Assert.Null(await fixture.Repository.ArtifactSets.TryReadLatestAsync(
            fixture.Policy,
            fixture.Epoch.BranchRefId
        ));
        DerivedArtifactSet rebuilt =
            await fixture.Repository.ArtifactSets.RebuildLatestPointerAsync(
                fixture.Engine,
                fixture.Policy
            ) ?? throw new Xunit.Sdk.XunitException(
                "Expected rebuilt set."
            );

        Assert.Equal(published.SetId, rebuilt.SetId);
        Assert.Equal(
            published.SetId,
            (await fixture.Repository.ArtifactSets.TryReadLatestAsync(
                fixture.Policy,
                fixture.Epoch.BranchRefId
            ))!.SetId
        );
    }

    [Fact]
    public async Task MalformedLatestPointerFailsFast() {
        Fixture fixture = await CreateFixtureAsync();
        _ = await fixture.Repository.ArtifactSets.PublishAsync(
            fixture.Engine,
            fixture.Publication()
        );
        string pointerPath = Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        ));
        await File.WriteAllTextAsync(pointerPath, "{broken");

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .TryReadLatestAsync(
                    fixture.Policy,
                    fixture.Epoch.BranchRefId
                )
        );
    }

    [Fact]
    public async Task OversizedSetIsRejectedBeforeDeserialization() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet published =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Engine,
                fixture.Publication()
            );
        string setPath = Path.Combine(
            fixture.Repository.ArtifactSets.SetsDirectory,
            $"{published.SetId}.json"
        );
        await File.WriteAllTextAsync(
            setPath,
            new string(
                'x',
                checked((int)
                    DerivedArtifactSetStore.MaxSetFileBytes + 1)
            )
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .TryReadExactAsync(published.SetId)
        );
    }

    [Fact]
    public async Task UnknownSetSchemaIsRejectedWithoutCompatibilityFallback() {
        Fixture fixture = await CreateFixtureAsync();
        DerivedArtifactSet published =
            await fixture.Repository.ArtifactSets.PublishAsync(
                fixture.Engine,
                fixture.Publication()
            );
        string setPath = Path.Combine(
            fixture.Repository.ArtifactSets.SetsDirectory,
            $"{published.SetId}.json"
        );
        string json = await File.ReadAllTextAsync(setPath);
        await File.WriteAllTextAsync(
            setPath,
            json.Replace(
                DerivedArtifactSetStore.SetSchema,
                "atelia.session-journal.derived-artifact-set.v1",
                StringComparison.Ordinal
            )
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .TryReadExactAsync(published.SetId)
        );
    }

    [Fact]
    public async Task SetStoreSymlinkFailsBeforeEnumeration() {
        Fixture fixture = await CreateFixtureAsync();
        string target = Path.Combine(
            Path.GetTempPath(),
            "atelia-artifact-set-v2-tests-symlink-target",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(target);
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(
            fixture.Repository.ArtifactSets.SetsDirectory,
            target
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .ReadInventoryAsync()
        );
    }

    public void Dispose() {
        foreach (SessionJournalEngine engine in _engines) {
            engine.Dispose();
        }
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

    private async ValueTask<Fixture> CreateFixtureAsync(
        bool settleSecond = true
    ) {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-artifact-set-v2-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-a", "system-a", "surface-a")
        );
        _engines.Add(engine);
        for (int index = 0; index < 4; index++) {
            engine.AppendObservation($"observation {index}");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"answer {index}")
                ]),
                new CompletionDescriptor("import", "v1", "model-a")
            );
        }
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            new(
                "memory-pack",
                "topology-v1",
                1,
                1,
                1,
                1_000
            ),
            null
        );
        DerivedArtifactEpochPlan epoch =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new("memory-pack", null, null)
            )).Epoch!;
        SessionContextAnchorSetupReferences setups =
            engine.ResolveContextAnchorSetupReferences(
                epoch.SourceEndInclusive
            );
        var targets = new[] {
            new MemoryPackBlockPath(
                MemoryPackCarrier.Observation,
                "memory.alpha"
            ),
            new MemoryPackBlockPath(
                MemoryPackCarrier.System,
                "memory.zeta"
            )
        };
        var policy = new DerivedArtifactSetPolicy(
            "policy-v2",
            "policy-fingerprint-v2",
            epoch.CoherenceGroup,
            [
                new("alpha", targets[0]),
                new("zeta", targets[1])
            ]
        );
        DerivedMemoryRoleProvisioning[] roles = [
            Provision("alpha", "alpha-profile", targets[0]),
            Provision("zeta", "zeta-profile", targets[1])
        ];
        DerivedMemoryOrchestrationTransaction transaction =
            await repository.Orchestrations.GetOrCreateAsync(
                epoch,
                policy,
                roles
            );
        DerivedMemoryArtifact[] artifacts = [
            await WriteArtifactAsync(
                repository,
                epoch,
                roles[0],
                setups,
                "alpha",
                "alpha text"
            ),
            await WriteArtifactAsync(
                repository,
                epoch,
                roles[1],
                setups,
                "zeta",
                "zeta text"
            )
        ];
        _ = await repository.Orchestrations.SettleAsync(
            transaction,
            Settlement(transaction, artifacts[0])
        );
        if (settleSecond) {
            _ = await repository.Orchestrations.SettleAsync(
                transaction,
                Settlement(transaction, artifacts[1])
            );
        }
        var publication = new DerivedArtifactSetPublicationRequest(
            policy,
            transaction,
            setups,
            [
                new("alpha", artifacts[0].ArtifactId),
                new("zeta", artifacts[1].ArtifactId)
            ],
            epoch.InputSetId
        );
        if (settleSecond) {
            DerivedArtifactSet prepared =
                await repository.ArtifactSets.PreparePublicationAsync(
                    engine,
                    publication
                );
            _ = await repository.Orchestrations
                .GetOrCreateFinalizationAsync(
                    transaction,
                    setups,
                    [
                        Settlement(transaction, artifacts[0]),
                        Settlement(transaction, artifacts[1])
                    ],
                    prepared.SetId
                );
        }
        return new Fixture(
            path,
            engine,
            repository,
            epoch,
            policy,
            transaction,
            setups,
            [
                new("alpha", artifacts[0].ArtifactId),
                new("zeta", artifacts[1].ArtifactId)
            ]
        );
    }

    private static DerivedMemoryRoleProvisioning Provision(
        string roleId,
        string profileId,
        MemoryPackBlockPath target
    ) {
        const string fingerprint =
            "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        return new(
            roleId,
            profileId,
            target,
            true,
            "tests",
            fingerprint,
            fingerprint,
            fingerprint,
            DerivedMemoryRoleExecutionModes.Produce,
            "candidate",
            "attempt"
        );
    }

    private static async ValueTask<DerivedMemoryArtifact>
        WriteArtifactAsync(
        DerivedMemoryRepository repository,
        DerivedArtifactEpochPlan epoch,
        DerivedMemoryRoleProvisioning role,
        SessionContextAnchorSetupReferences setups,
        string candidate,
        string text
    ) {
        var draft = new MemoryPackDraft(new MemoryPack());
        draft.UpsertBlock(role.Target, text);
        return await repository.Artifacts.WriteCandidateAsync(
            new DerivedMemoryArtifactWriteRequest(
                epoch.EpochId,
                DerivedMemoryMaintainerRunner.GetEpochPlanFingerprint(
                    epoch
                ),
                role.RoleId,
                role.ProfileId,
                role.Producer,
                role.ProducerFingerprint,
                role.PromptFingerprint,
                role.ModelFingerprint,
                role.CandidateId == "candidate"
                    ? role.CandidateId
                    : candidate,
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
                role.Target,
                draft.Build()
            )
        );
    }

    private static DerivedMemoryRoleSettlement Settlement(
        DerivedMemoryOrchestrationTransaction transaction,
        DerivedMemoryArtifact artifact
    ) => new(
        transaction.TransactionId,
        artifact.RoleId,
        artifact.ArtifactId,
        artifact.Outcome
    );

    private sealed record Fixture(
        string Path,
        SessionJournalEngine Engine,
        DerivedMemoryRepository Repository,
        DerivedArtifactEpochPlan Epoch,
        DerivedArtifactSetPolicy Policy,
        DerivedMemoryOrchestrationTransaction Transaction,
        SessionContextAnchorSetupReferences AnchorSetups,
        IReadOnlyList<DerivedArtifactSetMemberSelection> Selections
    ) {
        public DerivedArtifactSetPublicationRequest Publication() => new(
            Policy,
            Transaction,
            AnchorSetups,
            Selections,
            Epoch.InputSetId
        );
    }
}
