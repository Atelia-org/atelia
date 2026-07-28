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

    private async ValueTask<PublishedFixture> CreatePublishedFixtureAsync() {
        string path = NewPath();
        EventAddress anchor;
        SessionContextAnchorSetupReferences anchorSetups;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            engine.AppendObservation("old observation");
            anchor = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("old answer")
                ]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            anchorSetups =
                engine.ResolveContextAnchorSetupReferences(anchor);
        }
        RawSnapshot rawBefore = ReadRawSnapshot(path);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        var worldTarget = new MemoryPackBlockPath(
            MemoryPackCarrier.Observation,
            "memory.world"
        );
        var selfTarget = new MemoryPackBlockPath(
            MemoryPackCarrier.Action,
            "memory.self"
        );
        DerivedMemoryArtifact world = await WriteArtifactAsync(
            repository,
            "world-profile",
            worldTarget,
            "derived world",
            anchor,
            anchorSetups
        );
        DerivedMemoryArtifact self = await WriteArtifactAsync(
            repository,
            "self-profile",
            selfTarget,
            "derived self",
            anchor,
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
        _ = await repository.ArtifactSets.PublishAsync(
            new DerivedArtifactSetPublicationRequest(
                policy,
                "main",
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
            )
        );
        RawSnapshot rawAfter = ReadRawSnapshot(path);
        Assert.Equal(rawBefore, rawAfter);

        return new PublishedFixture(
            path,
            new DerivedArtifactSetContextCandidateSource(
                repository,
                policy,
                "main"
            ),
            policy.CoherenceGroup
        );
    }

    private static async ValueTask<DerivedMemoryArtifact>
        WriteArtifactAsync(
        DerivedMemoryRepository repository,
        string profileId,
        MemoryPackBlockPath target,
        string text,
        EventAddress anchor,
        SessionContextAnchorSetupReferences setups
    ) {
        return await DerivedMemoryArtifactTestFactory.WriteGenesisAsync(
            repository,
            profileId.EndsWith("-profile", StringComparison.Ordinal)
                ? profileId[..^"-profile".Length]
                : profileId,
            profileId,
            target,
            text,
            anchor,
            setups
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
        ContextCandidateSource: provider,
        ContextSelection: new SessionContextSelectionOptions(
            coherenceGroup
        )
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
        DerivedArtifactSetContextCandidateSource Provider,
        string CoherenceGroup
    );

    private sealed record RawSnapshot(
        EventAddress Head,
        int EventCount,
        int UnknownEventKindCount
    );
}
