using System.Collections.Concurrent;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Cli;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Online;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRollingRecapGridHostTests : IDisposable {
    private const string AgentConnectionId = "agent";
    private const string RecapConnectionId = "recap-maintainer";
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Fact]
    public async Task FormalAsset_RollsSharedPriorAndKeepThroughRealHost() {
        RollingRepository fixture = CreateRollingRepository();
        var factory = new RoutedCompletionFactory(
            agentAnswer: "agent answer",
            recapScript: static request => {
                if (request.Prior.Contains(
                        "\"logicalColumnId\":\"world-understanding\"",
                        StringComparison.Ordinal)) {
                    return request.LogicalColumnId == "world-understanding"
                        ? RecapReply.Keep()
                        : RecapReply.Updated("autobiography-r2");
                }
                return request.LogicalColumnId == "world-understanding"
                    ? RecapReply.Updated("world-r1")
                    : RecapReply.Updated("autobiography-r1");
            }
        );
        RecapGridCompletionHost completion = CreateCompletionHost(
            fixture,
            ModelAConnections(),
            factory
        );
        var composition = new GalateaRecapGridComposition(
            completion,
            RecapGridOnlineLimits.Production,
            _estimator
        );
        await using var service = new GalateaHostService(
            Config(fixture.Path, ModelAConnections()),
            DisabledGalateaUserMessageNormalizer.Instance,
            composition
        );
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        await RunFreshAsync(service, session, "turn one");
        await RunFreshAsync(service, session, "turn two");
        await RunFreshAsync(service, session, "turn three");

        RecapInvocation[] recap = factory.Recap.Invocations.ToArray();
        Assert.Equal(6, recap.Length);
        RecapInvocation[][] rows = recap.Chunk(2).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.All(rows, static row => {
            Assert.Equal(2, row.Length);
            Assert.Same(row[0].Request.PromptPrefix,
                row[1].Request.PromptPrefix);
            Assert.NotEqual(Tail(row[0].Request), Tail(row[1].Request));
        });
        RecapInvocation[] firstRow = rows[0];
        RecapInvocation[] secondRow = rows[1];
        Assert.Equal(
            ["autobiography", "world-understanding"],
            firstRow.Select(static value => value.LogicalColumnId)
                .Order(StringComparer.Ordinal)
        );
        Assert.EndsWith("\"columns\":[]}", firstRow[0].Prior,
            StringComparison.Ordinal);
        Assert.All(firstRow, static value => {
            string rendered = string.Join("\n", value.Request.PromptPrefix
                .SharedContextMessages.Select(RenderMessage))
                + "\n" + Tail(value.Request);
            Assert.DoesNotContain("world-r1", rendered,
                StringComparison.Ordinal);
            Assert.DoesNotContain("autobiography-r1", rendered,
                StringComparison.Ordinal);
        });
        const string expectedPrior =
            "{\"schema\":\"atelia.recap.prior.v1\",\"columns\":["
            + "{\"logicalColumnId\":\"world-understanding\","
            + "\"content\":\"world-r1\"},"
            + "{\"logicalColumnId\":\"autobiography\","
            + "\"content\":\"autobiography-r1\"}]}";
        Assert.All(secondRow, value => Assert.Equal(
            expectedPrior,
            value.Prior
        ));

        (RecapCellArtifact world, RecapCellArtifact autobiography) =
            ReadHeadCells(fixture);
        Assert.Equal(RecapCellOutcome.KeepUnchanged, world.Outcome);
        Assert.Equal("world-r1", world.Content);
        Assert.Equal(RecapCellOutcome.Updated, autobiography.Outcome);
        Assert.Equal("autobiography-r2", autobiography.Content);
        Assert.All(completion.Telemetry.ReadSnapshot().Events, value => {
            Assert.Equal(RecapConnectionId, value.ConnectionId);
            Assert.Equal("recap-model-a", value.ModelId);
            Assert.Equal(fixture.Family.Digest, value.FamilyDigest);
        });
    }

    [Fact]
    public async Task ModelSwitchAfterPartialFailureBuildsOnlyMissingCell() {
        RollingRepository fixture = CreateRollingRepository();
        var factoryA = new RoutedCompletionFactory(
            agentAnswer: "agent-a",
            recapScript: static request =>
                request.LogicalColumnId == "world-understanding"
                    ? RecapReply.Updated("world-from-a")
                    : RecapReply.InvalidTerminal()
        );
        RecapGridCompletionHost completionA = CreateCompletionHost(
            fixture,
            ModelAConnections(),
            factoryA
        );
        await using (var serviceA = new GalateaHostService(
            Config(fixture.Path, ModelAConnections()),
            DisabledGalateaUserMessageNormalizer.Instance,
            new GalateaRecapGridComposition(
                completionA,
                PartialFailureLimits(),
                _estimator
            )
        )) {
            UserSessionHost sessionA = await serviceA.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            await RunFreshAsync(serviceA, sessionA, "seed row");
            int agentCallsBeforeFailure = factoryA.Agent.DispatchCallCount;
            GalateaLiveTurn failed = serviceA.StartTurn(
                sessionA,
                "model-a fails one column",
                new GalateaTurnOptions(AgentConnectionId)
            );
            GalateaTurnException failure = await Assert.ThrowsAsync<
                GalateaTurnException>(() => serviceA.RunTurnAsync(
                    sessionA,
                    failed,
                    CancellationToken.None
                ));
            Assert.Equal(
                "recap-grid-maintenance-continuation",
                failure.FailureReason
            );
            Assert.Equal(agentCallsBeforeFailure,
                factoryA.Agent.DispatchCallCount);
        }

        RecapCellArtifact worldBefore = Assert.Single(ReadAllCells(fixture));
        Assert.Equal(fixture.World.LogicalColumnId,
            worldBefore.LogicalColumnId);
        Assert.Equal(fixture.World.Digest, worldBefore.DefinitionDigest);
        byte[] worldCanonicalBefore = worldBefore.ToCanonicalBytes();
        ControlHeadRef partialControl = ReadControlSnapshot(fixture).Head;
        TimelineHeadRef partialTimeline = ReadTimelineHead(
            fixture.Path,
            fixture.RefId
        );
        RecapGridBuildProgressResult.Frontier frontier = Assert.IsType<
            RecapGridBuildProgressResult.Frontier
        >(InspectProgress(fixture));
        RecapGridMissingAssignmentProgress missing = Assert.Single(
            frontier.OrderedMissing
        );
        Assert.Equal(fixture.Autobiography.LogicalColumnId,
            missing.LogicalColumnId);
        RecapCompletionTelemetryEvent[] modelAEvents = completionA.Telemetry
            .ReadSnapshot().Events.ToArray();
        Assert.Equal(2, modelAEvents.Length);
        Assert.All(modelAEvents, value => {
            Assert.Equal(RecapConnectionId, value.ConnectionId);
            Assert.Equal("recap-model-a", value.ModelId);
            Assert.Equal(fixture.Family.Digest, value.FamilyDigest);
            Assert.Equal(fixture.Family.Digest,
                value.RouteKey.FamilyDigest);
            Assert.Equal(RecapRewriterProtocolV2.RuntimeProtocolId,
                value.RouteKey.RuntimeProtocolId);
            Assert.Null(value.RouteKey.SemanticModelId);
        });
        RecapCompletionTelemetryEvent failedEvent = Assert.Single(
            modelAEvents,
            value => value.DefinitionDigest == fixture.Autobiography.Digest
        );
        Assert.Equal("failed", failedEvent.ProviderOutcome);
        Assert.Equal(missing.EvaluationKey, failedEvent.EvaluationKey);

        var factoryB = new RoutedCompletionFactory(
            agentAnswer: "agent-b",
            recapScript: request =>
                request.LogicalColumnId == "autobiography"
                    ? RecapReply.Updated("autobiography-from-b")
                    : throw new InvalidOperationException(
                        "Model B must not repeat the durable world cell.")
        );
        IReadOnlyList<CompletionConnectionConfig> modelB =
            ModelBConnections();
        RecapGridCompletionHost completionB = CreateCompletionHost(
            fixture,
            modelB,
            factoryB
        );
        await using (var serviceB = new GalateaHostService(
            Config(fixture.Path, modelB),
            DisabledGalateaUserMessageNormalizer.Instance,
            new GalateaRecapGridComposition(
                completionB,
                PartialFailureLimits(),
                _estimator
            )
        )) {
            UserSessionHost sessionB = await serviceB.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            await RunFreshAsync(serviceB, sessionB, "retry with model b");
            Assert.Equal(1, factoryB.Agent.DispatchCallCount);
        }

        RecapInvocation modelBInvocation = Assert.Single(
            factoryB.Recap.Invocations
        );
        Assert.Equal("autobiography", modelBInvocation.LogicalColumnId);
        Assert.Equal("recap-model-b", modelBInvocation.Request.ModelId);
        RecapCompletionTelemetryEvent modelBEvent = Assert.Single(
            completionB.Telemetry.ReadSnapshot().Events
        );
        Assert.Equal(RecapConnectionId, modelBEvent.ConnectionId);
        Assert.Equal("recap-model-b", modelBEvent.ModelId);
        Assert.Equal(fixture.Family.Digest, modelBEvent.FamilyDigest);
        Assert.Equal(fixture.Family.Digest,
            modelBEvent.RouteKey.FamilyDigest);
        Assert.Equal(RecapRewriterProtocolV2.RuntimeProtocolId,
            modelBEvent.RouteKey.RuntimeProtocolId);
        Assert.Null(modelBEvent.RouteKey.SemanticModelId);
        Assert.Equal(fixture.Autobiography.Digest,
            modelBEvent.DefinitionDigest);
        Assert.Equal(missing.EvaluationKey, modelBEvent.EvaluationKey);
        Assert.Equal(partialTimeline,
            ReadTimelineHead(fixture.Path, fixture.RefId));
        Assert.Equal(partialControl, ReadControlSnapshot(fixture).Head);
        (RecapCellArtifact worldAfter, RecapCellArtifact autobiography) =
            ReadHeadCells(fixture);
        Assert.Equal(worldCanonicalBefore, worldAfter.ToCanonicalBytes());
        Assert.Equal("autobiography-from-b", autobiography.Content);
        Assert.Equal(RecapCellOutcome.Updated, autobiography.Outcome);
        Assert.IsType<RecapGridBuildProgressResult.Complete>(
            InspectProgress(fixture)
        );
    }

    [Theory]
    [InlineData(
        nameof(SessionJournalFailpoint.AfterRequestPreparedCommitted),
        true)]
    [InlineData(
        nameof(SessionJournalFailpoint.AfterCompletionAttemptStartedCommitted),
        false)]
    public async Task ActiveFormalRecipeFrozenRecoveryNeverRunsRecapProvider(
        string failpointName,
        bool resumes
    ) {
        RollingRepository fixture = CreateRollingRepository();
        IReadOnlyList<CompletionConnectionConfig> connections =
            ModelAConnections();
        var boundaryFactory = new RoutedCompletionFactory(
            "must not finish",
            static _ => throw new InvalidOperationException(
                "Boundary creation must not execute RecapGrid work.")
        );
        EventAddress recoveryHead = await CreateRecoveryBoundaryAsync(
            fixture,
            connections[0],
            boundaryFactory.Agent,
            Enum.Parse<SessionJournalFailpoint>(failpointName)
        );
        Assert.Equal(fixture.Recipe.Digest,
            ReadControlSnapshot(fixture).Head.ActiveRecipeDigest);

        var recoveryFactory = new RoutedCompletionFactory(
            "recovered agent",
            static _ => throw new InvalidOperationException(
                "Frozen recovery must not execute active RecapGrid work.")
        );
        int routeLoads = 0;
        RecapGridCompletionHost completion = CreateCompletionHost(
            fixture,
            connections,
            recoveryFactory,
            () => Interlocked.Increment(ref routeLoads)
        );
        await using var service = new GalateaHostService(
            Config(fixture.Path, connections),
            DisabledGalateaUserMessageNormalizer.Instance,
            new GalateaRecapGridComposition(
                completion,
                RecapGridOnlineLimits.Production,
                _estimator
            )
        );
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn turn = service.StartRecovery(
            session,
            new GalateaTurnOptions(
                AgentConnectionId,
                GalateaTurnMode.Resume,
                RestartUncertainCompletion: false,
                ExpectedHead: recoveryHead
            )
        );

        if (resumes) {
            await service.RunTurnAsync(session, turn, CancellationToken.None);
            service.FinishTurn(session, turn);
            Assert.Equal("completed", turn.Status);
            Assert.Equal(1, recoveryFactory.Agent.DispatchCallCount);
        }
        else {
            GalateaTurnException failure = await Assert.ThrowsAsync<
                GalateaTurnException>(() => service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                ));
            Assert.Equal("uncertain-completion-restart-required",
                failure.FailureReason);
            Assert.Equal(0, recoveryFactory.Agent.DispatchCallCount);
        }
        Assert.Empty(recoveryFactory.Recap.Invocations);
        Assert.Equal(0, routeLoads);
        Assert.Equal(fixture.Recipe.Digest,
            ReadControlSnapshot(fixture).Head.ActiveRecipeDigest);
    }

    [Fact]
    public async Task ActiveFormalRecipeToolContinuationSealsDurableToolTail() {
        RollingRepository fixture = CreateRollingRepository();
        var seedFactory = new RoutedCompletionFactory(
            "seed agent",
            static request => RecapReply.Updated(
                "seed-" + request.LogicalColumnId
            )
        );
        RecapGridCompletionHost seedCompletion = CreateCompletionHost(
            fixture,
            ModelAConnections(),
            seedFactory
        );
        await using (var seedService = new GalateaHostService(
            Config(fixture.Path, ModelAConnections()),
            DisabledGalateaUserMessageNormalizer.Instance,
            new GalateaRecapGridComposition(
                seedCompletion,
                RecapGridOnlineLimits.Production,
                _estimator
            )
        )) {
            UserSessionHost seedSession = await seedService.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            await RunFreshAsync(seedService, seedSession, "seed one");
            await RunFreshAsync(seedService, seedSession, "seed two");
        }
        EvaluationKeyDigest[] healthyKeys = seedCompletion.Telemetry
            .ReadSnapshot().Events.Select(static value => value.EvaluationKey)
            .ToArray();
        Assert.Equal(2, healthyKeys.Length);

        RecapGridAgentControlProfile profile =
            RecapGridAgentControlProfile.Create(
                "rolling-operator",
                fixture.Admission
            );
        EventAddress actionHead = await CreateToolContinuationBoundaryAsync(
            fixture,
            profile,
            ModelAConnections()[0],
            _estimator
        );

        var recoveryFactory = new RoutedCompletionFactory(
            "continued agent",
            static request => RecapReply.Updated(
                "continued-" + request.LogicalColumnId
            )
        );
        UserSessionHost? session = null;
        EventAddress? durableToolResultHead = null;
        int routeLoads = 0;
        RecapGridCompletionHost recoveryCompletion = CreateCompletionHost(
            fixture,
            ModelAConnections(),
            recoveryFactory,
            () => {
                Interlocked.Increment(ref routeLoads);
                Assert.NotNull(session);
                Assert.NotNull(durableToolResultHead);
                Assert.Empty(recoveryFactory.Recap.Invocations);
            },
            profile
        );
        await using var service = new GalateaHostService(
            Config(fixture.Path, ModelAConnections()),
            DisabledGalateaUserMessageNormalizer.Instance,
            new GalateaRecapGridComposition(
                recoveryCompletion,
                profile.ProfileId,
                RecapGridOnlineLimits.Production,
                _estimator
            )
        );
        session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn turn = service.StartRecovery(
            session,
            new GalateaTurnOptions(
                AgentConnectionId,
                GalateaTurnMode.Resume,
                ExpectedHead: actionHead
            )
        );

        await service.RunTurnAsync(session, turn, CancellationToken.None);
        service.FinishTurn(session, turn);

        Assert.Equal("completed", turn.Status);
        Assert.Equal(0, routeLoads);
        Assert.Empty(recoveryFactory.Recap.Invocations);
        SessionCurrentLineagePrefix lineage = session.Engine
            .ReadCurrentLineagePrefix(64);
        durableToolResultHead = Assert.Single(lineage.HeadToOldest,
            static value => value.Kind == SessionEventKind.ToolResultObserved)
            .Address;

        await RunFreshAsync(
            service,
            session,
            "seal the durable tool-result tail"
        );

        Assert.Equal(1, routeLoads);
        RecapInvocation[] withToolTail = recoveryFactory.Recap.Invocations
            .Where(static value => value.Request.PromptPrefix
                .SharedContextMessages.OfType<ToolResultsMessage>().Any())
            .ToArray();
        Assert.Equal(2, withToolTail.Length);
        Assert.Equal(
            ["autobiography", "world-understanding"],
            withToolTail.Select(static value => value.LogicalColumnId)
                .Order(StringComparer.Ordinal)
        );
        Assert.All(withToolTail, static value => {
            ToolResultsMessage results = Assert.Single(value.Request
                .PromptPrefix.SharedContextMessages
                .OfType<ToolResultsMessage>());
            Assert.Contains(
                "\"status\":\"available\"",
                string.Join("|", results.Results.Select(
                    static result => result.GetFlattenedText()
                )),
                StringComparison.Ordinal
            );
        });
        EvaluationKeyDigest[] continuationKeys = recoveryCompletion.Telemetry
            .ReadSnapshot().Events.Select(static value => value.EvaluationKey)
            .ToArray();
        Assert.Equal(continuationKeys.Length,
            continuationKeys.Distinct().Count());
        Assert.DoesNotContain(continuationKeys,
            value => healthyKeys.Contains(value));
        Assert.Equal(fixture.Recipe.Digest,
            ReadControlSnapshot(fixture).Head.ActiveRecipeDigest);
    }

    private RollingRepository CreateRollingRepository() {
        string path = NewPath();
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV2,
            out RecapGridControlRegistrationBundle? created
        ));
        RecapGridControlRegistrationBundle bundle = created!;
        FamilyDefinition family = Assert.Single(bundle.Families);
        MaintainerDefinitionRevision world = bundle.Definitions[0];
        MaintainerDefinitionRevision autobiography = bundle.Definitions[1];
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.Create
                | RecapGridControlPermission.RegisterFamily
                | RecapGridControlPermission.RegisterDefinition
                | RecapGridControlPermission.RegisterRecipe
                | RecapGridControlPermission.Activate,
            [family.Digest],
            bundle.Definitions.Select(static value =>
                value.Capability.CapabilityFingerprint),
            bundle.Definitions.Select(static value => value.Target.Carrier),
            ["world", "autobiography"],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1_024
        );
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "agent-model",
                "test system prompt",
                "openai-chat/strict"
            )
        );
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                engine.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024
                ),
                _estimator
            )
        );
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(
                engine,
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
                engine.Path,
                engine.BranchRefId,
                admission
            )
        );
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(engine.Path)
        );
        TimelineHeadRef timeline = ReadTimelineHead(
            engine.Path,
            engine.BranchRefId
        );
        using RecapGridControlHandle control = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            engine.Path,
            engine.BranchRefId,
            admission
        )).Handle;
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlSnapshotResult.Available
        >(control.Reader.ReadSnapshot()).Snapshot.Head;
        RecapGridControlOperation operation = RecapGridOperatorAssetCatalog
            .CreateProvisionOperation(
                GalateaRecapGridAssets.RollingRewriteZhCnV2,
                initial.InstanceId
            );
        ControlHeadRef registered = Assert.IsType<
            RecapGridControlOperationResult.Applied
        >(control.Coordinator.ApplyRegistrationBundle(
            initial,
            timeline,
            operation,
            bundle
        )).Head;
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timeline.TimelineId,
            bootstrapThroughRowId: null,
            BuildTarget.Create([
                new BuildTargetColumn(
                    world.LogicalColumnId,
                    world.Digest
                ),
                new BuildTargetColumn(
                    autobiography.LogicalColumnId,
                    autobiography.Digest
                )
            ])
        );
        ControlHeadRef withRecipe = Assert.IsType<
            RecapGridControlPutResult.Stored
        >(control.Coordinator.PutBuildRecipe(
            registered,
            timeline,
            recipe,
            bootstrapWitness: null
        )).Head;
        Assert.IsType<RecapGridControlActivateResult.Applied>(
            control.Coordinator.CompareExchangeActiveRecipe(
                withRecipe,
                timeline,
                recipe.Digest,
                RecapGridControlActivationPurpose.Direct
            )
        );
        return new RollingRepository(
            path,
            engine.BranchRefId,
            family,
            world,
            autobiography,
            recipe,
            admission
        );
    }

    private static RecapGridCompletionHost CreateCompletionHost(
        RollingRepository fixture,
        IReadOnlyList<CompletionConnectionConfig> connections,
        ICompletionClientFactory factory,
        Action? onRouteLoad = null,
        RecapGridAgentControlProfile? agentProfile = null
    ) {
        Func<RecapGridRouteManifest> routeLoader = () => {
            onRouteLoad?.Invoke();
            return RecapGridRouteManifest.Create([
                new RecapGridRouteManifestEntry(
                    new RecapCompletionRouteKey(
                        fixture.Family.Digest,
                        RecapRewriterProtocolV2.RuntimeProtocolId,
                        semanticModelId: null
                    ),
                    RecapConnectionId,
                    maximumConcurrency: 2,
                    dispatchTimeout: TimeSpan.FromSeconds(30),
                    maximumOutputTokens: 2_048
                )
            ]);
        };
        CompletionConnectionsFileConfig frozen =
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
            connections,
            AgentConnectionId
        ));
        return agentProfile is null
            ? RecapGridCompletionHost.Create(
                routeLoader,
                frozen,
                factory
            )
            : RecapGridCompletionHost.Create(
                routeLoader,
                frozen,
                factory,
                new RecapGridAgentControlProfileRegistry([agentProfile])
            );
    }

    private (RecapCellArtifact World, RecapCellArtifact Autobiography)
        ReadHeadCells(RollingRepository fixture) {
        TimelineHeadRef head = ReadTimelineHead(fixture.Path, fixture.RefId);
        HistoryRowId rowId = head.HeadRowId!.Value;
        using RecapGridStoreReaderHandle store = Assert.IsType<
            RecapGridStoreReaderOpenResult.Opened
        >(RecapGridStoreFactory.OpenReader(fixture.Path)).Handle;
        RecapRowView view = Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Found
        >(store.Reader.ReadViewAt(new RowViewAssignmentKey(
            fixture.RefId,
            head.TimelineId,
            fixture.Recipe.Digest,
            rowId
        ))).Value;
        RecapCellArtifact[] cells = view.OrderedCells.Select(member =>
            Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                store.Reader.ReadCell(member.CellDigest)
            ).Value
        ).ToArray();
        Assert.Equal(2, cells.Length);
        return (cells[0], cells[1]);
    }

    private static RecapCellArtifact[] ReadAllCells(
        RollingRepository fixture
    ) {
        var result = new List<RecapCellArtifact>();
        RecapGridStoreExportCursor? cursor = null;
        do {
            RecapGridStoreExportPage page = Assert.IsType<
                RecapGridStoreExportResult.Page
            >(RecapGridStoreMaintenance.Export(
                fixture.Path,
                cursor,
                includeContent: true
            )).Value;
            result.AddRange(page.Items
                .Where(static item => item.Kind == "cell")
                .Select(static item => RecapCellArtifact.DecodeCanonical(
                    item.Canonical!
                )));
            cursor = page.NextCursor;
        } while (cursor is not null);
        return result.ToArray();
    }

    private RecapGridBuildProgressResult InspectProgress(
        RollingRepository fixture
    ) {
        using SessionJournalEngine journal =
            SessionJournalEngine.OpenReadOnly(fixture.Path);
        using RecapGridManagerHandle manager = Assert.IsType<
            RecapGridManagerOpenResult.Opened
        >(RecapGridManagerFactory.Open(
            journal.ReadView,
            _estimator
        )).Handle;
        return manager.Manager.InspectBuildProgress(new RecapGridBuildRequest(
            new RecapGridBuildSelection.LiveActive(),
            throughRowId: null,
            new RecapGridBuildBudget(
                maximumRecipeRowSteps: 64,
                maximumNewCalls: 128,
                maximumElapsed: TimeSpan.FromMinutes(1)
            )
        ));
    }

    private static RecapGridControlSnapshot ReadControlSnapshot(
        RollingRepository fixture
    ) {
        using RecapGridControlReaderHandle control = Assert.IsType<
            RecapGridControlReaderOpenResult.Opened
        >(RecapGridControlFactory.OpenReader(
            fixture.Path,
            fixture.RefId
        )).Handle;
        return Assert.IsType<RecapGridControlSnapshotResult.Available>(
            control.Reader.ReadSnapshot()
        ).Snapshot;
    }

    private static async Task<EventAddress> CreateRecoveryBoundaryAsync(
        RollingRepository fixture,
        CompletionConnectionConfig connection,
        ICompletionClient client,
        SessionJournalFailpoint failpoint
    ) {
        CompletionDispatchIdentity dispatch =
            CompletionDispatchIdentityFactory.Create(connection, client);
        var runtime = new SessionRuntime(
            client,
            CompletionTarget: new SessionCompletionTargetIdentity(
                dispatch.ConnectionId,
                dispatch.Kind,
                dispatch.ConnectionFingerprint,
                dispatch.RequestAdapterFingerprint
            ),
            ContextCandidateSource: new EmptyCandidateSource()
        );
        using SessionJournalEngine engine = SessionJournalEngine.OpenForTest(
            fixture.Path,
            runtime,
            new SessionJournalTestHooks(failpoint)
        );
        SessionJournalFailpointException exception = await Assert.ThrowsAsync<
            SessionJournalFailpointException>(() => engine.SendAsync(
                engine.ReadCurrentHead()!.Value,
                GalateaHostService.WrapUserMessageForEngine(
                    "frozen active-recipe fixture"
                )
            ));
        Assert.Equal(failpoint, exception.Failpoint);
        return engine.ReadCurrentHead()!.Value;
    }

    private static async Task<EventAddress>
        CreateToolContinuationBoundaryAsync(
        RollingRepository fixture,
        RecapGridAgentControlProfile profile,
        CompletionConnectionConfig connection,
        IHistoryUnitLoadEstimator estimator
    ) {
        var client = new RecapGridInspectToolCallClient();
        CompletionDispatchIdentity dispatch =
            CompletionDispatchIdentityFactory.Create(connection, client);
        var target = new SessionCompletionTargetIdentity(
            dispatch.ConnectionId,
            dispatch.Kind,
            dispatch.ConnectionFingerprint,
            dispatch.RequestAdapterFingerprint
        );
        using SessionJournalEngine engine = SessionJournalEngine.OpenForTest(
            fixture.Path,
            new SessionRuntime(
                client,
                CompletionTarget: target,
                ContextCandidateSource: new EmptyCandidateSource()
            ),
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterActionCommitted
            )
        );
        using RecapGridAgentControlHandle agent = Assert.IsType<
            RecapGridAgentControlOpenResult.Opened
        >(RecapGridAgentControlFactory.Bind(
            engine.ReadView,
            profile,
            estimator
        )).Handle;
        using RecapGridContextHandle context = Assert.IsType<
            RecapGridContextOpenResult.Opened
        >(RecapGridContextFactory.Open(
            engine.ReadView,
            estimator
        )).Handle;
        engine.UseRuntime(new SessionRuntime(
            client,
            agent.ToolSession,
            target,
            ToolRuntimeIdentity: agent.RuntimeIdentity,
            ContextCandidateSource: context
        ));
        SessionJournalFailpointException failure = await Assert.ThrowsAsync<
            SessionJournalFailpointException>(() => engine.SendAsync(
                engine.ReadCurrentHead()!.Value,
                "inspect the exact active rolling recipe"
            ));
        Assert.Equal(SessionJournalFailpoint.AfterActionCommitted,
            failure.Failpoint);
        SessionExecutionBoundaryInspection boundary =
            engine.InspectExecutionBoundary();
        Assert.Equal(SessionExecutionPhase.AwaitingToolExecution,
            boundary.Phase);
        return boundary.Head!.Value;
    }

    private TimelineHeadRef ReadTimelineHead(string path, RefId refId) {
        using HistoryTimelineReaderHandle timeline = Assert.IsType<
            HistoryTimelineReaderOpenResult.Opened
        >(HistoryTimelineMaintenance.OpenReader(path, refId)).Handle;
        return Assert.IsType<HistoryTimelineSnapshotResult.Available>(
            timeline.Reader.ReadSnapshot()
        ).Head;
    }

    private static async Task RunFreshAsync(
        GalateaHostService service,
        UserSessionHost session,
        string message
    ) {
        GalateaLiveTurn turn = service.StartTurn(
            session,
            message,
            new GalateaTurnOptions(AgentConnectionId)
        );
        await service.RunTurnAsync(session, turn, CancellationToken.None);
        service.FinishTurn(session, turn);
        Assert.Equal("completed", turn.Status);
    }

    private static GalateaConfig Config(
        string path,
        IReadOnlyList<CompletionConnectionConfig> connections
    ) => new(
        [new GalateaUserConfig(
            "alice", "pw", path, "test system prompt")],
        connections,
        AgentConnectionId
    );

    private static IReadOnlyList<CompletionConnectionConfig>
        ModelAConnections() => [
            Connection(AgentConnectionId, "agent-model"),
            Connection(RecapConnectionId, "recap-model-a")
        ];

    private static IReadOnlyList<CompletionConnectionConfig>
        ModelBConnections() => [
            Connection(AgentConnectionId, "agent-model"),
            Connection(RecapConnectionId, "recap-model-b")
        ];

    private static RecapGridOnlineLimits PartialFailureLimits() => new(
        maximumAuditEvents: 64,
        maximumNewCalls: 2,
        softMaximumElapsed: TimeSpan.FromMinutes(1)
    );

    private static CompletionConnectionConfig Connection(
        string id,
        string model
    ) => new(
        id,
        "openai-chat",
        model,
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static string Tail(CompletionRequest request) =>
        Assert.IsType<ObservationMessage>(Assert.Single(
            request.TailMessages
        )).Content!;

    private static string RenderMessage(IHistoryMessage message) =>
        message switch {
            ToolResultsMessage value => string.Join("|", value.Results.Select(
                static result => result.GetFlattenedText()
            )),
            ObservationMessage value => value.Content ?? string.Empty,
            ActionMessage value => value.GetFlattenedText(),
            _ => message.ToString() ?? string.Empty
        };

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-galatea-rolling-grid-tests",
            Guid.NewGuid().ToString("N")
        );
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

    private sealed record RollingRepository(
        string Path,
        RefId RefId,
        FamilyDefinition Family,
        MaintainerDefinitionRevision World,
        MaintainerDefinitionRevision Autobiography,
        GridBuildRecipe Recipe,
        RecapGridControlAdmission Admission
    );

    private sealed record RecapReply(
        string Outcome,
        string? Content,
        bool Invalid = false
    ) {
        internal static RecapReply Updated(string content) => new(
            RecapRewriterProtocolV2.UpdatedOutcome,
            content
        );

        internal static RecapReply Keep() => new(
            RecapRewriterProtocolV2.KeepUnchangedOutcome,
            null
        );

        internal static RecapReply InvalidTerminal() => new(
            RecapRewriterProtocolV2.UpdatedOutcome,
            "must-not-persist",
            Invalid: true
        );
    }

    private sealed record RecapInvocation(
        CompletionRequest Request,
        string LogicalColumnId,
        string Prior
    );

    private sealed class EmptyCandidateSource
        : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(new SessionContextCandidateSelection(
            SessionContextCandidateSelectionStatus.EmptyLineage,
            Candidate: null
        ));

        public ValueTask<SessionContextCandidateMaterializationResult>
            MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "An empty fixture must not materialize a candidate."
        );
    }

    private sealed class RoutedCompletionFactory : ICompletionClientFactory {
        internal RoutedCompletionFactory(
            string agentAnswer,
            Func<RecapInvocation, RecapReply> recapScript
        ) {
            Agent = new AgentClient(agentAnswer);
            Recap = new RecapClient(recapScript);
        }

        internal AgentClient Agent { get; }
        internal RecapClient Recap { get; }

        public ICompletionClient Create(CompletionConnectionConfig connection)
            => connection.Id switch {
                AgentConnectionId => Agent,
                RecapConnectionId => Recap,
                _ => throw new InvalidOperationException(
                    $"Unexpected connection '{connection.Id}'.")
            };
    }

    private sealed class AgentClient(string answer) : ICompletionClient {
        private int _dispatchCallCount;
        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );
        public string Name => "fake-agent";
        public string ApiSpecId => "openai-chat-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            observer?.OnTextDelta(answer);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(answer)]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }

    private sealed class RecapClient(
        Func<RecapInvocation, RecapReply> script
    ) : ICompletionClient {
        private int _callId;
        internal ConcurrentQueue<RecapInvocation> Invocations { get; } = [];
        public string Name => "fake-recap";
        public string ApiSpecId => "openai-chat-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            string tail = Tail(request);
            using JsonDocument tailDocument = JsonDocument.Parse(tail);
            string column = tailDocument.RootElement.GetProperty(
                "logicalColumnId"
            ).GetString()!;
            string prior = Assert.IsType<ObservationMessage>(
                request.PromptPrefix.SharedContextMessages[0]
            ).Content!;
            var invocation = new RecapInvocation(request, column, prior);
            Invocations.Enqueue(invocation);
            RecapReply reply = script(invocation);
            string tool = reply.Invalid
                ? "invalid-terminal"
                : RecapRewriterProtocolV2.TerminalToolName;
            string arguments = JsonSerializer.Serialize(new {
                outcome = reply.Outcome,
                content = reply.Content
            });
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                    tool,
                    $"recap-{Interlocked.Increment(ref _callId)}",
                    arguments
                ))]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }

    private sealed class RecapGridInspectToolCallClient : ICompletionClient {
        public string Name => "rolling-recipe-inspection";
        public string ApiSpecId => "openai-chat-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                    "recap_grid.control",
                    "inspect-active-recipe",
                    "{\"action\":\"inspect\"}"
                ))]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }
}
