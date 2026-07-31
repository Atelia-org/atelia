using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Planner;
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
    public async Task PreparedReopenDoesNotReadDeletedRecapStore() {
        string path = NewPath();
        var sourceClient = new CapturingClient("must not run");
        EventAddress preparedAddress;

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            ),
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
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-a"
                )
            );

            DerivedRecapStore store = DerivedRecapStore.Open(
                path,
                engine.BranchRefId
            );
            await store.CreateAsync();
            RecapBlockId blockId = new("roleplay.self");
            var target = new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            );
            var maintainer = new FixedRecapMaintainer(target);
            var policy = new FirstBuildPolicy(blockId);
            RecapPlanningInputs inputs = CreateInputs(
                blockId,
                target,
                maintainer.Id,
                policy
            );
            var coordinator =
                new DerivedRecapOnlineLifecycleCoordinator(
                    engine,
                    store,
                    inputs,
                    new RecapPlanningLimits(
                        maxRawGrowthEventCount: 512,
                        maxRouteEndpointsPerBlock: 4,
                        maxMaintainerCallsPerBuild: 4,
                        maxRawEventsPerStep: 64,
                        maxRawEventsPerBuild: 512
                    ),
                    new RecapBlockMaintainerRegistry([maintainer])
                );

            SessionExecutionBoundaryInspection boundary =
                engine.InspectExecutionBoundary();
            EventAddress idleHead = boundary.Head!.Value;
            SessionGoverningSetup setup =
                engine.ResolveGoverningSetup(idleHead);
            var selectionRequest =
                new SessionContextSelectionRequest(
                    idleHead,
                    setup.RuntimeConfig.DerivedContext.NthPrevious
                );
            SessionContextLifecycleResult prepared =
                await coordinator.PrepareAsync(
                    engine,
                    new SessionContextLifecycleRequest(
                        selectionRequest,
                        boundary.Phase
                    ),
                    CancellationToken.None
                );
            Assert.Equal(
                SessionContextLifecycleStatus.Ready,
                prepared.Status
            );
            Assert.Equal(1, maintainer.CallCount);

            SessionContextCandidateSelection selected =
                await coordinator.SelectAsync(
                    selectionRequest,
                    CancellationToken.None
                );
            SessionContextCandidateDescriptor descriptor =
                Assert.IsType<SessionContextCandidateDescriptor>(
                    selected.Candidate
                );
            SessionContextCandidate candidate =
                await coordinator.MaterializeAsync(
                    descriptor,
                    CancellationToken.None
                );
            Assert.Equal(
                RecapText,
                Assert.Single(candidate.Contributions).ExactText
            );

            engine.UseRuntime(
                CreateRuntime(
                    sourceClient,
                    coordinator,
                    coordinator
                )
            );
            SessionJournalFailpointException failure =
                await Assert.ThrowsAsync<
                    SessionJournalFailpointException
                >(
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
            SessionExecutionBoundaryInspection preparedBoundary =
                engine.InspectExecutionBoundary();
            Assert.Equal(
                SessionExecutionPhase.AwaitingCompletionDispatch,
                preparedBoundary.Phase
            );
            preparedAddress = preparedBoundary.Head!.Value;
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

        string recapRoot = Path.Combine(
            path,
            "derived",
            "recap",
            "v4"
        );
        Assert.True(Directory.Exists(recapRoot));
        Directory.Delete(recapRoot, recursive: true);

        var forbidden = new ThrowingContextCollaborator();
        var recoveryClient = new CapturingClient("recovered answer");
        ResumeOutcome outcome;
        using (var reopened = SessionJournalEngine.Open(
            path,
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
            outcome = await reopened.ResumeAsync(
                CancellationToken.None
            );
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
            SessionRequestCanonicalizer.Canonicalize(
                recoveredRequest
            )
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

    private static RecapPlanningInputs CreateInputs(
        RecapBlockId blockId,
        ContextHeaderBlockPath target,
        string maintainerId,
        IRecapPlanningPolicy policy
    ) {
        var estimator = new ConstantHistoryUnitLoadEstimator();
        return new(
            [
                new RecapBlockCatalogEntry(
                    blockId,
                    target,
                    maintainerId,
                    maxContentUtf8Bytes: 4096
                )
            ],
            new RecapCadenceConfig(
                estimator.Id,
                new HistoryLoadUnit(0),
                new HistoryLoadUnit(2)
            ),
            estimator,
            policy
        );
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

    private sealed class ConstantHistoryUnitLoadEstimator
        : IHistoryUnitLoadEstimator {
        public string Id =>
            "atelia.tests.history-load.constant-v1";

        public HistoryUnitLoadMeasurement Measure(
            SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) => new(
            new HistoryLoadUnit(1),
            RenderedUtf8Bytes: 1
        );
    }

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-recap-prepared-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class FirstBuildPolicy(RecapBlockId blockId)
        : IRecapPlanningPolicy {
        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) {
            if (context.Scheduling.LatestPublishedSetAnchor is not null) {
                return new RecapPlanningPolicyDecision.NoBuild(
                    "latest-exists"
                );
            }
            EventAddress start =
                context.Scheduling.HistoryWindow.StartExclusive;
            EventAddress admission =
                context.Scheduling.CapturedHead;
            return new RecapPlanningPolicyDecision.Build(
                admission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        blockId,
                        new RecapPlanningMaintainSource.Empty(start),
                        [admission],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            );
        }
    }

    private sealed class FixedRecapMaintainer(
        ContextHeaderBlockPath target
    ) : IRecapBlockMaintainer {
        public string Id => "fixed-recap";

        public ContextHeaderBlockPath Target { get; } = target;

        public int CallCount { get; private set; }

        public ValueTask<RecapBlockMaintenanceResult> MaintainAsync(
            RecapBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(
                new RecapBlockMaintenanceResult(
                    Id,
                    Target,
                    new ContextHeaderBlock(RecapText)
                )
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
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
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
            SessionJournalEngine engine,
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
