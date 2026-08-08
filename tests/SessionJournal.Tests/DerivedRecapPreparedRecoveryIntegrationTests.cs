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
    private const string ProfileName = "fixed-recap-profile";
    private const string PolicyId = "atelia.tests.first-build-v1";

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
            RecapMaintainerCapabilitySnapshot capabilities = new([
                new RecapProfilePlanningDescriptor(
                    ProfileName,
                    blockId,
                    target,
                    maintainer.Id,
                    maintainer.CapabilityFingerprint
                )
            ]);
            ResolvedRecapPlanningConfiguration configuration =
                CreateConfiguration(
                policy,
                capabilities
            );
            var source = new FixedConfigurationSource(configuration);
            var ready = Assert.IsType<
                DerivedRecapOperationPreparationResult.Ready
            >(await DerivedRecapOperationPreparer.PrepareAsync(
                engine.ReadView,
                store,
                capabilities,
                source,
                CancellationToken.None
            ));
            var coordinator =
                DerivedRecapOnlineLifecycleCoordinator.Create(
                    engine.ReadView,
                    store,
                    ready.Authority,
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
                    engine.ReadView,
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
        using (var reopened = SessionJournalTestRuntime.Attach(
            SessionJournalEngine.Open(
                path
            ),
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

    private static ResolvedRecapPlanningConfiguration
        CreateConfiguration(
        IRecapPlanningPolicy policy,
        RecapMaintainerCapabilitySnapshot capabilities
    ) {
        var estimator = new ConstantHistoryUnitLoadEstimator();
        var document = new RecapPlannerConfigDocument(
            RecapPlannerConfigCodec.SchemaV2,
            PolicyId,
            new RecapCadenceConfigDocument(
                estimator.Id,
                MinimumRecentHistoryLoad: 0,
                RecapBuildIntervalHistoryLoad: 2
            ),
            [new RecapPlannerCatalogEntryDocument(ProfileName, 4096)],
            new RecapPlannerLimitsDocument(512, 4, 4, 64, 512)
        );
        var catalog = new RecapPlannerConfigResolutionCatalog(
            [policy],
            [estimator]
        );
        return Assert.IsType<RecapPlannerConfigResolveResult.Resolved>(
            RecapPlannerConfigResolver.Resolve(
                RecapPlannerConfigSnapshot.FromDocument(document),
                catalog,
                capabilities
            )
        ).Configuration;
    }

    private sealed class FixedConfigurationSource(
        ResolvedRecapPlanningConfiguration configuration
    ) : IRecapActivePlanningConfigurationSource {
        public RecapActivePlanningConfigurationLoadResult Load()
            => new RecapActivePlanningConfigurationLoadResult.Available(
                configuration
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
        public string Id => PolicyId;

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
                        [admission]
                    )
                ]
            );
        }
    }

    private sealed class FixedRecapMaintainer(
        ContextHeaderBlockPath target
    ) : IRecapBlockMaintainer {
        public string Id => "fixed-recap";
        public string CapabilityFingerprint =>
            "sha256:0000000000000000000000000000000000000000000000000000000000000000";

        public ContextHeaderBlockPath Target { get; } = target;

        public int CallCount { get; private set; }

        public ValueTask<RecapMaintenanceSuccess> MaintainAsync(
            RecapMaintenanceEpochInput request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(
                (RecapMaintenanceSuccess)new
                    RecapMaintenanceSuccess.Updated(RecapText)
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
