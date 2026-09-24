using System.Collections.Concurrent;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Cli;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Online;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;
using RollingRepository = Atelia.Galatea.Server.Tests.GalateaRecapFixture.Repository;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRollingRecapGridHostTests : IDisposable {
    private const string AgentConnectionId = "agent";
    private const string RecapConnectionId = "recap-maintainer";
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Fact]
    public void DefaultPolicyRejectsMismatchedRegistrationBundle() {
        P1Target target = CreateDifferentCurrentP1Target();
        P1Target other = CreateSameFamilyTwoColumnP1Target();

        Assert.Throws<ArgumentException>(() =>
            GalateaRecapGridDefaultPolicy.ForTarget(
                target.Target,
                other.Bundle
            ));
    }

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
        CharacterSessionHost session = await service.GetSessionAsync(
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
            CompletionPromptPrefix first = row[0].Request.PromptPrefix;
            CompletionPromptPrefix second = row[1].Request.PromptPrefix;
            Assert.Equal(first.SystemPrompt, second.SystemPrompt);
            Assert.Equal(first.OutputContract.SemanticFingerprint,
                second.OutputContract.SemanticFingerprint);
            Assert.Equal(first.SharedContextMessages.Select(message => (message.Kind, RenderMessage(message))),
                second.SharedContextMessages.Select(message => (message.Kind, RenderMessage(message))));
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
        string expectedPrior = JsonSerializer.Serialize(new {
            schema = "atelia.recap.prior.v1",
            columns = new[] {
                new {
                    logicalColumnId = "world-understanding",
                    semanticHeading = "galatea.world-understanding Galatea积累的世界理解：",
                    carrier = "Observation",
                    blockKey = "galatea.world-understanding",
                    content = "world-r1"
                },
                new {
                    logicalColumnId = "autobiography",
                    semanticHeading = "galatea.first-person-autobiography Galatea积累的第一人称自传：",
                    carrier = "Action",
                    blockKey = "galatea.first-person-autobiography",
                    content = "autobiography-r1"
                }
            }
        });
        Assert.All(secondRow, value => Assert.Equal(
            expectedPrior,
            value.Prior
        ));

        (RecapCellArtifact world, RecapCellArtifact autobiography) =
            ReadHeadCells(fixture);
        Assert.Equal(RecapCellOutcome.KeepUnchanged, world.Outcome);
        Assert.Equal("world-r1", world.Content);
        Assert.Equal(RecapCellOutcome.KeepUnchanged, autobiography.Outcome);
        Assert.Equal("autobiography-r2", autobiography.Content);
        int providerCallsBeforeRecent = factory.Agent.DispatchCallCount
            + factory.Recap.Invocations.Count;
        RecentTurnsResponseDto recent = await service.GetRecentTurnsAsync(
            session,
            CancellationToken.None
        );
        Assert.Equal(
            "## galatea.world-understanding Galatea积累的世界理解：\n\n"
            + "~~~~recap-block\nworld-r1\n~~~~",
            recent.ContextHeader.Observation
        );
        Assert.Equal(
            "## galatea.first-person-autobiography Galatea积累的第一人称自传：\n\n"
            + "~~~~recap-block\nautobiography-r2\n~~~~",
            recent.ContextHeader.Action
        );
        Assert.Equal("ready", recent.RecapGridReadiness?.State);

        SessionGoverningSetup governing = session.Engine
            .ResolveGoverningSetup(
                session.Engine.ReadCurrentHead()!.Value
            );
        _ = session.Engine.AppendRuntimeConfigSetup(
            governing.RuntimeConfig with {
                DerivedContext = new SessionDerivedContextConfiguration(2)
            }
        );
        RecentTurnsResponseDto olderContext =
            await service.GetRecentTurnsAsync(
                session,
                CancellationToken.None
            );
        Assert.Equal(
            "## galatea.first-person-autobiography Galatea积累的第一人称自传：\n\n"
            + "~~~~recap-block\nautobiography-r1\n~~~~",
            olderContext.ContextHeader.Action
        );
        Assert.DoesNotContain(
            "autobiography-r2",
            olderContext.ContextHeader.Action,
            StringComparison.Ordinal
        );
        Assert.Equal(
            providerCallsBeforeRecent,
            factory.Agent.DispatchCallCount
                + factory.Recap.Invocations.Count
        );
        Assert.All(completion.ReadTelemetrySnapshot().Events, value => {
            Assert.Equal(RecapConnectionId, value.ConnectionId);
            Assert.Equal("recap-model-a", value.ModelId);
            Assert.Equal(fixture.Family.Digest, value.FamilyDigest);
        });
    }

    [Fact]
    public async Task ModelSwitchAfterPartialFailureBuildsOnlyMissingCell() {
        P1Target currentP1 = CreateDifferentCurrentP1Target();
        RollingRepository fixture = CreateRollingRepository(currentP1.Bundle);
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
            ),
            DefaultPolicies(GalateaRecapGridDefaultPolicy.ForTarget(
                fixture.Recipe.Target))
        )) {
            CharacterSessionHost sessionA = await serviceA.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            await RunFreshAsync(serviceA, sessionA, "seed row");
            int agentCallsBeforeFailure = factoryA.Agent.DispatchCallCount;
            GalateaLiveTurn failed = serviceA.StartTurn(
                sessionA,
                "model-a fails one column",
                new GalateaTurnOptions(AgentConnectionId),
                GalateaDelegateTestConfiguration.PlayerSender
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
        Assert.NotEqual(fixture.Recipe.Target.Digest, currentP1.Target.Digest);

        RecapCellArtifact worldBefore = Assert.Single(ReadAllCells(fixture));
        Assert.Equal(fixture.World.LogicalColumnId,
            worldBefore.LogicalColumnId);
        Assert.Equal(fixture.World.Digest, worldBefore.DefinitionDigest);
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
        RowWork frozenWork;
        using (RecapGridStoreReaderHandle store = Assert.IsType<
                   RecapGridStoreReaderOpenResult.Opened>(
                   RecapGridStoreFactory.OpenReader(fixture.Path)).Handle) {
            frozenWork = Assert.IsType<
                RecapGridStoreReadResult<RowWork>.Found>(
                store.Reader.ReadRowWork(new RowWorkKey(
                    fixture.RefId,
                    partialTimeline.TimelineId,
                    missing.RecipeDigest,
                    missing.RowId))).Value;
        }
        var missingSlot = new CellSlot(missing.RecipeDigest, missing.RowId,
            frozenWork.WorkId, missing.LogicalColumnId);
        RowWorkId frozenWorkId = frozenWork.WorkId;
        BuildTargetDigest frozenProducerTarget = frozenWork.ProducerTarget.Digest;
        Assert.Equal(fixture.Autobiography.LogicalColumnId,
            missing.LogicalColumnId);
        RecapCompletionTelemetryEvent[] modelAEvents = completionA
            .ReadTelemetrySnapshot().Events.ToArray();
        Assert.Equal(2, modelAEvents.Length);
        Assert.All(modelAEvents, value => {
            Assert.Equal(RecapConnectionId, value.ConnectionId);
            Assert.Equal("recap-model-a", value.ModelId);
            Assert.Equal(fixture.Family.Digest, value.FamilyDigest);
            Assert.Equal(fixture.Family.Digest,
                value.RouteKey.FamilyDigest);
            Assert.Equal(RecapRewriterProtocolV3.RuntimeProtocolId,
                value.RouteKey.RuntimeProtocolId);
            Assert.Null(value.RouteKey.SemanticModelId);
        });
        RecapCompletionTelemetryEvent failedEvent = Assert.Single(
            modelAEvents,
            value => value.DefinitionDigest == fixture.Autobiography.Digest
        );
        Assert.Equal("failed", failedEvent.ProviderOutcome);
        Assert.Equal(missingSlot, failedEvent.Slot);

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
            ),
            // P1 is a genuinely different registered target, but has no
            // route. Durable P0 work must therefore select its own target.
            DefaultPolicies(GalateaRecapGridDefaultPolicy.ForTarget(currentP1.Target))
        )) {
            CharacterSessionHost sessionB = await serviceB.GetSessionAsync(
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
            completionB.ReadTelemetrySnapshot().Events
        );
        Assert.Equal(RecapConnectionId, modelBEvent.ConnectionId);
        Assert.Equal("recap-model-b", modelBEvent.ModelId);
        Assert.Equal(fixture.Family.Digest, modelBEvent.FamilyDigest);
        Assert.Equal(fixture.Family.Digest,
            modelBEvent.RouteKey.FamilyDigest);
        Assert.Equal(RecapRewriterProtocolV3.RuntimeProtocolId,
            modelBEvent.RouteKey.RuntimeProtocolId);
        Assert.Null(modelBEvent.RouteKey.SemanticModelId);
        Assert.Equal(fixture.Autobiography.Digest,
            modelBEvent.DefinitionDigest);
        Assert.Equal(missingSlot, modelBEvent.Slot);
        using (RecapGridStoreReaderHandle store = Assert.IsType<
                   RecapGridStoreReaderOpenResult.Opened>(
                   RecapGridStoreFactory.OpenReader(fixture.Path)).Handle) {
            RowWork persisted = Assert.IsType<
                RecapGridStoreReadResult<RowWork>.Found>(
                store.Reader.ReadRowWork(frozenWork.Key)).Value;
            Assert.Equal(frozenWorkId, persisted.WorkId);
            Assert.Equal(frozenProducerTarget, persisted.ProducerTarget.Digest);
            Assert.Equal(fixture.Recipe.Target.Digest,
                persisted.ProducerTarget.Digest);
        }
        Assert.Equal(partialTimeline,
            ReadTimelineHead(fixture.Path, fixture.RefId));
        Assert.Equal(partialControl, ReadControlSnapshot(fixture).Head);
        (RecapCellArtifact worldAfter, RecapCellArtifact autobiography) =
            ReadHeadCells(fixture);
        Assert.Equal(worldBefore, worldAfter);
        Assert.Equal("autobiography-from-b", autobiography.Content);
        Assert.Equal(RecapCellOutcome.Updated, autobiography.Outcome);
        Assert.IsType<RecapGridBuildProgressResult.Complete>(
            InspectProgress(fixture)
        );
    }

    [Fact]
    public async Task CompletedP0ReadinessAndMaterializationIgnoreCurrentP1() {
        P1Target currentP1 = CreateSameFamilyTwoColumnP1Target();
        RollingRepository fixture = CreateRollingRepository();
        var seedFactory = new RoutedCompletionFactory("seed", static request =>
            RecapReply.Updated("p0-" + request.LogicalColumnId));
        RecapGridCompletionHost seedCompletion = CreateCompletionHost(
            fixture, ModelAConnections(), seedFactory);
        await using (var seed = new GalateaHostService(
            Config(fixture.Path, ModelAConnections()),
            DisabledGalateaUserMessageNormalizer.Instance,
            new GalateaRecapGridComposition(seedCompletion,
                RecapGridOnlineLimits.Production, _estimator),
            DefaultPolicies(GalateaRecapGridDefaultPolicy.ForTarget(
                fixture.Recipe.Target)))) {
            CharacterSessionHost session = await seed.GetSessionAsync("alice",
                CancellationToken.None);
            await RunFreshAsync(seed, session, "raw bootstrap");
            await RunFreshAsync(seed, session, "complete P0 recap");
        }
        Assert.NotEmpty(seedFactory.Recap.Invocations);
        TimelineHeadRef r0Head = ReadTimelineHead(fixture.Path, fixture.RefId);
        HistoryRowId r0 = Assert.IsType<HistoryRowId>(r0Head.HeadRowId);
        PersistedRowState r0Before = ReadPersistedRowState(
            fixture,
            r0Head.TimelineId,
            r0
        );
        Assert.Equal(fixture.Recipe.Target.Digest,
            r0Before.Work.ProducerTarget.Digest);
        Assert.Equal(fixture.Recipe.Target.ToCanonicalBytes(),
            r0Before.Work.ProducerTarget.ToCanonicalBytes());
        Assert.Equal(2, r0Before.Cells.Length);
        ControlHeadRef controlBefore = ReadControlSnapshot(fixture).Head;
        Assert.Equal(fixture.Recipe.Digest, controlBefore.ActiveRecipeDigest);
        Assert.DoesNotContain(
            ReadControlSnapshot(fixture).Definitions,
            definition => currentP1.Target.OrderedColumns.Any(column =>
                column.DefinitionDigest == definition.Digest)
        );
        string storePath = Path.Combine(fixture.Path, "derived", "recap-grid",
            "v1", "grid.sqlite");
        byte[] storeBefore = File.ReadAllBytes(storePath);
        int routeLoads = 0;
        var readFactory = new RoutedCompletionFactory("must not dispatch",
            static _ => throw new InvalidOperationException("read must not recap"));
        RecapGridCompletionHost readCompletion = CreateCompletionHost(fixture,
            ModelAConnections(), readFactory, () => {
                Interlocked.Increment(ref routeLoads);
                throw new InvalidOperationException("completed P0 reads must not load routes");
            });
        await using (var service = new GalateaHostService(
            Config(fixture.Path, ModelAConnections()),
            DisabledGalateaUserMessageNormalizer.Instance,
            new GalateaRecapGridComposition(readCompletion,
                RecapGridOnlineLimits.Production, _estimator),
            DefaultPolicies(GalateaRecapGridDefaultPolicy.ForTarget(
                currentP1.Target,
                currentP1.Bundle)))) {
            CharacterSessionHost reopened = await service.GetSessionAsync("alice",
                CancellationToken.None);

            RecentTurnsResponseDto result = await service.GetRecentTurnsAsync(
                reopened, CancellationToken.None);

            Assert.Equal("ready", result.RecapGridReadiness?.State);
            Assert.Contains("galatea.world-understanding Galatea积累的世界理解：",
                result.ContextHeader.Observation, StringComparison.Ordinal);
            Assert.Contains("p0-world-understanding", result.ContextHeader.Observation,
                StringComparison.Ordinal);
            Assert.Contains("galatea.first-person-autobiography Galatea积累的第一人称自传：",
                result.ContextHeader.Action, StringComparison.Ordinal);
            Assert.Contains("p0-autobiography", result.ContextHeader.Action,
                StringComparison.Ordinal);
            Assert.DoesNotContain("P1 distinct semantic heading", result.ContextHeader.Observation,
                StringComparison.Ordinal);
            Assert.DoesNotContain("P1 distinct semantic heading", result.ContextHeader.Action,
                StringComparison.Ordinal);
            Assert.Equal(0, routeLoads);
            Assert.Empty(readFactory.Recap.Invocations);
            Assert.Equal(0, readFactory.Agent.DispatchCallCount);
            Assert.Equal(controlBefore, ReadControlSnapshot(fixture).Head);
            Assert.DoesNotContain(
                ReadControlSnapshot(fixture).Definitions,
                definition => currentP1.Target.OrderedColumns.Any(column =>
                    column.DefinitionDigest == definition.Digest)
            );
            Assert.Equal(storeBefore, File.ReadAllBytes(storePath));
        }

        var p1Factory = new RoutedCompletionFactory("p1 agent", static request =>
            RecapReply.Updated("p1-" + request.LogicalColumnId));
        RecapGridCompletionHost p1Completion = CreateCompletionHost(
            fixture,
            ModelAConnections(),
            p1Factory,
            additionalRouteFamily: currentP1.Bundle.Families.Single().Digest
        );
        await using (var p1Service = new GalateaHostService(
            Config(fixture.Path, ModelAConnections()),
            DisabledGalateaUserMessageNormalizer.Instance,
            new GalateaRecapGridComposition(p1Completion,
                PartialFailureLimits(), _estimator),
            DefaultPolicies(GalateaRecapGridDefaultPolicy.ForTarget(
                currentP1.Target,
                currentP1.Bundle)))) {
            CharacterSessionHost reopened = await p1Service.GetSessionAsync(
                "alice", CancellationToken.None);
            GalateaLiveTurn turn = p1Service.StartTurn(
                reopened,
                "seal exactly one new row under P1",
                new GalateaTurnOptions(AgentConnectionId),
                GalateaDelegateTestConfiguration.PlayerSender
            );
            GalateaTurnException continuation = await Assert.ThrowsAsync<
                GalateaTurnException>(() => p1Service.RunTurnAsync(
                    reopened,
                    turn,
                    CancellationToken.None
                ));
            Assert.Equal("recap-grid-maintenance-continuation",
                continuation.FailureReason);
            Assert.Equal(0, p1Factory.Agent.DispatchCallCount);
        }

        RecapInvocation[] p1Calls = p1Factory.Recap.Invocations.ToArray();
        Assert.Equal(2, p1Calls.Length);
        RecapCompletionTelemetryEvent[] p1Events = p1Completion
            .ReadTelemetrySnapshot().Events.ToArray();
        Assert.Equal(2, p1Events.Length);
        HistoryRowId h1 = Assert.Single(p1Events
            .Select(static value => value.Slot.HistoryRowId)
            .Distinct());
        TimelineHeadRef h1Head = ReadTimelineHead(fixture.Path, fixture.RefId);
        Assert.Equal(r0Head.TimelineId, h1Head.TimelineId);
        Assert.NotEqual(r0, h1);
        PersistedRowState h1State = ReadPersistedRowState(
            fixture,
            r0Head.TimelineId,
            h1
        );
        Assert.Equal(currentP1.Target.Digest,
            h1State.Work.ProducerTarget.Digest);
        Assert.Equal(currentP1.Target.ToCanonicalBytes(),
            h1State.Work.ProducerTarget.ToCanonicalBytes());
        Assert.Equal(r0, h1State.Work.PreviousHistoryRowId);
        Assert.Equal(r0Before.View.Id, h1State.Work.PreviousRowResultId);
        Assert.Equal(r0, h1State.View.PreviousHistoryRowId);
        Assert.Equal(r0Before.View.Id, h1State.View.PreviousRowResultId);
        Assert.Equal(currentP1.Target.Digest, h1State.View.TargetDigest);
        Assert.Equal(fixture.Recipe.Digest, h1State.Work.Key.RootRecipeDigest);
        Assert.Equal(fixture.Recipe.Digest, h1State.View.RecipeDigest);
        Assert.Equal(2, h1State.Cells.Length);
        Assert.Equal(currentP1.Target.OrderedColumns.Select(static column =>
                (column.LogicalColumnId, column.DefinitionDigest)),
            h1State.Cells.Select(static cell =>
                (cell.LogicalColumnId, cell.DefinitionDigest)));
        Assert.All(h1State.Cells, cell => {
            Assert.Equal(fixture.Recipe.Digest, cell.Slot.RecipeDigest);
            Assert.Equal(h1, cell.Slot.HistoryRowId);
            Assert.Equal(h1State.Work.WorkId, cell.Slot.WorkId);
        });

        Assert.Equal(currentP1.Target.OrderedColumns
                .Select(static column => column.LogicalColumnId.Value)
                .Order(StringComparer.Ordinal),
            p1Calls.Select(static call => call.LogicalColumnId)
                .Order(StringComparer.Ordinal));
        string expectedPrior = JsonSerializer.Serialize(new {
            schema = "atelia.recap.prior.v1",
            columns = new[] {
                new {
                    logicalColumnId = "world-understanding",
                    semanticHeading = "galatea.world-understanding Galatea积累的世界理解：",
                    carrier = "Observation",
                    blockKey = "galatea.world-understanding",
                    content = "p0-world-understanding"
                },
                new {
                    logicalColumnId = "autobiography",
                    semanticHeading = "galatea.first-person-autobiography Galatea积累的第一人称自传：",
                    carrier = "Action",
                    blockKey = "galatea.first-person-autobiography",
                    content = "p0-autobiography"
                }
            }
        });
        Assert.All(p1Calls, call => Assert.Equal(expectedPrior, call.Prior));
        Assert.Equal(h1State.Cells
                .Select(static cell => cell.Slot)
                .OrderBy(static slot => slot.LogicalColumnId.Value,
                    StringComparer.Ordinal),
            p1Events.Select(static value => value.Slot)
                .OrderBy(static slot => slot.LogicalColumnId.Value,
                    StringComparer.Ordinal));
        Assert.All(p1Events, value => {
            Assert.Equal(h1, value.Slot.HistoryRowId);
            Assert.Equal(h1State.Work.WorkId, value.Slot.WorkId);
            Assert.Equal(currentP1.Bundle.Families.Single().Digest,
                value.FamilyDigest);
        });

        PersistedRowState r0After = ReadPersistedRowState(
            fixture,
            r0Head.TimelineId,
            r0
        );
        Assert.Equal(r0Before.View.Id, r0After.View.Id);
        Assert.Equal(r0Before.View.Coordinate, r0After.View.Coordinate);
        Assert.Equal(r0Before.View.OrderedCells, r0After.View.OrderedCells);
        Assert.Equal(r0Before.Work.WorkId, r0After.Work.WorkId);
        Assert.Equal(r0Before.Work.ProducerTarget.Digest,
            r0After.Work.ProducerTarget.Digest);
        Assert.Equal(r0Before.Work.ToCanonicalBytes(),
            r0After.Work.ToCanonicalBytes());
        Assert.Equal(r0Before.Cells, r0After.Cells);
        RecapGridControlSnapshot controlAfter = ReadControlSnapshot(fixture);
        Assert.NotEqual(controlBefore, controlAfter.Head);
        Assert.Equal(fixture.Recipe.Digest,
            controlAfter.Head.ActiveRecipeDigest);
        Assert.All(
            currentP1.Target.OrderedColumns,
            column => Assert.Contains(
                controlAfter.Definitions,
                definition => definition.Digest == column.DefinitionDigest
            )
        );
    }

    [Theory]
    [InlineData(nameof(SessionJournalFailpoint.AfterRequestPreparedCommitted))]
    [InlineData("LegacyStarted")]
    public async Task ActiveFormalRecipeFrozenRecoveryNeverRunsRecapProvider(
        string failpointName
    ) {
        P1Target currentP1 = CreateDifferentCurrentP1Target();
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
            SessionJournalFailpoint.AfterRequestPreparedCommitted,
            failpointName == "LegacyStarted"
        );
        Assert.Equal(fixture.Recipe.Digest,
            ReadControlSnapshot(fixture).Head.ActiveRecipeDigest);
        RecapGridControlSnapshot controlBefore = ReadControlSnapshot(fixture);
        Assert.DoesNotContain(
            controlBefore.Definitions,
            definition => currentP1.Target.OrderedColumns.Any(column =>
                column.DefinitionDigest == definition.Digest)
        );

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
            ),
            DefaultPolicies(GalateaRecapGridDefaultPolicy.ForTarget(
                currentP1.Target,
                currentP1.Bundle))
        );
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn turn = service.StartRecovery(
            session,
            new GalateaTurnOptions(
                AgentConnectionId,
                GalateaTurnMode.Resume,
                ExpectedHead: recoveryHead
            )
        );

        {
            await service.RunTurnAsync(session, turn, CancellationToken.None);
            service.FinishTurn(session, turn);
            Assert.Equal("completed", turn.Status);
            Assert.Equal(1, recoveryFactory.Agent.DispatchCallCount);
        }
        Assert.Empty(recoveryFactory.Recap.Invocations);
        Assert.Equal(0, routeLoads);
        RecapGridControlSnapshot controlAfter = ReadControlSnapshot(fixture);
        Assert.Equal(controlBefore.Head, controlAfter.Head);
        Assert.DoesNotContain(
            controlAfter.Definitions,
            definition => currentP1.Target.OrderedColumns.Any(column =>
                column.DefinitionDigest == definition.Digest)
        );
    }

    private RollingRepository CreateRollingRepository(
        RecapGridControlRegistrationBundle? inactiveAdditionalBundle = null
    ) {
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            NewPath(), new SessionCreateOptions("agent-model", "test system prompt", "openai-chat/strict"));
        return GalateaRecapFixture.Provision(engine, _estimator,
            inactiveAdditionalBundle);
    }

    private static RecapGridCompletionHost CreateCompletionHost(
        RollingRepository fixture,
        IReadOnlyList<CompletionConnectionConfig> connections,
        ICompletionClientFactory factory,
        Action? onRouteLoad = null,
        FamilyDefinitionDigest? additionalRouteFamily = null
    ) {
        Func<RecapGridRouteManifest> routeLoader = () => {
            onRouteLoad?.Invoke();
            var routes = new List<RecapGridRouteManifestEntry> {
                new RecapGridRouteManifestEntry(
                    new RecapCompletionRouteKey(
                        fixture.Family.Digest,
                        RecapRewriterProtocolV3.RuntimeProtocolId,
                        semanticModelId: null
                    ),
                    RecapConnectionId,
                    maximumConcurrency: 2,
                    dispatchTimeout: TimeSpan.FromSeconds(30)
                )
            };
            if (additionalRouteFamily is { } p1Family) {
                routes.Add(new RecapGridRouteManifestEntry(
                    new RecapCompletionRouteKey(p1Family,
                        RecapRewriterProtocolV3.RuntimeProtocolId,
                        semanticModelId: null), RecapConnectionId,
                    maximumConcurrency: 2,
                    dispatchTimeout: TimeSpan.FromSeconds(30)));
            }
            return RecapGridRouteManifest.Create(routes);
        };
        CompletionConnectionsFileConfig frozen =
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
            connections,
            AgentConnectionId
        ));
        return RecapGridCompletionHost.Create(
            routeLoader,
            frozen,
            factory,
            inputProjector: GalateaInputProjector.Instance
        );
    }

    private static P1Target CreateDifferentCurrentP1Target() {
        FamilyDefinition p1Family = FamilyDefinition.Create(
            "P1 has a distinct family instruction.", [],
            RecapRewriterProtocolV3.CreateOutputProtocol(),
            RecapRewriterProtocolV3.CreateInputRenderingProtocol());
        MaintainerDefinitionRevision p1Definition =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("p1-current-policy"), p1Family.Digest,
                new ContextHeaderBlockTarget(ContextHeaderCarrier.System,
                    "p1.current-policy", "P1 distinct semantic heading"),
                new MaintainerCapabilitySpec(
                    RecapRewriterProtocolV3.RuntimeProtocolId,
                    MaintainerReadableScope.FullPriorBuildTargetAndCurrentHistorySegmentV1),
                new MaintainerDeclarativeSpec("P1 distinct definition",
                    "P1 maintains a different current-policy heading."),
                maxContentUtf8Bytes: 16 * 1024);
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(p1Definition.LogicalColumnId,
                p1Definition.Digest)]);
        return new P1Target(target, new RecapGridControlRegistrationBundle(
            [p1Family], [p1Definition], []));
    }

    private static P1Target CreateSameFamilyTwoColumnP1Target() {
        FamilyDefinition family = FamilyDefinition.Create(
            "P1 has one family and two distinct current-policy columns.", [],
            RecapRewriterProtocolV3.CreateOutputProtocol(),
            RecapRewriterProtocolV3.CreateInputRenderingProtocol());
        MaintainerCapabilitySpec capability = new(
            RecapRewriterProtocolV3.RuntimeProtocolId,
            MaintainerReadableScope.FullPriorBuildTargetAndCurrentHistorySegmentV1
        );
        MaintainerDefinitionRevision observation =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("p1-world"), family.Digest,
                new ContextHeaderBlockTarget(ContextHeaderCarrier.Observation,
                    "p1.world", "P1 distinct semantic heading (world)"),
                capability,
                new MaintainerDeclarativeSpec("P1 world",
                    "P1 maintains its world column."),
                maxContentUtf8Bytes: 16 * 1024);
        MaintainerDefinitionRevision action =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("p1-autobiography"), family.Digest,
                new ContextHeaderBlockTarget(ContextHeaderCarrier.Action,
                    "p1.autobiography",
                    "P1 distinct semantic heading (autobiography)"),
                capability,
                new MaintainerDeclarativeSpec("P1 autobiography",
                    "P1 maintains its autobiography column."),
                maxContentUtf8Bytes: 16 * 1024);
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(observation.LogicalColumnId,
                observation.Digest),
            new BuildTargetColumn(action.LogicalColumnId, action.Digest)
        ]);
        return new P1Target(target, new RecapGridControlRegistrationBundle(
            [family], [observation, action], []));
    }

    private sealed record P1Target(BuildTarget Target,
        RecapGridControlRegistrationBundle Bundle);

    private sealed record PersistedRowState(
        RecapRowView View,
        RowWork Work,
        RecapCellArtifact[] Cells
    );

    private static PersistedRowState ReadPersistedRowState(
        RollingRepository fixture,
        TimelineId timelineId,
        HistoryRowId rowId
    ) {
        using RecapGridStoreReaderHandle store = Assert.IsType<
            RecapGridStoreReaderOpenResult.Opened
        >(RecapGridStoreFactory.OpenReader(fixture.Path)).Handle;
        RecapRowView view = Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Found
        >(store.Reader.ReadViewAt(new RowViewAssignmentKey(
            fixture.RefId,
            timelineId,
            fixture.Recipe.Digest,
            rowId
        ))).Value;
        RowWork work = Assert.IsType<
            RecapGridStoreReadResult<RowWork>.Found
        >(store.Reader.ReadRowWork(new RowWorkKey(
            fixture.RefId,
            timelineId,
            fixture.Recipe.Digest,
            rowId
        ))).Value;
        RecapCellArtifact[] cells = view.OrderedCells.Select(member =>
            Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                store.Reader.ReadCell(member.CellId)
            ).Value
        ).ToArray();
        return new PersistedRowState(view, work, cells);
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
                store.Reader.ReadCell(member.CellId)
            ).Value
        ).ToArray();
        Assert.Equal(2, cells.Length);
        return (cells[0], cells[1]);
    }

    private static RecapCellArtifact[] ReadAllCells(
        RollingRepository fixture
    ) {
        using var store = Assert.IsType<RecapGridStoreReaderOpenResult.Opened>(
            RecapGridStoreFactory.OpenReader(fixture.Path)).Handle;
        var result = new List<RecapCellArtifact>();
        RecapGridStoreExportCursor? cursor = null;
        do {
            RecapGridStoreExportPage page = Assert.IsType<
                RecapGridStoreExportResult.Page
            >(RecapGridStoreMaintenance.Export(
                fixture.Path,
                cursor,
                includeContent: false
            )).Value;
            result.AddRange(page.Items
                .Where(static item => item.Kind == "cell")
                .Select(item => Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                    store.Reader.ReadCell(new CellId(item.Key))).Value));
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
        SessionJournalFailpoint failpoint,
        bool legacyStarted
    ) {
        CompletionDispatchIdentity dispatch =
            CompletionDispatchIdentityFactory.Create(connection, client);
        var runtime = new SessionRuntime(
            client,
            CompletionTarget: new SessionCompletionTargetIdentity(
                dispatch.ConnectionId,
                dispatch.Kind,
                dispatch.ConnectionFingerprint
            ),
            ContextCandidateSource: new EmptyCandidateSource(),
            InputProjector: GalateaInputProjector.Instance
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
                    "frozen active-recipe fixture",
                    DateTimeOffset.UnixEpoch
                )
            ));
        Assert.Equal(failpoint, exception.Failpoint);
        EventAddress head = engine.ReadCurrentHead()!.Value;
        engine.Dispose();
        return legacyStarted ? LegacyPreparedV7Fixture.AppendStarted(fixture.Path, head) : head;
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
        CharacterSessionHost session,
        string message
    ) {
        GalateaLiveTurn turn = service.StartTurn(
            session,
            message,
            new GalateaTurnOptions(AgentConnectionId),
            GalateaDelegateTestConfiguration.PlayerSender
        );
        await service.RunTurnAsync(session, turn, CancellationToken.None);
        service.FinishTurn(session, turn);
        Assert.Equal("completed", turn.Status);
    }

    private static GalateaConfig Config(
        string path,
        IReadOnlyList<CompletionConnectionConfig> connections
    ) => new(
        [new GalateaCharacterConfig(
            "alice",
            new GalateaCharacterName("Galatea"),
            path,
            path + "-delegation-state",
            path + "-character-memory-state",
            GalateaDelegateTestConfiguration.CreateHomeDirectory(path, "alice"),
            GalateaSessionProvisioning.ExistingOnly,
            "test system prompt",
            AgentConnectionId,
            [new(AgentConnectionId, "", "")])],
        GalateaDelegateTestConfiguration.Players,
        connections,
        InputNormalizerConnectionId: null,
        Delegates: GalateaDelegateTestConfiguration.Create()
    );

    private static IReadOnlyDictionary<string,
        GalateaRecapGridDefaultPolicy> DefaultPolicies(
        GalateaRecapGridDefaultPolicy policy
    ) => new Dictionary<string, GalateaRecapGridDefaultPolicy>(
        StringComparer.Ordinal) { ["alice"] = policy };

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
            foreach (string characterId in new[] { "alice", "bob" }) {
                string home = path + "-home-" + characterId;
                if (Directory.Exists(home)) {
                    Directory.Delete(home, recursive: true);
                }
            }
        }
    }

    private sealed record RecapReply(
        string? Content,
        bool Invalid = false
    ) {
        internal static RecapReply Updated(string content) => new(content);

        internal static RecapReply Keep() => new(Content: null);

        internal static RecapReply InvalidTerminal() => new(
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
            string content = reply.Content ?? PriorContent(prior, column);
            IReadOnlyList<ActionBlock> blocks = reply.Invalid
                ? [
                    new ActionBlock.Text(content),
                    new ActionBlock.Text("invalid-second-block")
                ]
                : [new ActionBlock.Text(content)];
            return Task.FromResult(new CompletionResult(
                new ActionMessage(blocks),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }

        private static string PriorContent(string prior, string column) {
            using JsonDocument document = JsonDocument.Parse(prior);
            foreach (JsonElement item in document.RootElement
                .GetProperty("columns").EnumerateArray()) {
                if (string.Equals(
                        item.GetProperty("logicalColumnId").GetString(),
                        column,
                        StringComparison.Ordinal)) {
                    return item.GetProperty("content").GetString()!;
                }
            }
            throw new InvalidOperationException(
                "A keep reply requires an exact same-column prior."
            );
        }
    }

}
