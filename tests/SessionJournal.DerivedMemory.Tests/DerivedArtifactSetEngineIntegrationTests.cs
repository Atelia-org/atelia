using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedMemory.Tests;

public sealed class DerivedArtifactSetEngineIntegrationTests : IDisposable {
    private readonly List<string> _tempDirectories = [];

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for test-owned repositories.
            }
        }
    }

    [Fact]
    public async Task ConcreteProvider_DrivesObservationCompletionWithoutRawMutation() {
        PublishedFixture fixture = await CreatePublishedFixtureAsync();
        var client = new CapturingClient("online answer");
        EventAddress headBefore = ReadRawSnapshot(fixture.Path).Head;
        TurnResult outcome;
        using (var engine = SessionJournalEngine.Open(
            fixture.Path,
            CreateRuntime(
                client,
                fixture.Provider,
                fixture.CoherenceGroup
            )
        )) {
            outcome = await engine.SendAsync(
                "new observation",
                CancellationToken.None
            );
        }

        Assert.Equal("online answer", outcome.Message.GetFlattenedText());
        CompletionRequest request = Assert.Single(client.Requests);
        Assert.Contains(
            request.Context,
            message => message is ObservationMessage observation
                && observation.Content?.Contains(
                    "derived world",
                    StringComparison.Ordinal
                ) == true
        );
        Assert.Contains(
            request.Context,
            message => message is ActionMessage action
                && action.GetFlattenedText().Contains(
                    "derived self",
                    StringComparison.Ordinal
                )
        );
        RawSnapshot after = ReadRawSnapshot(fixture.Path);
        Assert.NotEqual(headBefore, after.Head);
        Assert.Equal(0, after.UnknownEventKindCount);
    }

    [Fact]
    public async Task PreparedRequest_ReopensExactlyAfterEntireDerivedDirectoryIsDeleted() {
        PublishedFixture fixture = await CreatePublishedFixtureAsync();
        var neverCalled = new CapturingClient("must not be called");
        using (var source = SessionJournalEngine.OpenForTest(
            fixture.Path,
            CreateRuntime(
                neverCalled,
                fixture.Provider,
                fixture.CoherenceGroup
            ),
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterRequestPreparedCommitted
            )
        )) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => source.SendAsync(
                    "durable observation",
                    CancellationToken.None
                )
            );
        }
        Assert.Empty(neverCalled.Requests);

        EventAddress prepared = ReadRawSnapshot(fixture.Path).Head;
        byte[] expectedCanonical;
        using (var journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(fixture.Path)) {
            expectedCanonical = SessionPreparedRequestReconstructor
                .Reconstruct(journal, prepared)
                .CanonicalBytes;
        }

        Directory.Delete(
            Path.Combine(fixture.Path, "derived"),
            recursive: true
        );
        var recoveryClient = new CapturingClient("recovered answer");
        ResumeOutcome outcome;
        using (var reopened = SessionJournalEngine.Open(
            fixture.Path,
            CreateRuntime(
                recoveryClient,
                provider: null,
                coherenceGroup: fixture.CoherenceGroup
            ) with {
                UncertainCompletionRecoveryPolicy =
                    SessionUncertainCompletionRecoveryPolicy
                        .RestartWithNewAttempt
            }
        )) {
            outcome = await reopened.ResumeAsync(CancellationToken.None);
        }

        Assert.Equal("recovered answer", outcome.Message?.GetFlattenedText());
        CompletionRequest recoveredRequest =
            Assert.Single(recoveryClient.Requests);
        Assert.Equal(
            expectedCanonical,
            SessionRequestCanonicalizer.Canonicalize(recoveredRequest)
        );
        Assert.False(Directory.Exists(
            Path.Combine(fixture.Path, "derived")
        ));
        Assert.Equal(
            0,
            ReadRawSnapshot(fixture.Path).UnknownEventKindCount
        );
    }

    [Fact]
    public async Task RewindBehindPublishedSetFailsOnlineValidationAndRebuildWithoutDerivedMutation() {
        PublishedFixture fixture = await CreatePublishedFixtureAsync();
        EventAddress currentHead = ReadRawSnapshot(fixture.Path).Head;
        SessionContextCandidateSelection selected =
            await fixture.Provider.SelectAsync(
                new(currentHead, 0),
                CancellationToken.None
            );
        Assert.NotNull(selected.Candidate);
        SessionContextCandidateDescriptor descriptor =
            selected.Candidate;
        using (var journal =
               EventJournal.EventJournal.OpenExisting(fixture.Path)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            EventAddress rewindTarget = journal
                .ReadEventHeaderPreview(descriptor.SetAdmissionAnchor)
                .Unwrap()
                .Parent!.Value;
            Assert.True(
                journal.MoveRef(
                    main,
                    currentHead,
                    rewindTarget
                ).Unwrap()
            );
        }
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            fixture.Repository.ArtifactSets.LatestPointersDirectory
        )));
        IReadOnlyDictionary<string, string> derivedBefore =
            SnapshotDerivedFiles(fixture.Repository.MemoryRoot);
        SessionContextCandidateSelection rediscovered =
            await fixture.Provider.SelectAsync(
                new(currentHead, 0),
                CancellationToken.None
            );
        Assert.NotNull(rediscovered.Candidate);
        Assert.Equal(
            derivedBefore,
            SnapshotDerivedFiles(fixture.Repository.MemoryRoot)
        );
        var client = new CapturingClient("must not be called");
        using var reopened = SessionJournalEngine.Open(
            fixture.Path,
            CreateRuntime(
                client,
                fixture.Provider,
                fixture.CoherenceGroup
            )
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await reopened.SendAsync(
                "after rewind",
                CancellationToken.None
            )
        );
        Assert.Empty(client.Requests);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Repository.ValidateBranchAgainstOpenEngineAsync(reopened)
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Repository.ArtifactSets
                .RebuildLatestPointerAsync(
                    reopened,
                    fixture.Policy
                )
        );
        Assert.Equal(
            derivedBefore,
            SnapshotDerivedFiles(fixture.Repository.MemoryRoot)
        );
    }

    private async ValueTask<PublishedFixture> CreatePublishedFixtureAsync() {
        string path = NewPath();
        DerivedArtifactEpochPlan epoch;
        DerivedMemoryRepository repository;
        DerivedMemoryBranchScope scope;
        SessionContextAnchorSetupReferences anchorSetups;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            engine.AppendObservation("old observation");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("old answer")
                ]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            repository = DerivedMemoryRepository.Open(path);
            scope = repository.Bind(engine);
            _ = await repository.EpochPlanner.ConfigureAsync(
                scope,
                new(
                    "integration-group",
                    "integration-topology-v1",
                    1,
                    1,
                    1,
                    1_000
                ),
                null
            );
            epoch = (await repository.EpochPlanner.PlanAsync(
                engine,
                new("integration-group", null, null)
            )).Epoch!;
            anchorSetups = engine.ResolveContextAnchorSetupReferences(
                epoch.SourceEndInclusive
            );
        }
        RawSnapshot rawBefore = ReadRawSnapshot(path);
        var worldTarget = new ContextHeaderBlockPath(
            ContextHeaderCarrier.Observation,
            "memory.world"
        );
        var selfTarget = new ContextHeaderBlockPath(
            ContextHeaderCarrier.Action,
            "memory.self"
        );
        DerivedMemoryArtifact world = await WriteArtifactAsync(
            repository,
            "world-profile",
            worldTarget,
            "derived world",
            epoch,
            anchorSetups
        );
        DerivedMemoryArtifact self = await WriteArtifactAsync(
            repository,
            "self-profile",
            selfTarget,
            "derived self",
            epoch,
            anchorSetups
        );
        var policy = new DerivedArtifactSetPolicy(
            "integration-policy",
            "integration-policy-v1",
            "integration-group",
            [
                new DerivedArtifactSetRoleRequirement(
                    "world",
                    worldTarget
                ),
                new DerivedArtifactSetRoleRequirement(
                    "self",
                    selfTarget
                )
            ]
        );
        DerivedMemoryOrchestrationTransaction transaction =
            await DerivedArtifactSetTestFactory
                .CreateSettledTransactionAsync(
                    repository,
                    epoch,
                    policy,
                    [world, self]
                );
        DerivedArtifactSet published;
        using (var authorityEngine = SessionJournalEngine.Open(path)) {
            var publication = new DerivedArtifactSetPublicationRequest(
                policy,
                transaction,
                anchorSetups,
                [
                    new DerivedArtifactSetMemberSelection(
                        "world",
                        world.ArtifactId
                    ),
                    new DerivedArtifactSetMemberSelection(
                        "self",
                        self.ArtifactId
                    )
                ],
                ExpectedPreviousSetId: null
            );
            DerivedArtifactSet prepared =
                await repository.ArtifactSets.PreparePublicationAsync(
                    authorityEngine,
                    publication
                );
            _ = await repository.Orchestrations
                .GetOrCreateFinalizationAsync(
                    transaction,
                    anchorSetups,
                    await repository.Orchestrations
                        .ReadSettlementsAsync(transaction),
                    prepared.SetId
                );
            published = await repository.ArtifactSets.PublishAsync(
                authorityEngine,
                publication
            );
        }
        RawSnapshot rawAfter = ReadRawSnapshot(path);
        Assert.Equal(rawBefore, rawAfter);

        return new PublishedFixture(
            path,
            repository,
            policy,
            new DerivedArtifactSetContextCandidateSource(
                repository,
                policy,
                scope
            ),
            policy.CoherenceGroup,
            published
        );
    }

    private static async ValueTask<DerivedMemoryArtifact>
        WriteArtifactAsync(
        DerivedMemoryRepository repository,
        string profileId,
        ContextHeaderBlockPath target,
        string text,
        DerivedArtifactEpochPlan epoch,
        SessionContextAnchorSetupReferences setups
    ) {
        string roleId =
            profileId.EndsWith("-profile", StringComparison.Ordinal)
                ? profileId[..^"-profile".Length]
                : profileId;
        var draft = new ContextHeaderPackDraft(new ContextHeaderPack());
        draft.UpsertBlock(target, text);
        const string fingerprint =
            "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        return await repository.Artifacts.WriteCandidateAsync(
            new DerivedMemoryArtifactWriteRequest(
                epoch.EpochId,
                DerivedMemoryMaintainerRunner.GetEpochPlanFingerprint(
                    epoch
                ),
                roleId,
                profileId,
                "tests",
                fingerprint,
                fingerprint,
                fingerprint,
                "candidate-1",
                "attempt-1",
                epoch.PlannedAtRawHead,
                epoch.SourceStartExclusive,
                epoch.SourceEndInclusive,
                epoch.SourceEndInclusive,
                epoch.RawStartSetups,
                setups,
                null,
                null,
                [],
                target,
                draft.Build()
            )
        );
    }

    private static SessionRuntime CreateRuntime(
        CapturingClient client,
        ICoherentContextCandidateSource? provider,
        string coherenceGroup
    ) => new(
        CompletionClient: client,
        CompletionTarget: new SessionCompletionTargetIdentity(
            "integration",
            "test",
            "integration-connection-v1",
            "integration-adapter-v1"
        ),
        MaxTokens: 256,
        ContextCandidateSource: provider
    );

    private static RawSnapshot ReadRawSnapshot(string path) {
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(path);
        RefId main = journal.OpenBranch(
            SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        IReadOnlyList<EventAddress> chain =
            journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();
        return new RawSnapshot(
            head,
            chain.Count,
            chain.Count(address => !Enum.IsDefined(
                typeof(SessionEventKind),
                journal.ReadEventHeaderPreview(address)
                    .Unwrap()
                    .OpaqueEventKind
            ))
        );
    }

    private static IReadOnlyDictionary<string, string>
        SnapshotDerivedFiles(string root) =>
        Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories
            )
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllText,
                StringComparer.Ordinal
            );

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-engine-integration-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed class CapturingClient(string response)
        : ICompletionClient {
        public string Name => "derived-integration";

        public string ApiSpecId => "derived-integration-v1";

        public List<CompletionRequest> Requests { get; } = [];

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(response)]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }

    private sealed record PublishedFixture(
        string Path,
        DerivedMemoryRepository Repository,
        DerivedArtifactSetPolicy Policy,
        DerivedArtifactSetContextCandidateSource Provider,
        string CoherenceGroup,
        DerivedArtifactSet Set
    );

    private sealed record RawSnapshot(
        EventAddress Head,
        int EventCount,
        int UnknownEventKindCount
    );
}
