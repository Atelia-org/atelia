using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Online;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed partial class GalateaRecapGridCompositionTests : IDisposable {
    private static readonly TimeSpan HttpCompletionDeadline =
        TimeSpan.FromSeconds(10);
    private const string FrozenRecoverySystemPrompt =
        "frozen old prompt";
    private const string CurrentFinalizedSystemPrompt =
        "current Galatea prompt";
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();
    private static GalateaRecapGridDefaultPolicy DefaultExpectation =>
        GalateaRecapGridDefaultPolicy.ForCharacter(
            new GalateaCharacterName("Galatea")
        );

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
            candidateFactory,
            inputProjector: GalateaInputProjector.Instance);
        var candidate = new GalateaRecapGridComposition(
            completion,
            RecapGridOnlineLimits.Production,
            _estimator);
        var clock = new CountingTimeProvider(
            new DateTimeOffset(
                2026,
                8,
                29,
                6,
                23,
                5,
                987,
                TimeSpan.Zero
            ),
            TimeZoneInfo.CreateCustomTimeZone(
                "galatea-test-utc-plus-8",
                TimeSpan.FromHours(8),
                "galatea-test-utc-plus-8",
                "galatea-test-utc-plus-8"
            )
        );
        await using var service = new GalateaHostService(
            Config(path, connection),
            DisabledGalateaUserMessageNormalizer.Instance,
            candidate,
            timeProvider: clock);
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice", CancellationToken.None);

        GalateaLiveTurn fresh = service.StartTurn(
            session,
            "fresh candidate",
            new GalateaTurnOptions(connection.Id),
            GalateaDelegateTestConfiguration.PlayerSender);
        await service.RunTurnAsync(session, fresh, CancellationToken.None);
        service.FinishTurn(session, fresh);

        Assert.Equal("completed", fresh.Status);
        Assert.Equal(1, candidateFactory.CreateCallCount);
        Assert.Equal(1, candidateFactory.Client.DispatchCallCount);
        Assert.Equal(0, routeLoads);
        Assert.False(File.Exists(Path.Combine(
            path, "derived", "recap-grid", "v1", "grid.sqlite")));
        var completed = Assert.Single(
            session.Engine.ReadRecentCompletedTurns()
                .RequireSnapshot().Turns
        );
        PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(completed.ObservationContent);
        Assert.Equal(
            new DateTimeOffset(
                2026,
                8,
                29,
                14,
                23,
                5,
                TimeSpan.FromHours(8)
            ),
            observation.ExternalLocalTimestamp
        );
        Assert.Equal(1, clock.GetUtcNowCallCount);

        EventAddress observationHead = session.Engine.AppendObservation(
            GalateaHostService.WrapUserMessageForEngine(
                "observation accepted candidate",
                DateTimeOffset.UnixEpoch));
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
        Assert.Equal(0, routeLoads);
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase);
        Assert.Equal(2, session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns.Count);
        Assert.Equal(1, clock.GetUtcNowCallCount);
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
                            RecapRewriterProtocolV3.RuntimeProtocolId,
                            null),
                        connection.Id,
                        maximumConcurrency: 1,
                        dispatchTimeout: TimeSpan.FromSeconds(30))
                ]);
            },
            Connections(connection),
            candidateFactory,
            inputProjector: GalateaInputProjector.Instance);
        var candidate = new GalateaRecapGridComposition(
            completion,
            RecapGridOnlineLimits.Production,
            _estimator);
        await using var service = new GalateaHostService(
            Config(path, connection),
            DisabledGalateaUserMessageNormalizer.Instance,
            candidate,
            DefaultPolicies(
                GalateaRecapGridDefaultPolicy.ForTarget(recipe.Target)
            ));
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice", CancellationToken.None);

        await RunFreshAsync(service, session, connection.Id, "first clue");
        Assert.Equal(1, candidateFactory.Client.AgentDispatchCount);
        Assert.Equal(0, candidateFactory.Client.RecapDispatchCount);

        await RunFreshAsync(service, session, connection.Id, "second clue");

        Assert.Equal(2, candidateFactory.Client.AgentDispatchCount);
        Assert.True(candidateFactory.Client.RecapDispatchCount > 0);
        Assert.Equal(1, routeLoads);
        Assert.True(File.Exists(Path.Combine(
            path, "derived", "recap-grid", "v1", "grid.sqlite")));
        int providerCallsBeforeReadiness =
            candidateFactory.Client.DispatchCallCount;
        IReadOnlyDictionary<string, string> readyBytes = SnapshotDomainFiles(
            path
        );
        EventAddress rawHead = session.Engine.ReadCurrentHead()!.Value;
        RecapGridReadinessSnapshotDto ready =
            GalateaRecapGridReadiness.Inspect(
                session.Engine.ReadView,
                rawHead,
                session.DefaultPolicy,
                CancellationToken.None
            );
        Assert.Equal("exact", ready.Freshness);
        Assert.Equal("ready", ready.State);
        Assert.NotNull(ready.Authority);
        Assert.Equal(readyBytes, SnapshotDomainFiles(path));
        Assert.Equal(
            providerCallsBeforeReadiness,
            candidateFactory.Client.DispatchCallCount
        );
        await using (GalateaRecapGridTurn settleCurrentRawTail = await candidate
            .OpenFreshAsync(
                session.Engine,
                connection.Id,
                pendingObservation: "settle current raw tail",
                defaultPolicy: session.DefaultPolicy,
                CancellationToken.None)) { }
        int providerCallsBeforeFulfilledProbe =
            candidateFactory.Client.DispatchCallCount;
        await using (GalateaRecapGridTurn fulfilledProbe = await candidate
            .OpenFreshAsync(
                session.Engine,
                connection.Id,
                pendingObservation: "provider-free fulfilled probe",
                defaultPolicy: session.DefaultPolicy,
                CancellationToken.None)) {
            RecapGridOnlineMaintenanceEvidence probeEvidence =
                Assert.IsType<RecapGridOnlineMaintenanceEvidence>(
                    fulfilledProbe.MaintenanceEvidence);
            Assert.Equal(0, probeEvidence.NewCalls);
            Assert.Equal(0, probeEvidence.CellsCommitted);
            Assert.Equal(0, probeEvidence.RecipeRowSteps);
            Assert.Equal(0, probeEvidence.RowViewsCommitted);
            Assert.Null(probeEvidence.LastAttemptedRecipeRow);
            Assert.Null(probeEvidence.NextRecipeRow);
            Assert.Equal(RecapGridOnlineContinuationKind.Ready,
                probeEvidence.ContinuationKind);
        }
        Assert.Equal(
            providerCallsBeforeFulfilledProbe,
            candidateFactory.Client.DispatchCallCount);

        TimelineHeadRef probeHead = ReadTimelineHead(
            path, session.Engine.BranchRefId);
        int providerCallsBeforeBootstrap =
            candidateFactory.Client.DispatchCallCount;

        UpdateMinimumRecentHistoryLoad(
            session.Engine,
            minimumRecentHistoryLoad: 1_000_000);
        IReadOnlyDictionary<string, string> bootstrapBytes =
            SnapshotDomainFiles(path);
        RecapGridReadinessSnapshotDto bootstrap =
            GalateaRecapGridReadiness.Inspect(
                session.Engine.ReadView,
                rawHead,
                session.DefaultPolicy,
                CancellationToken.None);
        Assert.Equal("exact", bootstrap.Freshness);
        Assert.Equal("reserve-bootstrap-raw-only", bootstrap.State);
        Assert.Null(bootstrap.Authority);
        RecapGridReserveBootstrapEvidenceDto evidence = Assert.IsType<
            RecapGridReserveBootstrapEvidenceDto>(
                bootstrap.ReserveBootstrap);
        Assert.Equal(probeHead.RefId.ToString(), evidence.RefId);
        Assert.Equal(probeHead.TimelineId.Value, evidence.TimelineId);
        Assert.Equal(probeHead.Generation, evidence.TimelineGeneration);
        Assert.Equal(
            probeHead.HeadRowId?.Value,
            evidence.TimelineHeadRowId);
        Assert.True(
            evidence.RetainedHistoryLoad < evidence.RequiredHistoryLoad);
        Assert.True(evidence.VerifiedRows > 0);
        Assert.True(evidence.Metrics.ExaminedTimelineRows > 0);
        Assert.Equal(bootstrapBytes, SnapshotDomainFiles(path));
        Assert.Equal(
            providerCallsBeforeBootstrap,
            candidateFactory.Client.DispatchCallCount);
        UpdateMinimumRecentHistoryLoad(
            session.Engine,
            minimumRecentHistoryLoad: 1);

        RemoveFulfillmentForTest(path);
        IReadOnlyDictionary<string, string> missingFulfillmentBytes =
            SnapshotDomainFiles(path);
        RecapGridReadinessSnapshotDto fulfillmentMissing =
            GalateaRecapGridReadiness.Inspect(
                session.Engine.ReadView,
                rawHead,
                session.DefaultPolicy,
                CancellationToken.None
            );
        Assert.Equal("exact", fulfillmentMissing.Freshness);
        Assert.Equal("fulfillment-missing", fulfillmentMissing.State);
        Assert.Equal(
            missingFulfillmentBytes,
            SnapshotDomainFiles(path)
        );
        Assert.Equal(
            providerCallsBeforeBootstrap,
            candidateFactory.Client.DispatchCallCount
        );

        RecapGridStorePhysicalWitness resetWitness = Assert.IsType<
            RecapGridStorePrepareResetResult.Prepared
        >(RecapGridStoreMaintenance.PrepareReset(path)).Witness;
        Assert.IsType<RecapGridStoreResetResult.Reset>(
            RecapGridStoreMaintenance.Reset(path, resetWitness)
        );
        IReadOnlyDictionary<string, string> frontierBytes =
            SnapshotDomainFiles(path);
        RecapGridReadinessSnapshotDto frontier =
            GalateaRecapGridReadiness.Inspect(
                session.Engine.ReadView,
                rawHead,
                session.DefaultPolicy,
                CancellationToken.None
            );
        Assert.Equal("exact", frontier.Freshness);
        Assert.Equal("frontier", frontier.State);
        Assert.NotEmpty(frontier.OrderedMissing!);
        Assert.Equal(frontierBytes, SnapshotDomainFiles(path));
        Assert.Equal(
            providerCallsBeforeBootstrap,
            candidateFactory.Client.DispatchCallCount
        );
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
        GridBuildRecipe targetRecipe;
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
            (family, _, targetRecipe) = ProvisionActiveCurrentRecipe(writer);
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
            {"v":2,"connections":[{"id":"test","kind":"openai-chat","modelId":"model-a","completionSurfaceId":"openai-chat/strict","baseAddress":"http://localhost:8000/","apiKey":"test-key"}],"defaultConnectionId":"test"}
            """);
        string routesPath = Path.Combine(external, "routes.json");
        File.WriteAllBytes(
            routesPath,
            RecapGridRouteManifest.Create([
                new RecapGridRouteManifestEntry(
                    new RecapCompletionRouteKey(
                        family.Digest,
                        RecapRewriterProtocolV3.RuntimeProtocolId,
                        null),
                    connection.Id,
                    maximumConcurrency: 1,
                    dispatchTimeout: TimeSpan.FromSeconds(30))
            ]).ToCanonicalBytes());
        string producerTargetPath = Path.Combine(
            external, "producer-target.canonical");
        File.WriteAllBytes(
            producerTargetPath,
            targetRecipe.Target.ToCanonicalBytes());
        string refId;
        using (SessionJournalEngine reader =
               SessionJournalEngine.OpenReadOnly(cliPath)) {
            refId = reader.BranchRefId.ToHexString();
        }
        var cliFactory = new TrackingFactory("same agent answer");
        Assert.Equal(0, Atelia.SessionJournal.Cli.Program.MainCore(
            [
                "run-online-turn",
                "--input", cliPath,
                "--branch", SessionJournalDefaults.MainBranchName,
                "--confirm-ref", refId,
                "--message", "same next clue",
                "--connection", connection.Id,
                "--connections", connectionsPath,
                "--routes", routesPath,
                "--producer-target", producerTargetPath
            ],
            cliFactory));

        var galateaFactory = new TrackingFactory("same agent answer");
        RecapGridCompletionHost completion = RecapGridCompletionHost.Create(
            () => RecapGridRouteManifest.DecodeCanonical(
                File.ReadAllBytes(routesPath)),
            Connections(connection),
            galateaFactory,
            inputProjector: GalateaInputProjector.Instance);
        var candidate = new GalateaRecapGridComposition(
            completion,
            RecapGridOnlineLimits.Production,
            _estimator);
        await using (var service = new GalateaHostService(
                         Config(galateaPath, connection),
                         DisabledGalateaUserMessageNormalizer.Instance,
                         candidate,
                         DefaultPolicies(
                             GalateaRecapGridDefaultPolicy.ForTarget(
                                 targetRecipe.Target
                             )
                         ))) {
            CharacterSessionHost session = await service.GetSessionAsync(
                "alice", CancellationToken.None);
            await RunFreshAsync(
                service, session, connection.Id, "same next clue");
        }

        DerivedSnapshot cli = ReadDerivedSnapshot(cliPath);
        DerivedSnapshot galatea = ReadDerivedSnapshot(galateaPath);
        Assert.Equal(cli.TimelineHead, galatea.TimelineHead);
        Assert.Equal(cli.Descriptor, galatea.Descriptor);
        // Separate stores allocate independent result IDs. Compare the complete
        // business graph (including membership and predecessor coordinates).
        Assert.Equal(cli.StoreBusinessRecords, galatea.StoreBusinessRecords);
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
        Assert.Empty(cliRequest.PromptPrefix.OutputContract.Tools);
        Assert.Empty(galateaRequest.PromptPrefix.OutputContract.Tools);
        Assert.Empty(cliRequest.TailMessages);
        Assert.Empty(galateaRequest.TailMessages);
        Assert.Equal(
            cliRequest.PromptPrefix.SharedContextMessages.Count(),
            galateaRequest.PromptPrefix.SharedContextMessages.Count()
        );
        Assert.Equal(2, cliRequest.PromptPrefix.SharedContextMessages.Count());
        Assert.Equal(
            Assert.IsType<ActionMessage>(
                cliRequest.PromptPrefix.SharedContextMessages[0]
            ).GetFlattenedText(),
            Assert.IsType<ActionMessage>(
                galateaRequest.PromptPrefix.SharedContextMessages[0]
            ).GetFlattenedText()
        );
        ObservationMessage cliTail = Assert.IsType<ObservationMessage>(
            cliRequest.PromptPrefix.SharedContextMessages[^1]);
        ObservationMessage galateaTail = Assert.IsType<ObservationMessage>(
            galateaRequest.PromptPrefix.SharedContextMessages[^1]);
        Assert.Equal(
            "same next clue",
            cliTail.Content);
        JsonElement value = Atelia.MdJson.MdJsonSerializer.Read(Assert.IsType<string>(galateaTail.Content));
        PlayerTurnObservation playerTurnObservation = GalateaObservationContent.ReadPlayerTurn(
            SessionInputContent.Structured(GalateaObservationContent.V1SchemaId, value));
        Assert.Equal("same next clue", playerTurnObservation.PlayerText);
        Assert.NotNull(playerTurnObservation.ExternalLocalTimestamp);
    }

    [Fact]
    public async Task FrozenToolRuntimeFailsBeforeCompletionClientBinding() {
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            NewPath(),
            new SessionCreateOptions(
                "model-a", "test system prompt", "openai-chat/strict"));
        EventAddress head = engine.ReadCurrentHead()!.Value;
        var frozen = new SessionRuntimeRecoveryRequirements.FrozenCompletionRequired(
            head,
            SessionExecutionPhase.AwaitingAgentAction,
            SessionEventKind.CompletionRequestPrepared,
            new SessionCompletionTargetIdentity("unavailable", "test", "fingerprint"),
            "client", "api", new string('a', 64),
            new SessionToolRuntimeIdentity("old-tool", "implementation", "capability"),
            head);
        var factory = new RejectingFactory();
        await using var composition = new GalateaRecapGridComposition(
            RecapGridCompletionHost.Create(
                () => throw new InvalidOperationException("No route should load."),
                Connections(Connection()),
                factory),
            RecapGridOnlineLimits.Production,
            _estimator);

        GalateaTurnException failure = Assert.Throws<GalateaTurnException>(
            () => composition.BindPrepared(engine, frozen));
        Assert.Equal("tool-runtime-unsupported", failure.FailureReason);
        Assert.Equal(0, factory.CreateCallCount);
    }

    [Theory]
    [InlineData(nameof(SessionJournalFailpoint.AfterRequestPreparedCommitted))]
    [InlineData("LegacyStarted")]
    public async Task ActualServiceFrozenRecoveryNeverCreatesOnline(
        string failpointName
    ) {
        string path = NewPath();
        CompletionConnectionConfig connection = Connection();
        bool legacyStarted = failpointName == "LegacyStarted";
        SessionJournalFailpoint failpoint = SessionJournalFailpoint.AfterRequestPreparedCommitted;
        EventAddress recoveryHead = await CreateRecoveryBoundaryAsync(
            path, connection, failpoint, legacyStarted);
        using (SessionJournalEngine targetProvisioner =
               SessionJournalEngine.Open(path)) {
            ProvisionTimelineAndControl(targetProvisioner);
            _ = ProvisionActiveEmptyRecipe(targetProvisioner);
        }
        string derived = Path.Combine(path, "derived");
        Directory.CreateDirectory(derived);
        string sentinel = Path.Combine(derived, "recap-grid-sentinel.bin");
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
            candidateFactory,
            inputProjector: GalateaInputProjector.Instance);
        var candidate = new GalateaRecapGridComposition(
            completion,
            RecapGridOnlineLimits.Production,
            _estimator);
        await using var service = new GalateaHostService(
            Config(path, connection, CurrentFinalizedSystemPrompt),
            DisabledGalateaUserMessageNormalizer.Instance,
            candidate);
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice", CancellationToken.None);
        Assert.Equal(
            FrozenRecoverySystemPrompt,
            session.Engine.ResolveGoverningSetup(recoveryHead).SystemPrompt.TextValue);
        int setupCountBeforeRecovery = CountSystemPromptSetups(
            session.Engine);
        Assert.Equal(1, setupCountBeforeRecovery);
        GalateaLiveTurn turn = service.StartRecovery(
            session,
            new GalateaTurnOptions(
                connection.Id,
                GalateaTurnMode.Resume,
                ExpectedHead: recoveryHead));

        {
            await service.RunTurnAsync(session, turn, CancellationToken.None);
            Assert.Equal("completed", turn.Status);
            Assert.Equal(1, candidateFactory.CreateCallCount);
            Assert.Equal(1, candidateFactory.Client.DispatchCallCount);
            CompletionRequest request = Assert.Single(
                candidateFactory.Client.AgentRequests);
            Assert.Equal(
                FrozenRecoverySystemPrompt,
                request.PromptPrefix.SystemPrompt);
            Assert.Equal(
                SessionExecutionPhase.Idle,
                session.Engine.InspectExecutionBoundary().Phase);
        }

        Assert.Equal(
            setupCountBeforeRecovery,
            CountSystemPromptSetups(session.Engine));
        EventAddress currentHead = Assert.IsType<EventAddress>(
            session.Engine.ReadCurrentHead());
        Assert.Equal(
            FrozenRecoverySystemPrompt,
            session.Engine.ResolveGoverningSetup(currentHead).SystemPrompt.TextValue);
        Assert.Equal(0, routeLoads);
        Assert.Equal(before, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public async Task CancelledFreshBindingDisposesLocalOnlineBeforeClientAndCanReopen() {
        string path = NewPath();
        CompletionConnectionConfig connection = Connection();
        using (SessionJournalEngine provisioner = SessionJournalEngine.Create(
                   path,
                   new SessionCreateOptions(
                       "model-a", "test system prompt", "openai-chat/strict"))) {
            ProvisionTimelineAndControl(provisioner);
        }
        var factory = new TrackingFactory("candidate answer");
        var candidate = new GalateaRecapGridComposition(
            RecapGridCompletionHost.Create(
                static () => throw new InvalidOperationException(
                    "Raw-only maintenance must not load routes."),
                Connections(connection),
                factory,
            inputProjector: GalateaInputProjector.Instance),
            RecapGridOnlineLimits.Production,
            _estimator);
        await using (candidate) {
            using SessionJournalEngine engine = SessionJournalEngine.Open(path);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                candidate.OpenFreshAsync(
                    engine,
                    connection.Id,
                    pendingObservation: "pending",
                    defaultPolicy: DefaultExpectation,
                    cancelled.Token).AsTask());
            Assert.Equal(0, factory.CreateCallCount);

            await using GalateaRecapGridTurn reopened = await candidate
                .OpenFreshAsync(
                    engine,
                    connection.Id,
                    pendingObservation: "pending",
                    defaultPolicy: DefaultExpectation,
                    CancellationToken.None);
            Assert.Equal(1, factory.CreateCallCount);
            Assert.Equal(0, factory.Client.DispatchCallCount);
            Assert.NotNull(reopened.MaintenanceEvidence);
        }
    }

    [Fact]
    public async Task DisposeAggregatesNonFatalSessionAndCandidateFailures() {
        GalateaHostService service = await CreateDisposalFixtureAsync();
        int disposedSessions = 0;
        int disposedCandidate = 0;
        var disposalOrder = new List<string>();
        service.DisposeHooksForTest = new(
            AfterSessionDisposed: index => {
                disposedSessions++;
                if (index == 0) {
                    throw new IOException("session cleanup failed");
                }
            },
            AfterRecapGridDisposed: () => {
                disposedCandidate++;
                disposalOrder.Add("completion");
                throw new InvalidOperationException(
                    "candidate cleanup failed");
            },
            AfterDelegationSupervisorDisposed: () =>
                disposalOrder.Add("sidecar"));
        Task disposal = service.DisposeAsync().AsTask();
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => disposal);
        Assert.Equal(2, disposedSessions);
        Assert.Equal(1, disposedCandidate);
        Assert.Equal(["sidecar", "completion"], disposalOrder);
        Assert.Collection(
            failure.InnerExceptions,
            static value => Assert.IsType<IOException>(value),
            static value => Assert.IsType<InvalidOperationException>(value));
        service.DisposeHooksForTest = null;
        Task repeated = service.DisposeAsync().AsTask();
        Assert.Same(disposal, repeated);
        Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => repeated));
        Assert.Equal(2, disposedSessions);
        Assert.Equal(1, disposedCandidate);
    }

    [Fact]
    public async Task DisposeFatalFailureCompletesRemainingCleanupAndPreservesCause() {
        GalateaHostService service = await CreateDisposalFixtureAsync();
        int disposedSessions = 0;
        int disposedCandidate = 0;
        var fatal = new OutOfMemoryException("fatal cleanup failure");
        service.DisposeHooksForTest = new(
            AfterSessionDisposed: _ => {
                disposedSessions++;
                throw fatal;
            },
            AfterRecapGridDisposed: () => disposedCandidate++);
        Task disposal = service.DisposeAsync().AsTask();
        Assert.Same(fatal, await Assert.ThrowsAsync<OutOfMemoryException>(() => disposal));
        Assert.Equal(2, disposedSessions);
        Assert.Equal(1, disposedCandidate);
        service.DisposeHooksForTest = null;
        Task repeated = service.DisposeAsync().AsTask();
        Assert.Same(disposal, repeated);
        Assert.Same(fatal, await Assert.ThrowsAsync<OutOfMemoryException>(() => repeated));
        Assert.Equal(2, disposedSessions);
        Assert.Equal(1, disposedCandidate);
    }

    private async Task<GalateaHostService> CreateDisposalFixtureAsync() {
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
            new RejectingFactory(),
            inputProjector: GalateaInputProjector.Instance);
        var candidate = new GalateaRecapGridComposition(
            completion,
            RecapGridOnlineLimits.Production,
            _estimator);
        var service = new GalateaHostService(
            new GalateaConfig(
                [
                    new GalateaCharacterConfig(
                        "alice",
                        new GalateaCharacterName("Galatea"),
                        first,
                        first + "-delegation-state",
                        first + "-character-memory-state",
                        GalateaDelegateTestConfiguration.CreateHomeDirectory(first, "alice"),
                        GalateaSessionProvisioning.ExistingOnly,
                        "test system prompt",
                        connection.Id),
                    new GalateaCharacterConfig(
                        "bob",
                        new GalateaCharacterName("Galatea-bob"),
                        second,
                        second + "-delegation-state",
                        second + "-character-memory-state",
                        GalateaDelegateTestConfiguration.CreateHomeDirectory(second, "bob"),
                        GalateaSessionProvisioning.ExistingOnly,
                        "test system prompt",
                        connection.Id)
                ],
                GalateaDelegateTestConfiguration.Players,
                [connection],
                [connection.Id],
                InputNormalizerConnectionId: null,
                Delegates: GalateaDelegateTestConfiguration.Create()),
            DisabledGalateaUserMessageNormalizer.Instance,
            candidate);
        _ = await service.GetSessionAsync("alice", CancellationToken.None);
        _ = await service.GetSessionAsync("bob", CancellationToken.None);
        return service;
    }

    private async Task<EventAddress> CreateRecoveryBoundaryAsync(
        string path,
        CompletionConnectionConfig connection,
        SessionJournalFailpoint failpoint,
        bool legacyStarted
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
                dispatch.ConnectionFingerprint),
            ContextCandidateSource: new EmptyCandidateSource());
        using SessionJournalEngine engine =
            SessionJournalEngine.CreateForTest(
                path,
                new SessionCreateOptions(
                    "model-a",
                    FrozenRecoverySystemPrompt,
                    "openai-chat/strict"),
                runtime,
                new SessionJournalTestHooks(Failpoint: failpoint));
        SessionJournalFailpointException exception =
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync(
                    engine.ReadCurrentHead()!.Value,
                    GalateaHostService.WrapUserMessageForEngine(
                        "frozen fixture",
                        DateTimeOffset.UnixEpoch)));
        Assert.Equal(failpoint, exception.Failpoint);
        EventAddress head = engine.ReadCurrentHead()!.Value;
        engine.Dispose();
        return legacyStarted ? LegacyPreparedV7Fixture.AppendStarted(path, head) : head;
    }

    private void ProvisionTimelineAndControl(SessionJournalEngine writer) {
        ProvisionTimeline(writer);
        ProvisionCadenceAndControl(writer);
    }

    private void ProvisionTimeline(SessionJournalEngine writer) {
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
    }

    private static void ProvisionCadenceAndControl(
        SessionJournalEngine writer
    ) {
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(
                writer,
                new RecapGridCadencePolicySpec(
                    minimumRecentHistoryLoad: 1,
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    targetHistoryLoad: 1,
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024
                )
            )
        );
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

    private static void UpdateMinimumRecentHistoryLoad(
        SessionJournalEngine writer,
        long minimumRecentHistoryLoad
    ) {
        using RecapGridCadenceHandle cadence = Assert.IsType<
            RecapGridCadenceOpenResult.Opened>(
                RecapGridCadenceFactory.OpenMutable(writer)).Handle;
        RecapGridCadenceSnapshot current = Assert.IsType<
            RecapGridCadenceReadResult.Available>(
                cadence.Reader.ReadSnapshot()).Snapshot;
        RecapGridCadencePolicySpec policy = current.Policy;
        Assert.IsType<RecapGridCadenceCompareExchangeResult.Updated>(
            cadence.Coordinator.CompareExchangePolicy(
                current.Head,
                new RecapGridCadencePolicySpec(
                    minimumRecentHistoryLoad,
                    policy.PartitionAlgorithmId,
                    policy.HistoryLoadEstimatorId,
                    policy.TargetHistoryLoad,
                    policy.MaxRawEvents,
                    policy.MaxRenderedBytes)));
    }

    private static RecapGridControlRegistrationBundle GalateaBundle(
        string characterName
    ) {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV7,
            new GalateaRecapGridAssetParameters(
                new GalateaCharacterName(characterName)
            ),
            out RecapGridControlRegistrationBundle? bundle
        ));
        return bundle!;
    }

    private static BuildTarget BundleTarget(
        RecapGridControlRegistrationBundle bundle
    ) => BuildTarget.Create(bundle.Definitions.Select(static definition =>
        new BuildTargetColumn(
            definition.LogicalColumnId,
            definition.Digest
        )
    ));

    private RegisteredTargetSet RegisterTargets(
        SessionJournalEngine writer,
        IReadOnlyList<FamilyDefinition> families,
        IReadOnlyList<MaintainerDefinitionRevision> definitions,
        params BuildTarget[] targets
    ) {
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            families.Select(static value => value.Digest).Distinct(),
            definitions.Select(static value =>
                value.Capability.CapabilityFingerprint).Distinct(),
            definitions.Select(static value => value.Target.Carrier)
                .Distinct(),
            definitions.Select(static value => value.LogicalColumnId.Value!)
                .Distinct(StringComparer.Ordinal),
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1_024
        );
        TimelineHeadRef timeline = ReadTimelineHead(
            writer.Path,
            writer.BranchRefId
        );
        using RecapGridControlHandle control = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            writer.Path,
            writer.BranchRefId,
            admission
        )).Handle;
        ControlHeadRef head = Assert.IsType<
            RecapGridControlSnapshotResult.Available
        >(control.Reader.ReadSnapshot()).Snapshot.Head;
        foreach (FamilyDefinition family in families.DistinctBy(
                     static value => value.Digest)) {
            head = PutHead(control.Coordinator.PutFamilyDefinition(
                head,
                family
            ));
        }
        foreach (MaintainerDefinitionRevision definition in
                 definitions.DistinctBy(static value => value.Digest)) {
            head = PutHead(control.Coordinator.PutMaintainerDefinition(
                head,
                definition
            ));
        }
        var recipes = new List<GridBuildRecipe>();
        foreach (BuildTarget target in targets) {
            GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
                timeline.TimelineId,
                bootstrapThroughRowId: null,
                target
            );
            head = PutHead(control.Coordinator.PutBuildRecipe(
                head,
                timeline,
                recipe,
                bootstrapWitness: null
            ));
            recipes.Add(recipe);
        }
        return new RegisteredTargetSet(admission, recipes, timeline);
    }

    private static ControlHeadRef ActivateTarget(
        SessionJournalEngine writer,
        RegisteredTargetSet registered,
        int recipeIndex
    ) {
        using RecapGridControlHandle control = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            writer.Path,
            writer.BranchRefId,
            registered.Admission
        )).Handle;
        ControlHeadRef head = Assert.IsType<
            RecapGridControlSnapshotResult.Available
        >(control.Reader.ReadSnapshot()).Snapshot.Head;
        RecapGridControlActivateResult result = control.Coordinator
            .CompareExchangeActiveRecipe(
                head,
                registered.Timeline,
                registered.Recipes[recipeIndex].Digest,
                RecapGridControlActivationPurpose.Direct
            );
        return result switch {
            RecapGridControlActivateResult.Applied value => value.Head,
            RecapGridControlActivateResult.AlreadyActive value => value.Head,
            _ => throw new Xunit.Sdk.XunitException(
                $"Unexpected activation result: {result.GetType().Name}"
            )
        };
    }

    private static ControlHeadRef PutHead(
        RecapGridControlPutResult result
    ) => result switch {
        RecapGridControlPutResult.Stored value => value.Head,
        RecapGridControlPutResult.AlreadyPresent value => value.Head,
        _ => throw new Xunit.Sdk.XunitException(
            $"Unexpected put result: {result.GetType().Name}"
        )
    };

    private sealed record RegisteredTargetSet(
        RecapGridControlAdmission Admission,
        IReadOnlyList<GridBuildRecipe> Recipes,
        TimelineHeadRef Timeline
    );

    internal static void ProvisionMismatchedTargetForTest(
        SessionJournalEngine writer
    ) => _ = ProvisionActiveEmptyRecipe(writer);

    private static (
        FamilyDefinition,
        MaintainerDefinitionRevision,
        GridBuildRecipe)
        ProvisionActiveEmptyRecipe(SessionJournalEngine writer) {
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(writer.Path));
        TimelineHeadRef timeline = ReadTimelineHead(
            writer.Path, writer.BranchRefId);
        Assert.Null(timeline.HeadRowId);
        FamilyDefinition family = FamilyDefinition.Create(
            "Maintain the inquiry.",
            [],
            RecapRewriterProtocolV3.CreateOutputProtocol(),
            new FamilyInputRenderingProtocol(
                RecapRewriterProtocolV3.InputProtocolId,
                RecapRewriterProtocolV3.PriorProjectionSchemaId,
                RecapRewriterProtocolV3
                    .HistorySegmentRenderingSchemaId));
        MaintainerDefinitionRevision definition =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("galatea.culprit"),
                family.Digest,
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.System,
                    "culprit",
                    "Derived context from prior history: culprit"
                ),
                new MaintainerCapabilitySpec(
                    RecapRewriterProtocolV3.RuntimeProtocolId,
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
            [],
            RecapRewriterProtocolV3.CreateOutputProtocol(),
            new FamilyInputRenderingProtocol(
                RecapRewriterProtocolV3.InputProtocolId,
                RecapRewriterProtocolV3.PriorProjectionSchemaId,
                RecapRewriterProtocolV3
                    .HistorySegmentRenderingSchemaId));
        MaintainerDefinitionRevision definition =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("galatea.culprit"),
                family.Digest,
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.System,
                    "culprit",
                    "Derived context from prior history: culprit"
                ),
                new MaintainerCapabilitySpec(
                    RecapRewriterProtocolV3.RuntimeProtocolId,
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
            RecapGridContextFactory.Open(
                raw.ReadView,
                new O200kBaseHistoryUnitLoadEstimator()
            )).Handle;
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
            ReadStoreBusinessRecords(path, items),
            contributions);
    }

    private static string[] ReadStoreBusinessRecords(
        string path, IReadOnlyList<RecapGridStoreExportItem> items) {
        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(RecapGridStoreMaintenance.Verify(path));
        using var store = Assert.IsType<RecapGridStoreReaderOpenResult.Opened>(
            RecapGridStoreFactory.OpenReader(path)).Handle;
        var cells = items.Where(item => item.Kind == "cell").ToDictionary(item => item.Key,
            item => Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                store.Reader.ReadCell(new CellId(item.Key))).Value);
        object CellBody(RecapCellArtifact cell) => new {
            cell.Slot, cell.DefinitionDigest, cell.Outcome, cell.Content
        };
        var rows = items.Where(item => item.Kind == "row-view").ToDictionary(item => item.Key,
            item => Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(
                store.Reader.ReadView(new RowResultId(item.Key))).Value);
        object RowBody(RecapRowView row) => new {
            row.RefId, row.TimelineId, row.HistoryRowId,
            row.RecipeDigest, row.TargetDigest, row.PreviousHistoryRowId, row.BootstrapCompleted,
            members = row.OrderedCells.Select(member => CellBody(cells[member.CellId.Value])).ToArray()
        };
        return items.Select(item => item.Kind switch {
            "row-work" => "row-work:" + item.Key + ":"
                + Convert.ToHexString(Assert.IsType<byte[]>(item.Json)),
            "cell" => "cell:" + JsonSerializer.Serialize(CellBody(cells[item.Key])),
            "row-view" => "row:" + JsonSerializer.Serialize(RowBody(rows[item.Key])),
            "fulfilled" => "fulfilled:" + item.Key + ":" + JsonSerializer.Serialize(
                RowBody(rows[item.FulfilledRowResultId!.Value.Value])),
            _ => throw new InvalidOperationException("Unexpected Store export kind: " + item.Kind)
        }).Order(StringComparer.Ordinal).ToArray();
    }

    private static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(destination);
        CopyUnixMode(source, destination);
        foreach (string directory in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories)) {
            string target = Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)
            );
            Directory.CreateDirectory(target);
            CopyUnixMode(directory, target);
        }
        foreach (string file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories)) {
            string target = Path.Combine(
                destination,
                Path.GetRelativePath(source, file)
            );
            File.Copy(file, target);
            CopyUnixMode(file, target);
        }
    }

    private static void CopyUnixMode(string source, string destination) {
        if (OperatingSystem.IsLinux()) {
            File.SetUnixFileMode(
                destination,
                File.GetUnixFileMode(source)
            );
        }
    }

    private static IReadOnlyDictionary<string, string> SnapshotDomainFiles(
        string root
    ) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => {
            string relative = Path.GetRelativePath(root, path);
            return relative.StartsWith(
                    "derived" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                || relative.StartsWith(
                    "control" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal);
        })
        .OrderBy(static path => path, StringComparer.Ordinal)
        .ToDictionary(
            path => Path.GetRelativePath(root, path),
            path => Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(path))
            ),
            StringComparer.Ordinal
        );

    private static void RemoveFulfillmentForTest(string repository) {
        string database = Path.Combine(
            repository,
            "derived",
            "recap-grid",
            "v1",
            "grid.sqlite"
        );
        var builder = new SqliteConnectionStringBuilder {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM fulfilled_view_ref;";
        int deleted = delete.ExecuteNonQuery();
        Assert.True(deleted > 0);
        using SqliteCommand count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "UPDATE store_metadata "
            + $"SET fulfilled_view_count = fulfilled_view_count - {deleted};";
        Assert.Equal(1, count.ExecuteNonQuery());
        transaction.Commit();
    }

    private static TimelineHeadRef ReadTimelineHead(string path, RefId refId) {
        using HistoryTimelineReaderHandle timeline = Assert.IsType<
            HistoryTimelineReaderOpenResult.Opened>(
            HistoryTimelineMaintenance.OpenReader(path, refId)).Handle;
        return Assert.IsType<HistoryTimelineSnapshotResult.Available>(
            timeline.Reader.ReadSnapshot()).Head;
    }

    private static async Task RunFreshAsync(
        GalateaHostService service,
        CharacterSessionHost session,
        string connectionId,
        string message
    ) {
        GalateaLiveTurn turn = service.StartTurn(
            session, message, new GalateaTurnOptions(connectionId),
            GalateaDelegateTestConfiguration.PlayerSender);
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

    private static IReadOnlyDictionary<string,
        GalateaRecapGridDefaultPolicy> DefaultPolicies(
        GalateaRecapGridDefaultPolicy expectation
    ) => new Dictionary<string, GalateaRecapGridDefaultPolicy>(
        StringComparer.Ordinal
    ) {
        ["alice"] = expectation
    };

    private static GalateaConfig Config(
        string path,
        CompletionConnectionConfig connection,
        string systemPrompt = "test system prompt"
    ) => new(
        [new GalateaCharacterConfig(
            "alice",
            new GalateaCharacterName("Galatea"),
            path,
            path + "-delegation-state",
            path + "-character-memory-state",
            GalateaDelegateTestConfiguration.CreateHomeDirectory(path, "alice"),
            GalateaSessionProvisioning.ExistingOnly,
            systemPrompt,
            connection.Id)],
        GalateaDelegateTestConfiguration.Players,
        [connection],
        [connection.Id],
        InputNormalizerConnectionId: null,
        Delegates: GalateaDelegateTestConfiguration.Create());

    private static int CountSystemPromptSetups(
        SessionJournalEngine engine
    ) => engine.ReadCurrentLineageHeaders().HeadToRoot.Count(
        static entry => entry.Kind == SessionEventKind.SystemPromptSetup
    );

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-galatea-recap-grid-tests",
            Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
            foreach (string userId in new[] { "alice", "bob" }) {
                string home = path + "-home-" + userId;
                if (Directory.Exists(home)) {
                    Directory.Delete(home, recursive: true);
                }
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

    private sealed class CountingTimeProvider(
        DateTimeOffset utcNow,
        TimeZoneInfo localTimeZone
    ) : TimeProvider {
        private int _getUtcNowCallCount;
        internal int GetUtcNowCallCount => Volatile.Read(
            ref _getUtcNowCallCount
        );
        public override TimeZoneInfo LocalTimeZone { get; } =
            localTimeZone;

        public override DateTimeOffset GetUtcNow() {
            Interlocked.Increment(ref _getUtcNowCallCount);
            return utcNow;
        }
    }

    private sealed class TrackingClient(string responseText)
        : ICompletionClient {
        private readonly object _requestGate = new();
        private readonly List<CompletionRequest> _agentRequests = [];
        private int _dispatchCallCount;
        private int _agentDispatchCount;
        private int _recapDispatchCount;
        public string Name => "galatea-recap-grid-test";
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
            bool recap = request.TailMessages is [ObservationMessage {
                Content: { } tail
            }] && tail.Contains(
                $"\"schema\":\"{RecapRewriterProtocolV3.InputProtocolId}\"",
                StringComparison.Ordinal
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
                ? new ActionMessage([new ActionBlock.Text(
                    "原来如此，那些疑点就都对得上了。")])
                : new ActionMessage([new ActionBlock.Text(responseText)]);
            return Task.FromResult(new CompletionResult(
                action,
                new CompletionDescriptor(
                    Name, ApiSpecId, request.ModelId)));
        }
    }

    private sealed record DerivedSnapshot(
        byte[] TimelineHead,
        byte[] Descriptor,
        IReadOnlyList<string> StoreBusinessRecords,
        IReadOnlyList<string> Contributions
    );
}
