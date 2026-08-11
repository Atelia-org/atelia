using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Online;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRecapGridCandidateCompositionTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Fact]
    public async Task ActualServiceFreshAndObservationRecoveryUsePerTurnOnline() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine provisioner = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a", "test system prompt", "openai-chat/strict"))) {
            refId = provisioner.BranchRefId;
            ProvisionTimelineAndControl(provisioner);
        }
        string oldV8 = Path.Combine(
            path, "derived", "recap", "v8", "corrupt-sentinel.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(oldV8)!);
        File.WriteAllBytes(oldV8, [8, 0, 8, 0]);
        byte[] oldV8Before = File.ReadAllBytes(oldV8);

        CompletionConnectionConfig connection = Connection();
        var candidateFactory = new TrackingFactory("candidate answer");
        int routeLoads = 0;
        RecapGridCompletionHost completion = RecapGridCompletionHost.Create(
            () => {
                Interlocked.Increment(ref routeLoads);
                throw new InvalidOperationException(
                    "A raw-only turn must not load recap routes.");
            },
            Connections(connection),
            candidateFactory);
        var candidate = new GalateaRecapGridCandidateComposition(
            completion,
            RecapGridOnlineLimits.Production,
            _estimator);
        var legacyFactory = new RejectingFactory();
        await using var legacy = new CompletionConnectionRegistry(
            Connections(connection), legacyFactory);
        await using var service = new GalateaHostService(
            Config(path, connection),
            legacy,
            DisabledGalateaUserMessageNormalizer.Instance,
            candidate);
        UserSessionHost session = await service.GetSessionAsync(
            "alice", CancellationToken.None);

        GalateaLiveTurn fresh = service.StartTurn(
            session,
            "fresh candidate",
            new GalateaTurnOptions(connection.Id));
        await service.RunTurnAsync(session, fresh, CancellationToken.None);
        service.FinishTurn(session, fresh);

        Assert.Equal("completed", fresh.Status);
        Assert.Equal(1, candidateFactory.CreateCallCount);
        Assert.Equal(1, candidateFactory.Client.DispatchCallCount);
        Assert.Equal(0, legacyFactory.CreateCallCount);
        Assert.Equal(0, routeLoads);
        Assert.False(File.Exists(Path.Combine(
            path, "derived", "recap-grid", "v1", "grid.sqlite")));
        Assert.Single(session.Engine.ReadRecentCompletedTurns().Turns);

        EventAddress observationHead = session.Engine.AppendObservation(
            GalateaHostService.WrapUserMessageForEngine(
                "observation accepted candidate"));
        GalateaLiveTurn recovery = service.StartRecovery(
            session,
            new GalateaTurnOptions(
                connection.Id,
                GalateaTurnMode.Resume,
                ExpectedHead: observationHead));
        await service.RunTurnAsync(
            session, recovery, CancellationToken.None);
        service.FinishTurn(session, recovery);

        Assert.Equal("completed", recovery.Status);
        Assert.Equal(1, candidateFactory.CreateCallCount);
        Assert.Equal(2, candidateFactory.Client.DispatchCallCount);
        Assert.Equal(0, legacyFactory.CreateCallCount);
        Assert.Equal(0, routeLoads);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase);
        Assert.Equal(2, session.Engine.ReadRecentCompletedTurns().Turns.Count);
        Assert.Equal(refId, session.Engine.BranchRefId);
        Assert.Equal(oldV8Before, File.ReadAllBytes(oldV8));
    }

    [Fact]
    public async Task ActualServiceBuildsMissingGridWorkThroughExactRoute() {
        string path = NewPath();
        FamilyDefinition family;
        MaintainerDefinitionRevision definition;
        GridBuildRecipe recipe;
        using (SessionJournalEngine provisioner = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a", "test system prompt", "openai-chat/strict"))) {
            ProvisionTimelineAndControl(provisioner);
            (family, definition, recipe) =
                ProvisionActiveEmptyRecipe(provisioner);
        }

        CompletionConnectionConfig connection = Connection();
        var candidateFactory = new TrackingFactory("candidate answer");
        int routeLoads = 0;
        RecapGridCompletionHost completion = RecapGridCompletionHost.Create(
            () => {
                Interlocked.Increment(ref routeLoads);
                return RecapGridRouteManifest.Create([
                    new RecapGridRouteManifestEntry(
                        new RecapCompletionRouteKey(
                            family.Digest,
                            RecapCompletionProtocolV1.RuntimeProtocolId,
                            null),
                        connection.Id,
                        maximumConcurrency: 1,
                        dispatchTimeout: TimeSpan.FromSeconds(30),
                        maximumOutputTokens: 1_024)
                ]);
            },
            Connections(connection),
            candidateFactory);
        var candidate = new GalateaRecapGridCandidateComposition(
            completion,
            RecapGridOnlineLimits.Production,
            _estimator);
        var legacyFactory = new RejectingFactory();
        await using var legacy = new CompletionConnectionRegistry(
            Connections(connection), legacyFactory);
        await using var service = new GalateaHostService(
            Config(path, connection),
            legacy,
            DisabledGalateaUserMessageNormalizer.Instance,
            candidate);
        UserSessionHost session = await service.GetSessionAsync(
            "alice", CancellationToken.None);

        await RunFreshAsync(service, session, connection.Id, "first clue");
        Assert.Equal(1, candidateFactory.Client.AgentDispatchCount);
        Assert.Equal(0, candidateFactory.Client.RecapDispatchCount);

        await RunFreshAsync(service, session, connection.Id, "second clue");

        Assert.Equal(2, candidateFactory.Client.AgentDispatchCount);
        Assert.True(candidateFactory.Client.RecapDispatchCount > 0);
        Assert.Equal(1, routeLoads);
        Assert.Equal(0, legacyFactory.CreateCallCount);
        Assert.True(File.Exists(Path.Combine(
            path, "derived", "recap-grid", "v1", "grid.sqlite")));
        TimelineHeadRef fulfilledHead;
        HistoryTimelineSelectedRow fulfilledRow;
        using (HistoryTimelineReaderHandle timeline = Assert.IsType<
                   HistoryTimelineReaderOpenResult.Opened>(
                   HistoryTimelineMaintenance.OpenReader(
                       path, session.Engine.BranchRefId)).Handle) {
            fulfilledHead = Assert.IsType<
                HistoryTimelineSnapshotResult.Available>(
                timeline.Reader.ReadSnapshot()).Head;
            Assert.NotNull(fulfilledHead.HeadRowId);
            fulfilledRow = Assert.IsType<
                HistoryTimelineReaderRowResult.Selected>(
                timeline.Reader.ReadSelectedRow(
                    fulfilledHead,
                    fulfilledHead.HeadRowId!.Value)).Row;
        }
        using (RecapGridStoreReaderHandle store = Assert.IsType<
                   RecapGridStoreReaderOpenResult.Opened>(
                   RecapGridStoreFactory.OpenReader(path)).Handle) {
            Assert.IsType<RecapGridStoreReadResult<
                RecapGridFulfilledView>.Found>(
                store.Reader.ReadFulfilled(FulfilledViewKey.Create(
                    session.Engine.BranchRefId,
                    fulfilledHead,
                    fulfilledRow.Descriptor.DescriptorDigest,
                    recipe)));
        }
        await service.DisposeAsync();
        Atelia.SessionJournal.Offline.SessionJournalOfflineValidationReport
            raw = await Atelia.SessionJournal.Offline
                .SessionJournalOfflineValidator.ValidateAsync(path);
        Assert.Equal(2, raw.PreparedRequestCount);
        Assert.Equal(SessionExecutionPhase.Idle, raw.ExecutionPhase);
        Assert.Equal(
            definition.Digest,
            recipe.Target.OrderedColumns[0].DefinitionDigest);
    }

    [Fact]
    public async Task CliAndGalateaHostsProduceExactSameDerivedCandidate() {
        string basePath = NewPath();
        FamilyDefinition family;
        using (SessionJournalEngine writer = SessionJournalEngine.Create(
            basePath,
            new SessionCreateOptions(
                "model-a", "test system prompt", "openai-chat/strict"))) {
            _ = writer.AppendObservation("X entered the locked room.");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("Investigate X.")
                ]),
                new CompletionDescriptor("import", "v1", "model-a"));
            ProvisionTimelineAndControl(writer);
            await using RecapGridOnlineContextHandle online = Assert.IsType<
                RecapGridOnlineOpenResult.Opened>(
                RecapGridOnlineFactory.Open(
                    writer,
                    new RejectingBatchExecutor(),
                    RecapGridOnlineLimits.Production,
                    _estimator)).Handle;
            EventAddress boundary = writer.ReadCurrentHead()!.Value;
            Assert.IsType<RecapGridOnlinePassResult.RawHistoryAuthorized>(
                await online.PreparePassAsync(
                    writer.ReadView,
                    new SessionContextLifecycleRequest(
                        new SessionContextSelectionRequest(boundary, 0),
                        SessionExecutionPhase.Idle,
                        SessionContextLifecycleTrigger.PreObservation,
                        "pending")));
            (family, _, _) = ProvisionActiveCurrentRecipe(writer);
        }
        string cliPath = NewPath();
        string galateaPath = NewPath();
        CopyDirectory(basePath, cliPath);
        CopyDirectory(basePath, galateaPath);
        CompletionConnectionConfig connection = Connection();
        string external = Path.Combine(NewPath(), "inputs");
        Directory.CreateDirectory(external);
        string connectionsPath = Path.Combine(external, "connections.json");
        File.WriteAllText(connectionsPath, """
            {"connections":[{"id":"test","kind":"openai-chat","modelId":"model-a","completionSurfaceId":"openai-chat/strict","baseAddress":"http://localhost:8000/","apiKey":"test-key"}],"defaultConnectionId":"test"}
            """);
        string routesPath = Path.Combine(external, "routes.json");
        File.WriteAllBytes(
            routesPath,
            RecapGridRouteManifest.Create([
                new RecapGridRouteManifestEntry(
                    new RecapCompletionRouteKey(
                        family.Digest,
                        RecapCompletionProtocolV1.RuntimeProtocolId,
                        null),
                    connection.Id,
                    maximumConcurrency: 1,
                    dispatchTimeout: TimeSpan.FromSeconds(30),
                    maximumOutputTokens: 1_024)
            ]).ToCanonicalBytes());
        RecapGridAgentControlProfile agentProfile = AgentProfile();
        string admissionPath = Path.Combine(external, "admission.json");
        File.WriteAllBytes(
            admissionPath,
            agentProfile.Admission.ToCanonicalBytes()
        );
        string refId;
        using (SessionJournalEngine reader =
               SessionJournalEngine.OpenReadOnly(cliPath)) {
            refId = reader.BranchRefId.ToHexString();
        }
        var cliFactory = new TrackingFactory("same agent answer");
        Assert.Equal(0, Atelia.SessionJournal.Cli.Program.MainCore(
            [
                "recap-grid", "candidate", "run-online-turn",
                "--input", cliPath,
                "--branch", SessionJournalDefaults.MainBranchName,
                "--confirm-ref", refId,
                "--message", "same next clue",
                "--connection", connection.Id,
                "--connections", connectionsPath,
                "--routes", routesPath,
                "--admission", admissionPath
            ],
            cliFactory));

        var galateaFactory = new TrackingFactory("same agent answer");
        RecapGridCompletionHost completion = RecapGridCompletionHost.Create(
            () => RecapGridRouteManifest.DecodeCanonical(
                File.ReadAllBytes(routesPath)),
            Connections(connection),
            galateaFactory,
            new RecapGridAgentControlProfileRegistry([agentProfile]));
        var candidate = new GalateaRecapGridCandidateComposition(
            completion,
            agentProfile.ProfileId,
            RecapGridOnlineLimits.Production,
            _estimator);
        var legacyFactory = new RejectingFactory();
        await using (var legacy = new CompletionConnectionRegistry(
                         Connections(connection), legacyFactory))
        await using (var service = new GalateaHostService(
                         Config(galateaPath, connection),
                         legacy,
                         DisabledGalateaUserMessageNormalizer.Instance,
                         candidate)) {
            UserSessionHost session = await service.GetSessionAsync(
                "alice", CancellationToken.None);
            await RunFreshAsync(
                service, session, connection.Id, "same next clue");
        }

        DerivedSnapshot cli = ReadDerivedSnapshot(cliPath);
        DerivedSnapshot galatea = ReadDerivedSnapshot(galateaPath);
        Assert.Equal(cli.TimelineHead, galatea.TimelineHead);
        Assert.Equal(cli.Descriptor, galatea.Descriptor);
        Assert.Equal(cli.StoreItems.Count, galatea.StoreItems.Count);
        for (int index = 0; index < cli.StoreItems.Count; index++) {
            Assert.Equal(cli.StoreItems[index].Kind,
                galatea.StoreItems[index].Kind);
            Assert.Equal(cli.StoreItems[index].Key,
                galatea.StoreItems[index].Key);
            Assert.Equal(cli.StoreItems[index].Canonical,
                galatea.StoreItems[index].Canonical);
            Assert.Equal(cli.StoreItems[index].FulfilledViewDigest,
                galatea.StoreItems[index].FulfilledViewDigest);
        }
        Assert.Equal(cli.Contributions, galatea.Contributions);
        Assert.True(cliFactory.Client.RecapDispatchCount > 0);
        Assert.Equal(
            cliFactory.Client.RecapDispatchCount,
            galateaFactory.Client.RecapDispatchCount);

        CompletionRequest cliRequest = Assert.Single(
            cliFactory.Client.AgentRequests);
        CompletionRequest galateaRequest = Assert.Single(
            galateaFactory.Client.AgentRequests);
        Assert.Equal(cliRequest.ModelId, galateaRequest.ModelId);
        Assert.Equal(cliRequest.MaxTokens, galateaRequest.MaxTokens);
        Assert.Equal(
            cliRequest.PromptPrefix.SystemPrompt,
            galateaRequest.PromptPrefix.SystemPrompt);
        Assert.Equal(
            SessionVisibleToolSetFingerprint.ComputeSha256(
                cliRequest.PromptPrefix.OutputContract.Tools
            ),
            SessionVisibleToolSetFingerprint.ComputeSha256(
                galateaRequest.PromptPrefix.OutputContract.Tools
            )
        );
        Assert.Equal(
            "recap_grid.control",
            Assert.Single(cliRequest.PromptPrefix.OutputContract.Tools).Name
        );
        Assert.Empty(cliRequest.TailMessages);
        Assert.Empty(galateaRequest.TailMessages);
        ObservationMessage cliTail = Assert.IsType<ObservationMessage>(
            Assert.Single(cliRequest.PromptPrefix.SharedContextMessages));
        ObservationMessage galateaTail = Assert.IsType<ObservationMessage>(
            Assert.Single(galateaRequest.PromptPrefix.SharedContextMessages));
        Assert.Equal(
            "same next clue",
            cliTail.Content);
        Assert.Equal(
            GalateaHostService.WrapUserMessageForEngine("same next clue"),
            galateaTail.Content);
    }

    [Theory]
    [InlineData(
        nameof(SessionJournalFailpoint.AfterRequestPreparedCommitted),
        true)]
    [InlineData(
        nameof(SessionJournalFailpoint.AfterCompletionAttemptStartedCommitted),
        false)]
    public async Task ActualServiceFrozenRecoveryNeverCreatesOnline(
        string failpointName,
        bool resumes
    ) {
        string path = NewPath();
        CompletionConnectionConfig connection = Connection();
        SessionJournalFailpoint failpoint = Enum.Parse<
            SessionJournalFailpoint>(failpointName);
        EventAddress recoveryHead = await CreateRecoveryBoundaryAsync(
            path, connection, failpoint);
        string derived = Path.Combine(path, "derived");
        Directory.CreateDirectory(derived);
        string sentinel = Path.Combine(derived, "candidate-sentinel.bin");
        File.WriteAllBytes(sentinel, [1, 3, 3, 7]);
        byte[] before = File.ReadAllBytes(sentinel);

        var candidateFactory = new TrackingFactory("recovered candidate");
        int routeLoads = 0;
        RecapGridCompletionHost completion = RecapGridCompletionHost.Create(
            () => {
                Interlocked.Increment(ref routeLoads);
                throw new InvalidOperationException(
                    "Frozen recovery must not load recap routes.");
            },
            Connections(connection),
            candidateFactory);
        var candidate = new GalateaRecapGridCandidateComposition(
            completion,
            RecapGridOnlineLimits.Production,
            _estimator);
        var legacyFactory = new RejectingFactory();
        await using var legacy = new CompletionConnectionRegistry(
            Connections(connection), legacyFactory);
        await using var service = new GalateaHostService(
            Config(path, connection),
            legacy,
            DisabledGalateaUserMessageNormalizer.Instance,
            candidate);
        UserSessionHost session = await service.GetSessionAsync(
            "alice", CancellationToken.None);
        GalateaLiveTurn turn = service.StartRecovery(
            session,
            new GalateaTurnOptions(
                connection.Id,
                GalateaTurnMode.Resume,
                RestartUncertainCompletion: false,
                ExpectedHead: recoveryHead));

        if (resumes) {
            await service.RunTurnAsync(session, turn, CancellationToken.None);
            Assert.Equal("completed", turn.Status);
            Assert.Equal(1, candidateFactory.CreateCallCount);
            Assert.Equal(1, candidateFactory.Client.DispatchCallCount);
            Assert.Equal(
                SessionExecutionPhase.Idle,
                session.Engine.InspectExecutionBoundary().Phase);
        }
        else {
            GalateaTurnException exception = await Assert.ThrowsAsync<
                GalateaTurnException>(() => service.RunTurnAsync(
                    session, turn, CancellationToken.None));
            Assert.Equal(
                "uncertain-completion-restart-required",
                exception.FailureReason);
            Assert.Equal(0, candidateFactory.CreateCallCount);
            Assert.Equal(0, candidateFactory.Client.DispatchCallCount);
            Assert.Equal(
                SessionExecutionPhase.AwaitingCompletion,
                session.Engine.InspectExecutionBoundary().Phase);
        }

        Assert.Equal(0, routeLoads);
        Assert.Equal(0, legacyFactory.CreateCallCount);
        Assert.Equal(before, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public async Task ToolBearingPreparedBindsFrozenProfileWithoutDerivedOpen() {
        string path = NewPath();
        CompletionConnectionConfig connection = Connection();
        RecapGridAgentControlProfile profile = AgentProfile();
        EventAddress prepared;
        var fixtureClient = new TrackingClient("prepared answer");
        CompletionDispatchIdentity dispatch =
            CompletionDispatchIdentityFactory.Create(
                connection,
                fixtureClient
            );
        using (SessionJournalEngine.Create(
                   path,
                   new SessionCreateOptions(
                       connection.ModelId,
                       "test system prompt",
                       connection.CompletionSurfaceId))) { }
        RecapGridAgentControlHandle agent;
        using (SessionJournalEngine bindingOwner =
               SessionJournalEngine.OpenReadOnly(path)) {
            agent = Assert.IsType<
                   RecapGridAgentControlOpenResult.Opened
               >(RecapGridAgentControlFactory.Bind(
                   bindingOwner.ReadView,
                   profile,
                   _estimator
               )).Handle;
        }
        using (agent) {
            var runtime = new SessionRuntime(
                fixtureClient,
                agent.ToolSession,
                new SessionCompletionTargetIdentity(
                    dispatch.ConnectionId,
                    dispatch.Kind,
                    dispatch.ConnectionFingerprint,
                    dispatch.RequestAdapterFingerprint
                ),
                ToolRuntimeIdentity: agent.RuntimeIdentity,
                ContextCandidateSource: new EmptyCandidateSource()
            );
            using SessionJournalEngine engine =
                SessionJournalEngine.OpenForTest(
                    path,
                    runtime,
                    new SessionJournalTestHooks(
                        SessionJournalFailpoint
                            .AfterRequestPreparedCommitted
                    )
                );
            _ = await Assert.ThrowsAsync<
                SessionJournalFailpointException>(() => engine.SendAsync(
                    "freeze tool profile",
                    CancellationToken.None
                ));
            prepared = engine.ReadCurrentHead()!.Value;
        }
        Assert.False(Directory.Exists(Path.Combine(path, "derived")));

        var candidateFactory = new TrackingFactory("recovered candidate");
        RecapGridCompletionHost completion = RecapGridCompletionHost.Create(
            static () => throw new InvalidOperationException(
                "Prepared recovery must not load routes."
            ),
            Connections(connection),
            candidateFactory,
            new RecapGridAgentControlProfileRegistry([profile])
        );
        var candidate = new GalateaRecapGridCandidateComposition(
            completion,
            profile.ProfileId,
            RecapGridOnlineLimits.Production,
            _estimator
        );
        await using var legacy = new CompletionConnectionRegistry(
            Connections(connection),
            new RejectingFactory()
        );
        await using var service = new GalateaHostService(
            Config(path, connection),
            legacy,
            DisabledGalateaUserMessageNormalizer.Instance,
            candidate
        );
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn turn = service.StartRecovery(
            session,
            new GalateaTurnOptions(
                connection.Id,
                GalateaTurnMode.Resume,
                ExpectedHead: prepared
            )
        );

        await service.RunTurnAsync(session, turn, CancellationToken.None);
        service.FinishTurn(session, turn);

        Assert.Equal("completed", turn.Status);
        Assert.Equal(1, candidateFactory.CreateCallCount);
        Assert.False(Directory.Exists(Path.Combine(path, "derived")));
    }

    [Fact]
    public async Task ActualServiceToolContinuationUsesFrozenProfileAndReceipt() {
        string path = NewPath();
        CompletionConnectionConfig connection = Connection();
        RecapGridAgentControlProfile profile = AgentProfile();
        using (SessionJournalEngine provisioner = SessionJournalEngine.Create(
                   path,
                   new SessionCreateOptions(
                       connection.ModelId,
                       "test system prompt",
                       connection.CompletionSurfaceId))) {
            ProvisionTimelineAndControl(provisioner);
        }
        EventAddress actionHead;
        RecapGridAgentControlHandle agent;
        using (SessionJournalEngine bindingOwner =
               SessionJournalEngine.OpenReadOnly(path)) {
            agent = Assert.IsType<RecapGridAgentControlOpenResult.Opened>(
                RecapGridAgentControlFactory.Bind(
                    bindingOwner.ReadView,
                    profile,
                    _estimator
                )
            ).Handle;
        }
        using (agent) {
            var fixtureClient = new ControlToolCallClient();
            CompletionDispatchIdentity identity =
                CompletionDispatchIdentityFactory.Create(
                    connection,
                    fixtureClient
                );
            var runtime = new SessionRuntime(
                fixtureClient,
                agent.ToolSession,
                new SessionCompletionTargetIdentity(
                    identity.ConnectionId,
                    identity.Kind,
                    identity.ConnectionFingerprint,
                    identity.RequestAdapterFingerprint
                ),
                ToolRuntimeIdentity: agent.RuntimeIdentity,
                ContextCandidateSource: new EmptyCandidateSource()
            );
            using SessionJournalEngine engine =
                SessionJournalEngine.OpenForTest(
                    path,
                    runtime,
                    new SessionJournalTestHooks(
                        SessionJournalFailpoint.AfterActionCommitted
                    )
                );
            _ = await Assert.ThrowsAsync<
                SessionJournalFailpointException>(() => engine.SendAsync(
                    engine.ReadCurrentHead()!.Value,
                    "provision from Galatea tool"
                ));
            actionHead = engine.ReadCurrentHead()!.Value;
        }

        var candidateFactory = new TrackingFactory("continued candidate");
        RecapGridCompletionHost completion = RecapGridCompletionHost.Create(
            static () => throw new InvalidOperationException(
                "Raw-only tool continuation must not load recap routes."
            ),
            Connections(connection),
            candidateFactory,
            new RecapGridAgentControlProfileRegistry([profile])
        );
        var candidate = new GalateaRecapGridCandidateComposition(
            completion,
            profile.ProfileId,
            RecapGridOnlineLimits.Production,
            _estimator
        );
        await using var legacy = new CompletionConnectionRegistry(
            Connections(connection),
            new RejectingFactory()
        );
        await using var service = new GalateaHostService(
            Config(path, connection),
            legacy,
            DisabledGalateaUserMessageNormalizer.Instance,
            candidate
        );
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn turn = service.StartRecovery(
            session,
            new GalateaTurnOptions(
                connection.Id,
                GalateaTurnMode.Resume,
                ExpectedHead: actionHead
            )
        );

        await service.RunTurnAsync(session, turn, CancellationToken.None);
        service.FinishTurn(session, turn);

        Assert.Equal("completed", turn.Status);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
        using RecapGridControlReaderHandle control = Assert.IsType<
            RecapGridControlReaderOpenResult.Opened
        >(RecapGridControlFactory.OpenReader(
            path,
            session.Engine.BranchRefId
        )).Handle;
        RecapGridControlSnapshot snapshot = Assert.IsType<
            RecapGridControlSnapshotResult.Available
        >(control.Reader.ReadSnapshot()).Snapshot;
        Assert.Equal(1, snapshot.Head.Generation);
        Assert.Single(snapshot.Families);
        Assert.Equal(2, snapshot.Definitions.Count);
    }

    [Fact]
    public async Task DisposeAggregatesNonFatalSessionAndCandidateFailures() {
        (GalateaHostService service, CompletionConnectionRegistry legacy) =
            await CreateDisposalFixtureAsync();
        int disposedSessions = 0;
        int disposedCandidate = 0;
        service.DisposeHooksForTest = new(
            AfterSessionDisposed: index => {
                disposedSessions++;
                if (index == 0) {
                    throw new IOException("session cleanup failed");
                }
            },
            AfterCandidateDisposed: () => {
                disposedCandidate++;
                throw new InvalidOperationException(
                    "candidate cleanup failed");
            });
        try {
            AggregateException failure = await Assert.ThrowsAsync<
                AggregateException>(async () =>
                    await service.DisposeAsync());
            Assert.Equal(2, disposedSessions);
            Assert.Equal(1, disposedCandidate);
            Assert.Collection(
                failure.InnerExceptions,
                static value => Assert.IsType<IOException>(value),
                static value => Assert.IsType<InvalidOperationException>(
                    value));
        }
        finally {
            service.DisposeHooksForTest = null;
            await service.DisposeAsync();
            await legacy.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeFatalFailureStopsBeforeRemainingCandidateCleanup() {
        (GalateaHostService service, CompletionConnectionRegistry legacy) =
            await CreateDisposalFixtureAsync();
        int disposedSessions = 0;
        int disposedCandidate = 0;
        service.DisposeHooksForTest = new(
            AfterSessionDisposed: _ => {
                disposedSessions++;
                throw new OutOfMemoryException("fatal cleanup failure");
            },
            AfterCandidateDisposed: () => disposedCandidate++);
        try {
            await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
                await service.DisposeAsync());
            Assert.Equal(1, disposedSessions);
            Assert.Equal(0, disposedCandidate);
        }
        finally {
            service.DisposeHooksForTest = null;
            await service.DisposeAsync();
            await legacy.DisposeAsync();
        }
    }

    private async Task<(GalateaHostService Service,
        CompletionConnectionRegistry Legacy)> CreateDisposalFixtureAsync() {
        string first = NewPath();
        string second = NewPath();
        using (SessionJournalEngine.Create(
                   first,
                   new SessionCreateOptions(
                       "model-a", "test system prompt",
                       "openai-chat/strict"))) { }
        using (SessionJournalEngine.Create(
                   second,
                   new SessionCreateOptions(
                       "model-a", "test system prompt",
                       "openai-chat/strict"))) { }
        CompletionConnectionConfig connection = Connection();
        var completion = RecapGridCompletionHost.Create(
            static () => throw new InvalidOperationException(
                "Disposal must not load routes."),
            Connections(connection),
            new RejectingFactory());
        var candidate = new GalateaRecapGridCandidateComposition(
            completion,
            RecapGridOnlineLimits.Production,
            _estimator);
        var legacy = new CompletionConnectionRegistry(
            Connections(connection), new RejectingFactory());
        var service = new GalateaHostService(
            new GalateaConfig(
                [
                    new GalateaUserConfig(
                        "alice", "pw", first, "test system prompt"),
                    new GalateaUserConfig(
                        "bob", "pw", second, "test system prompt")
                ],
                [connection],
                connection.Id),
            legacy,
            DisabledGalateaUserMessageNormalizer.Instance,
            candidate);
        _ = await service.GetSessionAsync("alice", CancellationToken.None);
        _ = await service.GetSessionAsync("bob", CancellationToken.None);
        return (service, legacy);
    }

    private async Task<EventAddress> CreateRecoveryBoundaryAsync(
        string path,
        CompletionConnectionConfig connection,
        SessionJournalFailpoint failpoint
    ) {
        var fixtureClient = new TrackingClient("unused");
        CompletionDispatchIdentity dispatch =
            CompletionDispatchIdentityFactory.Create(
                connection, fixtureClient);
        var runtime = new SessionRuntime(
            fixtureClient,
            CompletionTarget: new SessionCompletionTargetIdentity(
                dispatch.ConnectionId,
                dispatch.Kind,
                dispatch.ConnectionFingerprint,
                dispatch.RequestAdapterFingerprint),
            ContextCandidateSource: new EmptyCandidateSource());
        using SessionJournalEngine engine =
            SessionJournalEngine.CreateForTest(
                path,
                new SessionCreateOptions(
                    "model-a",
                    "test system prompt",
                    "openai-chat/strict"),
                runtime,
                new SessionJournalTestHooks(Failpoint: failpoint));
        SessionJournalFailpointException exception =
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync(
                    engine.ReadCurrentHead()!.Value,
                    GalateaHostService.WrapUserMessageForEngine(
                        "frozen fixture")));
        Assert.Equal(failpoint, exception.Failpoint);
        return engine.ReadCurrentHead()!.Value;
    }

    private void ProvisionTimelineAndControl(SessionJournalEngine writer) {
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                writer.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024),
                _estimator));
        Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(
                writer.Path,
                writer.BranchRefId,
                new RecapGridControlAdmission(
                    RecapGridControlPermission.Create,
                    Array.Empty<FamilyDefinitionDigest>(),
                    Array.Empty<string>(),
                    Array.Empty<ContextHeaderCarrier>(),
                    ["galatea."],
                    maximumBootstrapRows: 64,
                    maximumProjectedCalls: 1_024)));
    }

    private static RecapGridAgentControlProfile AgentProfile() {
        Assert.True(RecapGridAgentControlBuiltIns
            .TryCreateRegistrationBundle(
                RecapGridAgentControlBuiltIns.MysteryInvestigationV1,
                out RecapGridControlRegistrationBundle? builtIn
            ));
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [builtIn!.Families[0].Digest],
            builtIn.Definitions.Select(static value =>
                value.Capability.CapabilityFingerprint),
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1_024
        );
        return RecapGridAgentControlProfile.Create(
            "galatea-candidate-v1",
            admission
        );
    }

    private (FamilyDefinition, MaintainerDefinitionRevision, GridBuildRecipe)
        ProvisionActiveEmptyRecipe(SessionJournalEngine writer) {
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(writer.Path));
        TimelineHeadRef timeline = ReadTimelineHead(
            writer.Path, writer.BranchRefId);
        Assert.Null(timeline.HeadRowId);
        FamilyDefinition family = FamilyDefinition.Create(
            "Maintain the inquiry.",
            [new FamilyToolDefinition(
                "submit",
                "Submit the maintained content.",
                new FamilyObjectInputSchema([
                    new FamilyToolProperty(
                        "outcome",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.String,
                            orderedEnum: [
                                RecapCompletionProtocolV1.UpdatedOutcome,
                                RecapCompletionProtocolV1
                                    .KeepUnchangedOutcome
                            ]),
                        required: true),
                    new FamilyToolProperty(
                        "content",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.String,
                            nullable: true),
                        required: true)
                ]))],
            new FamilyOutputProtocol(
                RecapCompletionProtocolV1.OutputProtocolId,
                "submit",
                FamilyToolChoice.Required,
                allowParallel: false),
            new FamilyInputRenderingProtocol(
                RecapCompletionProtocolV1.InputProtocolId,
                RecapCompletionProtocolV1.PriorProjectionSchemaId,
                RecapCompletionProtocolV1
                    .HistorySegmentRenderingSchemaId));
        MaintainerDefinitionRevision definition =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("galatea.culprit"),
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System, "culprit"),
                new MaintainerCapabilitySpec(
                    RecapCompletionProtocolV1.RuntimeProtocolId,
                    MaintainerReadableScope
                        .FullPriorBuildTargetAndCurrentHistorySegmentV1),
                new MaintainerDeclarativeSpec(
                    "Who is the culprit?",
                    "Maintain the culprit hypothesis."),
                maxContentUtf8Bytes: 16 * 1024);
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timeline.TimelineId,
            bootstrapThroughRowId: null,
            BuildTarget.Create([new BuildTargetColumn(
                definition.LogicalColumnId,
                definition.Digest)]));
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [family.Digest],
            [definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["galatea."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1_024);
        using RecapGridControlHandle control = Assert.IsType<
            RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(
                writer.Path, writer.BranchRefId, admission)).Handle;
        ControlHeadRef head = Assert.IsType<
            RecapGridControlSnapshotResult.Available>(
            control.Reader.ReadSnapshot()).Snapshot.Head;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            control.Coordinator.PutFamilyDefinition(head, family)).Head;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            control.Coordinator.PutMaintainerDefinition(
                head, definition)).Head;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            control.Coordinator.PutBuildRecipe(
                head, timeline, recipe, bootstrapWitness: null)).Head;
        Assert.IsType<RecapGridControlActivateResult.Applied>(
            control.Coordinator.CompareExchangeActiveRecipe(
                head,
                timeline,
                recipe.Digest,
                RecapGridControlActivationPurpose.Direct));
        return (family, definition, recipe);
    }

    private (FamilyDefinition, MaintainerDefinitionRevision, GridBuildRecipe)
        ProvisionActiveCurrentRecipe(SessionJournalEngine writer) {
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(writer.Path));
        TimelineHeadRef timeline = ReadTimelineHead(
            writer.Path, writer.BranchRefId);
        using HistoryTimelineReaderHandle timelineReader = Assert.IsType<
            HistoryTimelineReaderOpenResult.Opened>(
            HistoryTimelineMaintenance.OpenReader(
                writer.Path, writer.BranchRefId)).Handle;
        HistoryTimelineSelectedRow selected = Assert.IsType<
            HistoryTimelineReaderRowResult.Selected>(
            timelineReader.Reader.ReadSelectedRow(
                timeline, timeline.HeadRowId!.Value)).Row;
        (FamilyDefinition family, MaintainerDefinitionRevision definition) =
            BuildGridValues();
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timeline.TimelineId,
            selected.Descriptor.RowId,
            BuildTarget.Create([new BuildTargetColumn(
                definition.LogicalColumnId,
                definition.Digest)]));
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [family.Digest],
            [definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["galatea."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1_024);
        using RecapGridControlHandle control = Assert.IsType<
            RecapGridControlOpenResult.Opened>(
            RecapGridControlFactory.Open(
                writer.Path, writer.BranchRefId, admission)).Handle;
        ControlHeadRef head = Assert.IsType<
            RecapGridControlSnapshotResult.Available>(
            control.Reader.ReadSnapshot()).Snapshot.Head;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            control.Coordinator.PutFamilyDefinition(head, family)).Head;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            control.Coordinator.PutMaintainerDefinition(
                head, definition)).Head;
        head = Assert.IsType<RecapGridControlPutResult.Stored>(
            control.Coordinator.PutBuildRecipe(
                head, timeline, recipe, selected.Witness)).Head;
        Assert.IsType<RecapGridControlActivateResult.Applied>(
            control.Coordinator.CompareExchangeActiveRecipe(
                head,
                timeline,
                recipe.Digest,
                RecapGridControlActivationPurpose.Direct));
        return (family, definition, recipe);
    }

    private static (FamilyDefinition, MaintainerDefinitionRevision)
        BuildGridValues() {
        FamilyDefinition family = FamilyDefinition.Create(
            "Maintain the inquiry.",
            [new FamilyToolDefinition(
                "submit",
                "Submit the maintained content.",
                new FamilyObjectInputSchema([
                    new FamilyToolProperty(
                        "outcome",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.String,
                            orderedEnum: [
                                RecapCompletionProtocolV1.UpdatedOutcome,
                                RecapCompletionProtocolV1
                                    .KeepUnchangedOutcome
                            ]),
                        required: true),
                    new FamilyToolProperty(
                        "content",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.String,
                            nullable: true),
                        required: true)
                ]))],
            new FamilyOutputProtocol(
                RecapCompletionProtocolV1.OutputProtocolId,
                "submit",
                FamilyToolChoice.Required,
                allowParallel: false),
            new FamilyInputRenderingProtocol(
                RecapCompletionProtocolV1.InputProtocolId,
                RecapCompletionProtocolV1.PriorProjectionSchemaId,
                RecapCompletionProtocolV1
                    .HistorySegmentRenderingSchemaId));
        MaintainerDefinitionRevision definition =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("galatea.culprit"),
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System, "culprit"),
                new MaintainerCapabilitySpec(
                    RecapCompletionProtocolV1.RuntimeProtocolId,
                    MaintainerReadableScope
                        .FullPriorBuildTargetAndCurrentHistorySegmentV1),
                new MaintainerDeclarativeSpec(
                    "Who is the culprit?",
                    "Maintain the culprit hypothesis."),
                maxContentUtf8Bytes: 16 * 1024);
        return (family, definition);
    }

    private static DerivedSnapshot ReadDerivedSnapshot(string path) {
        using SessionJournalEngine raw =
            SessionJournalEngine.OpenReadOnly(path);
        TimelineHeadRef timelineHead;
        HistoryTimelineSelectedRow selected;
        using (HistoryTimelineReaderHandle timeline = Assert.IsType<
                   HistoryTimelineReaderOpenResult.Opened>(
                   HistoryTimelineMaintenance.OpenReader(
                       path, raw.BranchRefId)).Handle) {
            timelineHead = Assert.IsType<
                HistoryTimelineSnapshotResult.Available>(
                timeline.Reader.ReadSnapshot()).Head;
            selected = Assert.IsType<
                HistoryTimelineReaderRowResult.Selected>(
                timeline.Reader.ReadSelectedRow(
                    timelineHead,
                    timelineHead.HeadRowId!.Value)).Row;
        }
        List<RecapGridStoreExportItem> items = [];
        RecapGridStoreExportCursor? cursor = null;
        do {
            RecapGridStoreExportResult.Page page = Assert.IsType<
                RecapGridStoreExportResult.Page>(
                RecapGridStoreMaintenance.Export(
                    path, cursor, includeContent: true));
            items.AddRange(page.Value.Items);
            cursor = page.Value.NextCursor;
        } while (cursor is not null);
        using RecapGridContextHandle getter = Assert.IsType<
            RecapGridContextOpenResult.Opened>(
            RecapGridContextFactory.Open(raw.ReadView)).Handle;
        RecapGridContextSelection selection = Assert.IsType<
            RecapGridContextResolveResult.Selected>(
            getter.Resolve(raw.ReadCurrentHead()!.Value, 0)).Selection;
        RecapGridContextMaterializeResult.Available materialized =
            Assert.IsType<RecapGridContextMaterializeResult.Available>(
                getter.Materialize(selection));
        string[] contributions = [.. materialized.Candidate.Contributions
            .Select(static value => string.Join(
                "|",
                value.Target.Carrier,
                value.Target.BlockKey,
                value.ExactText,
                value.AbsorbedThrough.ToString(),
                value.ContentSha256))];
        return new DerivedSnapshot(
            timelineHead.ToCanonicalBytes(),
            selected.Descriptor.ToCanonicalBytes(),
            items,
            contributions);
    }

    private static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories)) {
            Directory.CreateDirectory(Path.Combine(
                destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories)) {
            File.Copy(file, Path.Combine(
                destination, Path.GetRelativePath(source, file)));
        }
    }

    private TimelineHeadRef ReadTimelineHead(string path, RefId refId) {
        using HistoryTimelineReaderHandle timeline = Assert.IsType<
            HistoryTimelineReaderOpenResult.Opened>(
            HistoryTimelineMaintenance.OpenReader(path, refId)).Handle;
        return Assert.IsType<HistoryTimelineSnapshotResult.Available>(
            timeline.Reader.ReadSnapshot()).Head;
    }

    private static async Task RunFreshAsync(
        GalateaHostService service,
        UserSessionHost session,
        string connectionId,
        string message
    ) {
        GalateaLiveTurn turn = service.StartTurn(
            session, message, new GalateaTurnOptions(connectionId));
        await service.RunTurnAsync(session, turn, CancellationToken.None);
        service.FinishTurn(session, turn);
        Assert.Equal("completed", turn.Status);
    }

    private static CompletionConnectionConfig Connection() => new(
        "test",
        "openai-chat",
        "model-a",
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key");

    private static CompletionConnectionsFileConfig Connections(
        CompletionConnectionConfig connection
    ) => CompletionConnectionConfigLoader.NormalizeAndValidate(new(
        [connection],
        connection.Id));

    private static GalateaConfig Config(
        string path,
        CompletionConnectionConfig connection
    ) => new(
        [new GalateaUserConfig(
            "alice", "pw", path, "test system prompt")],
        [connection],
        connection.Id);

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-galatea-recap-grid-candidate-tests",
            Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private sealed class EmptyCandidateSource
        : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(new SessionContextCandidateSelection(
            SessionContextCandidateSelectionStatus.EmptyLineage,
            Candidate: null));

        public ValueTask<SessionContextCandidateMaterializationResult>
            MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "An empty fixture must not materialize.");
    }

    private sealed class RejectingBatchExecutor : IRecapCellBatchExecutor {
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "No recipe is active while sealing the base fixture.");
    }

    private sealed class RejectingFactory : ICompletionClientFactory {
        private int _createCallCount;
        internal int CreateCallCount => Volatile.Read(ref _createCallCount);

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Interlocked.Increment(ref _createCallCount);
            throw new InvalidOperationException(
                "The legacy Galatea registry must stay unused.");
        }
    }

    private sealed class TrackingFactory(string responseText)
        : ICompletionClientFactory {
        private int _createCallCount;
        internal TrackingClient Client { get; } = new(responseText);
        internal int CreateCallCount => Volatile.Read(ref _createCallCount);

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Interlocked.Increment(ref _createCallCount);
            return Client;
        }
    }

    private sealed class TrackingClient(string responseText)
        : ICompletionClient {
        private readonly object _requestGate = new();
        private readonly List<CompletionRequest> _agentRequests = [];
        private int _dispatchCallCount;
        private int _agentDispatchCount;
        private int _recapDispatchCount;
        public string Name => "galatea-recap-grid-candidate-test";
        public string ApiSpecId => "openai-chat-v1";
        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount);
        internal int AgentDispatchCount => Volatile.Read(
            ref _agentDispatchCount);
        internal int RecapDispatchCount => Volatile.Read(
            ref _recapDispatchCount);
        internal IReadOnlyList<CompletionRequest> AgentRequests {
            get {
                lock (_requestGate) {
                    return _agentRequests.ToArray();
                }
            }
        }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            bool recap = request.PromptPrefix.OutputContract.Tools.Any(
                static tool => string.Equals(
                    tool.Name,
                    "submit",
                    StringComparison.Ordinal
                )
            );
            if (recap) {
                Interlocked.Increment(ref _recapDispatchCount);
            }
            else {
                Interlocked.Increment(ref _agentDispatchCount);
                lock (_requestGate) {
                    _agentRequests.Add(request);
                }
                observer?.OnTextDelta(responseText);
            }
            ActionMessage action = recap
                ? new ActionMessage([new ActionBlock.ToolCall(
                    new RawToolCall(
                        "submit",
                        "recap-call",
                        "{\"outcome\":\"updated\","
                        + "\"content\":\"原来如此，那些疑点就都对得上了。\"}"))])
                : new ActionMessage([new ActionBlock.Text(responseText)]);
            return Task.FromResult(new CompletionResult(
                action,
                new CompletionDescriptor(
                    Name, ApiSpecId, request.ModelId)));
        }
    }

    private sealed class ControlToolCallClient : ICompletionClient {
        public string Name => "galatea-recap-grid-candidate-test";
        public string ApiSpecId => "openai-chat-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new CompletionResult(
            new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                "recap_grid.control",
                "control-call",
                "{\"action\":\"provision-built-in\","
                + "\"builtInAssetId\":\"mystery-investigation-v1\"}"
            ))]),
            new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
        ));
    }

    private sealed record DerivedSnapshot(
        byte[] TimelineHead,
        byte[] Descriptor,
        IReadOnlyList<RecapGridStoreExportItem> StoreItems,
        IReadOnlyList<string> Contributions
    );
}
