using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaSessionProvisioningTests {
    [Fact]
    public void FirstTurnBootstrapPolicy_IsExactAndTimelineProjected() {
        RecapGridCadencePolicySpec cadence =
            GalateaFirstTurnBootstrapPolicy.Cadence;

        Assert.Equal(24_000, cadence.MinimumRecentHistoryLoad);
        Assert.Equal(60_000, cadence.TargetHistoryLoad);
        Assert.Equal(65_536, cadence.MaxRawEvents);
        Assert.Equal(1_048_576, cadence.MaxRenderedBytes);
        Assert.Equal(
            HistoryPartitionAlgorithms
                .FirstReplaySafeBoundaryAtTargetV1,
            cadence.PartitionAlgorithmId
        );
        Assert.Equal(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            cadence.HistoryLoadEstimatorId
        );

        HistoryTimelineInitialPolicySpec timeline =
            GalateaFirstTurnBootstrapPolicy.CreateTimelinePolicy();
        Assert.Equal(
            cadence.PartitionAlgorithmId,
            timeline.PartitionAlgorithmId
        );
        Assert.Equal(
            cadence.HistoryLoadEstimatorId,
            timeline.HistoryLoadEstimatorId
        );
        Assert.Equal(
            cadence.TargetHistoryLoad,
            timeline.TargetHistoryLoad.Value
        );
        Assert.Equal(cadence.MaxRawEvents, timeline.MaxRawEvents);
        Assert.Equal(
            cadence.MaxRenderedBytes,
            timeline.MaxRenderedBytes
        );
    }

    [Fact]
    public async Task MissingCreateIfMissing_ConcurrentlyCreatesOneFirstTurnReadyRepositoryFromExactDefault() {
        var factory = new CountingCompletionClientFactory();
        CompletionConnectionConfig decoy = Connection(
            "first",
            "model-decoy",
            "surface-decoy"
        );
        CompletionConnectionConfig selected = Connection(
            "selected",
            "model-selected",
            "surface-selected"
        );
        await using var host = GalateaTestHost.CreateMissingSession(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [decoy, selected],
            defaultConnectionId: selected.Id,
            systemPrompt: "resolved prompt"
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        using var ready = new CountdownEvent(16);
        using var start = new ManualResetEventSlim(false);
        Task<UserSessionHost>[] requests = [
            .. Enumerable.Range(0, 16).Select(_ => Task.Run(async () => {
                ready.Signal();
                start.Wait();
                return await service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                );
            }))
        ];
        bool allReady = ready.Wait(TimeSpan.FromSeconds(15));
        start.Set();
        Assert.True(allReady);
        UserSessionHost[] sessions = await Task.WhenAll(requests);

        UserSessionHost session = sessions[0];
        Assert.All(sessions, value => Assert.Same(session, value));
        Assert.True(Directory.Exists(host.SessionDirectory));
        AssertFirstTurnReadyRepository(session.Engine);
        SessionExecutionBoundaryInspection boundary =
            session.Engine.InspectExecutionBoundary();
        Assert.Equal(SessionExecutionPhase.Idle, boundary.Phase);
        var head = Assert.IsType<Atelia.EventJournal.EventAddress>(
            boundary.Head
        );
        SessionGoverningSetup governing =
            session.Engine.ResolveGoverningSetup(head);
        Assert.Equal(selected.ModelId, governing.RuntimeConfig.ModelId);
        Assert.Equal(
            selected.CompletionSurfaceId,
            governing.RuntimeConfig.CompletionSurfaceId
        );
        Assert.Equal("resolved prompt", governing.SystemPrompt);

        SessionCurrentLineageSnapshot lineage =
            session.Engine.ReadCurrentLineageHeaders();
        Assert.Equal(3, lineage.HeadToRoot.Count);
        Assert.Equal(
            [
                SessionEventKind.SessionCreated,
                SessionEventKind.SystemPromptSetup,
                SessionEventKind.RuntimeConfigSetup
            ],
            lineage.HeadToRoot.Select(static value => value.Kind)
        );

        RecentTurnsResponseDto recent = await service.GetRecentTurnsAsync(
            session,
            CancellationToken.None
        );
        Assert.Empty(recent.Turns);
        RecapGridReadinessSnapshotDto readiness = Assert.IsType<
            RecapGridReadinessSnapshotDto
        >(recent.RecapGridReadiness);
        Assert.Equal("exact", readiness.Freshness);
        Assert.Equal("raw-only", readiness.State);
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task MissingExistingOnly_FailsWithoutCreatingPath() {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            GalateaSessionProvisioning.ExistingOnly
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        GalateaSessionUnavailableException failure =
            await Assert.ThrowsAsync<GalateaSessionUnavailableException>(
                () => service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                )
            );

        Assert.Equal("session-unprovisioned", failure.Code);
        Assert.False(File.Exists(host.SessionDirectory));
        Assert.False(Directory.Exists(host.SessionDirectory));
    }

    [Fact]
    public async Task MissingCreateIfMissing_InMaintenanceModeFailsWithoutCreatingPath() {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            GalateaSessionProvisioning.CreateIfMissing,
            maintenanceMode: true,
            agentControlProfile: CreateNoControlCreateProfile()
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        GalateaSessionUnavailableException failure =
            await Assert.ThrowsAsync<GalateaSessionUnavailableException>(
                () => service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                )
            );

        Assert.Equal("session-unprovisioned", failure.Code);
        Assert.False(File.Exists(host.SessionDirectory));
        Assert.False(Directory.Exists(host.SessionDirectory));
    }

    [Theory]
    [InlineData("empty-directory")]
    [InlineData("incomplete-directory")]
    [InlineData("file")]
    public async Task ExistingNonRepositoryPath_IsNeverAdoptedOrOverwritten(
        string shape
    ) {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance
        );
        const string Sentinel = "operator-owned sentinel";
        switch (shape) {
            case "empty-directory":
                Directory.CreateDirectory(host.SessionDirectory);
                break;
            case "incomplete-directory":
                Directory.CreateDirectory(host.SessionDirectory);
                File.WriteAllText(
                    Path.Combine(host.SessionDirectory, "sentinel.txt"),
                    Sentinel
                );
                break;
            case "file":
                File.WriteAllText(host.SessionDirectory, Sentinel);
                break;
            default:
                throw new InvalidOperationException(shape);
        }
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        GalateaSessionUnavailableException failure =
            await Assert.ThrowsAsync<GalateaSessionUnavailableException>(
                () => service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                )
            );
        Assert.Equal("session-unprovisioned", failure.Code);

        if (shape == "file") {
            Assert.Equal(Sentinel, File.ReadAllText(host.SessionDirectory));
        }
        else if (shape == "incomplete-directory") {
            Assert.Equal(
                Sentinel,
                File.ReadAllText(Path.Combine(
                    host.SessionDirectory,
                    "sentinel.txt"
                ))
            );
            Assert.Single(Directory.EnumerateFileSystemEntries(
                host.SessionDirectory
            ));
        }
        else {
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                host.SessionDirectory
            ));
        }
    }

    [Fact]
    public async Task ExistingValidCreateIfMissing_OnlyOpensAndPreservesSetup() {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection(
                "test",
                "current-model",
                "current-surface"
            )],
            systemPrompt: "current prompt",
            agentControlProfile: CreateNoControlCreateProfile()
        );
        Atelia.EventJournal.EventAddress originalHead;
        using (SessionJournalEngine created = SessionJournalEngine.Create(
                   host.SessionDirectory,
                   new SessionCreateOptions(
                       "original-model",
                       "original prompt",
                       "original-surface"
                   ))) {
            originalHead = Assert.IsType<
                Atelia.EventJournal.EventAddress
            >(created.ReadCurrentHead());
        }
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        Assert.Equal(originalHead, session.Engine.ReadCurrentHead());
        SessionGoverningSetup setup =
            session.Engine.ResolveGoverningSetup(originalHead);
        Assert.Equal("original-model", setup.RuntimeConfig.ModelId);
        Assert.Equal(
            "original-surface",
            setup.RuntimeConfig.CompletionSurfaceId
        );
        Assert.Equal("original prompt", setup.SystemPrompt);
        Assert.Equal(
            3,
            session.Engine.ReadCurrentLineageHeaders().HeadToRoot.Count
        );
        Assert.IsType<RecapGridCadenceReaderOpenResult.Absent>(
            RecapGridCadenceFactory.OpenReader(session.Engine.ReadView)
        );
        Assert.IsType<HistoryTimelineOpenResult.Absent>(
            HistoryTimelineFactory.Open(
                session.Engine.ReadView,
                new O200kBaseHistoryUnitLoadEstimator()
            )
        );
    }

    [Fact]
    public async Task ExistingPartialCreateIfMissing_DoesNotCompleteDerivedBootstrap() {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance
        );
        Atelia.EventJournal.EventAddress originalHead;
        byte[] cadenceBytes;
        using (SessionJournalEngine created = SessionJournalEngine.Create(
                   host.SessionDirectory,
                   new SessionCreateOptions(
                       "model-a",
                       "test system prompt",
                       "openai-chat/strict"
                   ))) {
            originalHead = Assert.IsType<
                Atelia.EventJournal.EventAddress
            >(created.ReadCurrentHead());
            RecapGridCadenceCreateResult cadenceCreated =
                RecapGridCadenceFactory.Create(
                    created,
                    GalateaFirstTurnBootstrapPolicy.Cadence
                );
            cadenceBytes = Assert.IsType<
                RecapGridCadenceCreateResult.Created
            >(cadenceCreated).Snapshot.ToCanonicalBytes();
        }
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        Assert.Equal(originalHead, session.Engine.ReadCurrentHead());
        RecapGridCadenceReaderOpenResult cadenceOpened =
            RecapGridCadenceFactory.OpenReader(session.Engine.ReadView);
        using RecapGridCadenceReaderHandle cadence = Assert.IsType<
            RecapGridCadenceReaderOpenResult.Opened
        >(cadenceOpened).Handle;
        Assert.Equal(
            cadenceBytes,
            Assert.IsType<RecapGridCadenceReadResult.Available>(
                cadence.Reader.ReadSnapshot()
            ).Snapshot.ToCanonicalBytes()
        );
        Assert.IsType<HistoryTimelineOpenResult.Absent>(
            HistoryTimelineFactory.Open(
                session.Engine.ReadView,
                new O200kBaseHistoryUnitLoadEstimator()
            )
        );
        Assert.IsType<RecapGridControlReaderOpenResult.TimelineAbsent>(
            RecapGridControlFactory.OpenReader(
                host.SessionDirectory,
                session.Engine.BranchRefId
            )
        );
    }

    [Fact]
    public async Task FailedLazy_IsEvictedSoExternalProvisionCanRetryInSameService() {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance,
            GalateaSessionProvisioning.ExistingOnly
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        _ = await Assert.ThrowsAsync<GalateaSessionUnavailableException>(
            () => service.GetSessionAsync(
                "alice",
                CancellationToken.None
            )
        );
        using (SessionJournalEngine provisioned =
               SessionJournalEngine.Create(
                   host.SessionDirectory,
                   new SessionCreateOptions(
                       "model-a",
                       "test system prompt",
                       "openai-chat/strict"
                   ))) {
        }

        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
    }

    [Fact]
    public async Task CompetingServices_AtomicallyPublishExactlyOneCompleteRepository() {
        var factoryA = new CountingCompletionClientFactory();
        var factoryB = new CountingCompletionClientFactory();
        CompletionConnectionConfig connectionA = Connection(
            "test",
            "model-a",
            "surface-a"
        );
        CompletionConnectionConfig connectionB = Connection(
            "test",
            "model-b",
            "surface-b"
        );
        await using var hostA = GalateaTestHost.CreateMissingSession(
            factoryA,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [connectionA],
            systemPrompt: "prompt-a"
        );
        await using var hostB = GalateaTestHost.PointAtSession(
            hostA.SessionDirectory,
            [connectionB],
            connectionB.Id,
            factoryB,
            DisabledGalateaUserMessageNormalizer.Instance,
            "prompt-b",
            GalateaSessionProvisioning.CreateIfMissing
        );
        GalateaHostService serviceA = hostA.Factory.Services
            .GetRequiredService<GalateaHostService>();
        GalateaHostService serviceB = hostB.Factory.Services
            .GetRequiredService<GalateaHostService>();
        using var beforePublish = new Barrier(2);
        string? stagingA = null;
        string? stagingB = null;
        serviceA.SessionProvisioningHooksForTest = new(
            BeforeSessionRepositoryPublish: (staging, final) => {
                stagingA = staging;
                Assert.Equal(hostA.SessionDirectory, final);
                AssertCompleteStagingCandidate(staging);
                Assert.True(beforePublish.SignalAndWait(
                    TimeSpan.FromSeconds(15)
                ));
            }
        );
        serviceB.SessionProvisioningHooksForTest = new(
            BeforeSessionRepositoryPublish: (staging, final) => {
                stagingB = staging;
                Assert.Equal(hostA.SessionDirectory, final);
                AssertCompleteStagingCandidate(staging);
                Assert.True(beforePublish.SignalAndWait(
                    TimeSpan.FromSeconds(15)
                ));
            }
        );

        Task<SessionAttempt> attemptTaskA = Task.Run(() => ObserveAsync(
            () => serviceA.GetSessionAsync(
                "alice",
                CancellationToken.None
            )
        ));
        Task<SessionAttempt> attemptTaskB = Task.Run(() => ObserveAsync(
            () => serviceB.GetSessionAsync(
                "alice",
                CancellationToken.None
            )
        ));
        SessionAttempt[] attempts = await Task.WhenAll(
            attemptTaskA,
            attemptTaskB
        );

        SessionAttempt winner = Assert.Single(attempts,
            static value => value.Session is not null
        );
        SessionAttempt loser = Assert.Single(attempts,
            static value => value.Error is not null
        );
        IOException publicationConflict = Assert.IsType<IOException>(
            loser.Error
        );
        Assert.Equal(
            17,
            Assert.IsType<Win32Exception>(
                publicationConflict.InnerException
            ).NativeErrorCode
        );
        Assert.NotEqual(stagingA, stagingB);
        Assert.NotNull(stagingA);
        Assert.NotNull(stagingB);
        Assert.False(Directory.Exists(stagingA));
        Assert.False(Directory.Exists(stagingB));
        Assert.True(Directory.Exists(hostA.SessionDirectory));

        UserSessionHost winningSession = winner.Session!;
        AssertFirstTurnReadyRepository(winningSession.Engine);
        SessionExecutionBoundaryInspection boundary =
            winningSession.Engine.InspectExecutionBoundary();
        Assert.Equal(SessionExecutionPhase.Idle, boundary.Phase);
        var winnerHead = Assert.IsType<
            Atelia.EventJournal.EventAddress
        >(boundary.Head);
        Assert.Equal(
            [
                SessionEventKind.SessionCreated,
                SessionEventKind.SystemPromptSetup,
                SessionEventKind.RuntimeConfigSetup
            ],
            winningSession.Engine.ReadCurrentLineageHeaders()
                .HeadToRoot.Select(static value => value.Kind)
        );
        SessionGoverningSetup winnerSetup =
            winningSession.Engine.ResolveGoverningSetup(winnerHead);
        bool winnerIsA = ReferenceEquals(winner, attempts[0]);
        var expectedWinnerSetup = winnerIsA
            ? ("model-a", "surface-a", "prompt-a")
            : ("model-b", "surface-b", "prompt-b");
        Assert.Equal(
            expectedWinnerSetup,
            (
                winnerSetup.RuntimeConfig.ModelId,
                winnerSetup.RuntimeConfig.CompletionSurfaceId,
                winnerSetup.SystemPrompt
            )
        );
        GalateaHostService winnerService = winnerIsA
            ? serviceA
            : serviceB;
        RecentTurnsResponseDto recent =
            await winnerService.GetRecentTurnsAsync(
                winningSession,
                CancellationToken.None
            );
        Assert.Empty(recent.Turns);
        Assert.Equal(
            "raw-only",
            Assert.IsType<RecapGridReadinessSnapshotDto>(
                recent.RecapGridReadiness
            ).State
        );
        Assert.Equal(0, factoryA.CreateCallCount);
        Assert.Equal(0, factoryB.CreateCallCount);

        GalateaHostService loserService = ReferenceEquals(
            loser,
            attempts[0]
        ) ? serviceA : serviceB;
        await winningSession.DisposeAsync();
        UserSessionHost reopened = await loserService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(winnerHead, reopened.Engine.ReadCurrentHead());
        SessionGoverningSetup reopenedSetup =
            reopened.Engine.ResolveGoverningSetup(winnerHead);
        Assert.Equal(winnerSetup, reopenedSetup);
        Assert.Equal(0, factoryA.CreateCallCount);
        Assert.Equal(0, factoryB.CreateCallCount);
    }

    [Fact]
    public async Task BeforePublishHookFailure_CleansCandidateAndLeavesFinalAbsent() {
        await using var host = GalateaTestHost.CreateMissingSession(
            new CountingCompletionClientFactory(),
            DisabledGalateaUserMessageNormalizer.Instance
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        string? stagingPath = null;
        service.SessionProvisioningHooksForTest = new(
            BeforeSessionRepositoryPublish: (staging, _) => {
                stagingPath = staging;
                throw new TestPublishException();
            }
        );

        _ = await Assert.ThrowsAsync<TestPublishException>(() =>
            service.GetSessionAsync("alice", CancellationToken.None));

        Assert.NotNull(stagingPath);
        Assert.False(Directory.Exists(stagingPath));
        Assert.False(Directory.Exists(host.SessionDirectory));
        Assert.False(File.Exists(host.SessionDirectory));

        service.SessionProvisioningHooksForTest = null;
        UserSessionHost retry = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(
            SessionExecutionPhase.Idle,
            retry.Engine.InspectExecutionBoundary().Phase
        );
    }

    [Fact]
    public async Task BootstrapStepFailure_ClosesAndCleansOwnedCandidate() {
        var factory = new CountingCompletionClientFactory();
        await using var host = GalateaTestHost.CreateMissingSession(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        string? stagingPath = null;
        service.SessionProvisioningHooksForTest = new(
            AfterSessionRepositoryBootstrapStep: (component, staging) => {
                if (string.Equals(
                        component,
                        "Cadence",
                        StringComparison.Ordinal)) {
                    stagingPath = staging;
                    throw new TestBootstrapException();
                }
            }
        );

        _ = await Assert.ThrowsAsync<TestBootstrapException>(() =>
            service.GetSessionAsync("alice", CancellationToken.None));

        Assert.NotNull(stagingPath);
        Assert.False(Directory.Exists(stagingPath));
        Assert.False(Directory.Exists(host.SessionDirectory));
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Fact]
    public async Task MissingCreateIfMissing_WithoutControlCreatePermissionWritesNothing() {
        var factory = new CountingCompletionClientFactory();
        await using var host = GalateaTestHost.CreateMissingSession(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            agentControlProfile: CreateNoControlCreateProfile()
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();

        GalateaSessionUnavailableException failure =
            await Assert.ThrowsAsync<GalateaSessionUnavailableException>(
                () => service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                )
            );

        Assert.Equal("session-unprovisioned", failure.Code);
        Assert.False(Directory.Exists(host.SessionDirectory));
        Assert.Empty(Directory.EnumerateDirectories(
            host.RootDirectory,
            ".galatea-session-*.staging",
            SearchOption.TopDirectoryOnly
        ));
        Assert.Equal(0, factory.CreateCallCount);
        Assert.False(File.Exists(Path.Combine(
            Path.GetDirectoryName(host.ConfigPath)!,
            "recap-grid-routes.json"
        )));
    }

    [Fact]
    public async Task MissingCreateIfMissing_FirstTwoOrdinaryTurnsUseRawOnlyWithoutRecapDispatch() {
        var factory = new TwoTurnCompletionFactory();
        await using var host = GalateaTestHost.CreateMissingSession(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance
        );
        GalateaConfig config = GalateaConfigLoader.Load(host.ConfigPath);
        int routeLoads = 0;
        RecapGridCompletionHost completion = RecapGridCompletionHost.Create(
            () => {
                Interlocked.Increment(ref routeLoads);
                throw new InvalidOperationException(
                    "A first-turn raw-only repository must not load routes."
                );
            },
            new CompletionConnectionsFileConfig(
                config.Connections,
                config.DefaultConnectionId
            ),
            factory,
            config.RecapGrid!.AgentControlProfiles
        );
        var composition = new GalateaRecapGridComposition(
            completion,
            config.RecapGrid.CurrentAgentControlProfileId,
            estimators: [new O200kBaseHistoryUnitLoadEstimator()]
        );
        await using var service = new GalateaHostService(
            config,
            DisabledGalateaUserMessageNormalizer.Instance,
            composition
        );
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(0, factory.CreateCallCount);

        for (int index = 1; index <= 2; index++) {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                $"ordinary turn {index}",
                new GalateaTurnOptions("test")
            );
            await service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
            service.FinishTurn(session, turn);
            Assert.Equal("completed", turn.Status);
        }

        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(2, factory.Client.MainDispatchCallCount);
        Assert.Equal(0, factory.Client.RecapDispatchCallCount);
        Assert.Equal(0, routeLoads);
        Assert.Equal(
            2,
            session.Engine.ReadRecentCompletedTurns()
                .RequireSnapshot().Turns.Count
        );
        Assert.IsType<RecapGridStoreReaderOpenResult.Absent>(
            RecapGridStoreFactory.OpenReader(host.SessionDirectory)
        );
        Assert.False(File.Exists(Path.Combine(
            Path.GetDirectoryName(host.ConfigPath)!,
            "recap-grid-routes.json"
        )));
    }

    [Fact]
    public async Task HttpRecentTurns_FirstAuthenticatedSessionUseCreatesFirstTurnReadyRepository() {
        var factory = new CountingCompletionClientFactory();
        await using var host = GalateaTestHost.CreateMissingSession(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance
        );
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage login =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.False(Directory.Exists(host.SessionDirectory));

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/recent-turns"
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RecentTurnsResponseDto? recent = await response.Content
            .ReadFromJsonAsync<RecentTurnsResponseDto>();
        Assert.NotNull(recent);
        Assert.Empty(recent!.Turns);
        RecapGridReadinessSnapshotDto readiness = Assert.IsType<
            RecapGridReadinessSnapshotDto
        >(recent.RecapGridReadiness);
        Assert.Equal("raw-only", readiness.State);
        Assert.True(Directory.Exists(host.SessionDirectory));
        Assert.Equal(0, factory.CreateCallCount);
    }

    private static CompletionConnectionConfig Connection(
        string id,
        string modelId,
        string completionSurfaceId
    ) => new(
        id,
        "openai-chat",
        modelId,
        completionSurfaceId,
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static RecapGridAgentControlProfile
        CreateNoControlCreateProfile()
        => RecapGridAgentControlProfile.Create(
            "no-create",
            new RecapGridControlAdmission(
                RecapGridControlPermission.None,
                Array.Empty<FamilyDefinitionDigest>(),
                Array.Empty<string>(),
                Array.Empty<ContextHeaderCarrier>(),
                ["test."],
                maximumBootstrapRows: 0,
                maximumProjectedCalls: 0
            )
        );

    private static void AssertCompleteStagingCandidate(string stagingPath) {
        using SessionJournalEngine candidate =
            SessionJournalEngine.OpenReadOnly(stagingPath);
        AssertFirstTurnReadyRepository(candidate);
    }

    private static void AssertFirstTurnReadyRepository(
        SessionJournalEngine candidate
    ) {
        Assert.Equal(
            SessionExecutionPhase.Idle,
            candidate.InspectExecutionBoundary().Phase
        );
        Assert.Equal(
            [
                SessionEventKind.SessionCreated,
                SessionEventKind.SystemPromptSetup,
                SessionEventKind.RuntimeConfigSetup
            ],
            candidate.ReadCurrentLineageHeaders()
                .HeadToRoot.Select(static value => value.Kind)
        );
        var rawHead = Assert.IsType<Atelia.EventJournal.EventAddress>(
            candidate.ReadCurrentHead()
        );

        RecapGridCadenceReaderOpenResult cadenceOpened =
            RecapGridCadenceFactory.OpenReader(candidate.ReadView);
        using RecapGridCadenceReaderHandle cadence = Assert.IsType<
            RecapGridCadenceReaderOpenResult.Opened
        >(cadenceOpened).Handle;
        RecapGridCadenceSnapshot cadenceSnapshot = Assert.IsType<
            RecapGridCadenceReadResult.Available
        >(cadence.Reader.ReadSnapshot()).Snapshot;
        Assert.True(GalateaFirstTurnBootstrapPolicy.Matches(
            cadenceSnapshot.Policy
        ));

        HistoryTimelineOpenResult timelineOpened =
            HistoryTimelineFactory.Open(
                candidate.ReadView,
                new O200kBaseHistoryUnitLoadEstimator()
            );
        using HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(timelineOpened).Handle;
        TimelineHeadRef timelineHead = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(timeline.Reader.ReadSnapshot()).Head;
        Assert.Null(timelineHead.HeadRowId);
        Assert.Equal(0, timelineHead.Generation);
        Assert.Equal(
            GalateaFirstTurnBootstrapPolicy.CreateTimelinePolicy(
                timelineHead.TimelineId
            ).PolicyDigest,
            timelineHead.ActivePartitionPolicyDigest
        );

        RecapGridControlReaderOpenResult controlOpened =
            RecapGridControlFactory.OpenReader(
                candidate.Path,
                candidate.BranchRefId
            );
        using RecapGridControlReaderHandle control = Assert.IsType<
            RecapGridControlReaderOpenResult.Opened
        >(controlOpened).Handle;
        RecapGridControlSnapshot controlSnapshot = Assert.IsType<
            RecapGridControlSnapshotResult.Available
        >(control.Reader.ReadSnapshot()).Snapshot;
        Assert.Equal(timelineHead.TimelineId,
            controlSnapshot.Head.TimelineId);
        Assert.Equal(0, controlSnapshot.Head.Generation);
        Assert.Null(controlSnapshot.ActiveRecipe);
        Assert.Empty(controlSnapshot.Families);
        Assert.Empty(controlSnapshot.Definitions);
        Assert.Empty(controlSnapshot.Recipes);
        Assert.IsType<RecapGridStoreReaderOpenResult.Absent>(
            RecapGridStoreFactory.OpenReader(candidate.Path)
        );

        RecapGridContextOpenResult getterOpened =
            RecapGridContextFactory.Open(
                candidate.ReadView,
                new O200kBaseHistoryUnitLoadEstimator()
            );
        using RecapGridContextHandle getter = Assert.IsType<
            RecapGridContextOpenResult.Opened
        >(getterOpened).Handle;
        Assert.IsType<RecapGridContextResolveResult.RawHistoryAuthorized>(
            getter.Resolve(rawHead, nthPrevious: 0)
        );
    }

    private sealed class CountingCompletionClientFactory
        : ICompletionClientFactory {
        private int _createCallCount;

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            _ = connection;
            Interlocked.Increment(ref _createCallCount);
            throw new InvalidOperationException(
                "Session provisioning must not create a Completion client."
            );
        }
    }

    private sealed class TwoTurnCompletionFactory
        : ICompletionClientFactory {
        private int _createCallCount;

        internal TwoTurnCompletionClient Client { get; } = new();
        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            _ = connection;
            Interlocked.Increment(ref _createCallCount);
            return Client;
        }
    }

    private sealed class TwoTurnCompletionClient : ICompletionClient {
        private int _mainDispatchCallCount;
        private int _recapDispatchCallCount;

        public string Name => "galatea-first-turn-bootstrap-test";
        public string ApiSpecId => "openai-chat-v1";
        internal int MainDispatchCallCount => Volatile.Read(
            ref _mainDispatchCallCount
        );
        internal int RecapDispatchCallCount => Volatile.Read(
            ref _recapDispatchCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            bool isRecap = request.TailMessages is [ObservationMessage {
                Content: { } tail
            }] && tail.Contains(
                $"\"schema\":\"{RecapRewriterProtocolV3.InputProtocolId}\"",
                StringComparison.Ordinal
            );
            if (isRecap) {
                Interlocked.Increment(ref _recapDispatchCallCount);
                throw new InvalidOperationException(
                    "A first-turn raw-only repository must not dispatch recap work."
                );
            }
            int call = Interlocked.Increment(
                ref _mainDispatchCallCount
            );
            string response = $"answer {call}";
            observer?.OnTextDelta(response);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(response)]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }

    private static async Task<SessionAttempt> ObserveAsync(
        Func<Task<UserSessionHost>> action
    ) {
        try {
            return new SessionAttempt(await action(), Error: null);
        }
        catch (Exception exception) {
            return new SessionAttempt(Session: null, exception);
        }
    }

    private sealed record SessionAttempt(
        UserSessionHost? Session,
        Exception? Error
    );

    private sealed class TestPublishException : Exception;
    private sealed class TestBootstrapException : Exception;
}
