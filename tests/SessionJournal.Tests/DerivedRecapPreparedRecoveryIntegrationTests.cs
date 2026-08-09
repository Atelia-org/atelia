using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class DerivedRecapPreparedRecoveryIntegrationTests
    : IDisposable {
    private const string RecapText =
        "durable recap survives derived Store deletion";

    private static readonly SessionCompletionTargetIdentity Target =
        new(
            "derived-recap-integration",
            "test",
            "derived-recap-connection-v1",
            "derived-recap-adapter-v1"
        );

    private readonly List<string> _paths = [];

    [Fact]
    public async Task PreparedReopenDoesNotReadDeletedV8RecapStore() {
        string path = NewPath();
        var sourceClient = new CapturingClient("must not run");
        EventAddress preparedAddress;

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-a", "system-a", "surface-a"),
            runtime: null!,
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterRequestPreparedCommitted
            )
        )) {
            engine.AppendObservation("old observation");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("old answer")
                ]),
                new CompletionDescriptor("import", "import-v1", "model-a")
            );

            DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
                path,
                engine.BranchRefId
            );
            await store.CreateAsync();
            EventAddress idleHead = engine.ReadCurrentHead()!.Value;
            SessionHistoryPlanningSeed start = Assert.IsType<
                SessionCreatedPlanningSeedReadResult.Available
            >(engine.ReadView.ReadSessionCreatedPlanningSeedAtBounded(
                idleHead,
                32
            )).Seed;
            SessionHistoryPlanningWindow window = Assert.IsType<
                SessionHistoryPlanningWindowReadResult.Available
            >(engine.ReadView.ReadHistoryPlanningWindowAtBounded(
                idleHead,
                start,
                32
            )).Window;
            var definition = new RecapEpochBlockDefinition(
                new RecapBlockId("roleplay.self"),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "roleplay.self"
                ),
                "fixed-recap",
                "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                4096,
                0
            );
            DerivedRecapEpochInput input = DerivedRecapV8Codec
                .CreateEpochInput(
                    new RecapEpochBoundary(start.Address, start.Setups),
                    new RecapEpochBoundary(idleHead, window.EndSetups),
                    window.RawAddresses.Count,
                    window.RawRangeSha256,
                    Array.AsReadOnly([
                        .. window.Units.Select(static unit => unit.Message)
                    ]),
                    RecapEpochPrevious.Empty.Instance
                );
            DerivedRecapEpochManifest manifest = DerivedRecapV8Codec
                .CreateManifest(
                    engine.BranchRefId,
                    idleHead,
                    input.PayloadSha256,
                    [definition]
                );
            _ = Assert.IsType<InstallRecapEpochBuildingResult.Installed>(
                await store.InstallBuildingAsync(
                    manifest,
                    input,
                    idleHead,
                    engine.ReadView.ReadCurrentHead
                )
            );
            RecapEpochStoreSnapshot building = Assert.IsType<
                RecapEpochStoreReadResult.Available
            >(await store.ReadBuildingAsync(idleHead)).Snapshot;
            Assert.IsType<WriteRecapEpochFinalResult.Installed>(
                await store.WriteFinalAsync(
                    Assert.Single(building.Blocks).WriteAuthority!,
                    DerivedRecapV8Codec.CreateFinalBlock(
                        manifest,
                        definition,
                        RecapText
                    )
                )
            );
            Assert.IsType<PublishRecapEpochResult.Published>(
                await store.PublishBuildingAsync(
                    building.Descriptor,
                    idleHead,
                    engine.ReadView.ReadCurrentHead
                )
            );
            engine.AppendObservation("recent observation");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("recent answer")
                ]),
                new CompletionDescriptor("import", "import-v1", "model-a")
            );

            var candidates = new DerivedRecapContextCandidateSource(
                store,
                engine.ReadView
            );
            var lifecycle = new ReadyContextLifecycle();
            engine.UseRuntime(CreateRuntime(
                sourceClient,
                candidates,
                lifecycle
            ));
            SessionJournalFailpointException failure =
                await Assert.ThrowsAsync<SessionJournalFailpointException>(
                    () => engine.SendAsync(
                        "durable observation",
                        CancellationToken.None
                    )
                );
            Assert.Equal(
                SessionJournalFailpoint.AfterRequestPreparedCommitted,
                failure.Failpoint
            );
            Assert.Empty(sourceClient.Requests);
            preparedAddress = engine.InspectExecutionBoundary().Head!.Value;
        }

        byte[] expectedCanonical;
        using (EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(path)) {
            SessionPreparedRequestReconstruction reconstruction =
                SessionPreparedRequestReconstructor.Reconstruct(
                    journal,
                    preparedAddress
                );
            expectedCanonical = reconstruction.CanonicalBytes;
            Assert.Contains(
                RecapText,
                Encoding.UTF8.GetString(expectedCanonical),
                StringComparison.Ordinal
            );
        }

        string recapRoot = Path.Combine(path, "derived", "recap", "v8");
        Assert.True(Directory.Exists(recapRoot));
        Directory.Delete(recapRoot, recursive: true);

        var forbidden = new ThrowingContextCollaborator();
        var recoveryClient = new CapturingClient("recovered answer");
        ResumeOutcome outcome;
        using (var reopened = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Open(path),
            CreateRuntime(
                recoveryClient,
                forbidden,
                forbidden
            ) with {
                UncertainCompletionRecoveryPolicy =
                    SessionUncertainCompletionRecoveryPolicy
                        .RestartWithNewAttempt
            }
        )) {
            outcome = await reopened.ResumeAsync(CancellationToken.None);
        }

        Assert.True(outcome.Advanced);
        Assert.Equal(
            "recovered answer",
            outcome.Message?.GetFlattenedText()
        );
        CompletionRequest recoveredRequest =
            Assert.Single(recoveryClient.Requests);
        Assert.Equal(
            expectedCanonical,
            SessionRequestCanonicalizer.Canonicalize(recoveredRequest)
        );
        Assert.Equal(0, forbidden.LifecycleCalls);
        Assert.Equal(0, forbidden.SelectionCalls);
        Assert.Equal(0, forbidden.MaterializationCalls);
        Assert.False(Directory.Exists(recapRoot));
    }

    public void Dispose() {
        foreach (string path in _paths) {
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

    private static SessionRuntime CreateRuntime(
        ICompletionClient client,
        ICoherentContextCandidateSource candidates,
        ISessionContextLifecycleCoordinator lifecycle
    ) => new(
        CompletionClient: client,
        CompletionTarget: Target,
        MaxTokens: 256,
        ContextCandidateSource: candidates,
        ContextLifecycle: lifecycle
    );

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-recap-prepared-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class ReadyContextLifecycle
        : ISessionContextLifecycleCoordinator {
        public ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                SessionContextLifecycleResult.Ready
            );
        }
    }

    private sealed class CapturingClient(string response)
        : ICompletionClient {
        public string Name => "derived-recap-integration";
        public string ApiSpecId => "derived-recap-integration-v1";
        public List<CompletionRequest> Requests { get; } = [];

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text(response)
                ]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }

    private sealed class ThrowingContextCollaborator
        : ISessionContextLifecycleCoordinator,
          ICoherentContextCandidateSource {
        public int LifecycleCalls { get; private set; }
        public int SelectionCalls { get; private set; }
        public int MaterializationCalls { get; private set; }

        public ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) {
            LifecycleCalls++;
            throw new Xunit.Sdk.XunitException(
                "Prepared recovery must not invoke context lifecycle."
            );
        }

        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) {
            SelectionCalls++;
            throw new Xunit.Sdk.XunitException(
                "Prepared recovery must not select derived context."
            );
        }

        public ValueTask<SessionContextCandidate> MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) {
            MaterializationCalls++;
            throw new Xunit.Sdk.XunitException(
                "Prepared recovery must not materialize derived context."
            );
        }
    }
}
