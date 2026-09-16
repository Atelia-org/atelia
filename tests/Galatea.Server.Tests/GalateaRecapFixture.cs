using System.Collections.Concurrent;
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
using Atelia.SessionJournal.Cli;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Online;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Journal = Atelia.EventJournal.EventJournal;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Formal rolling asset on an initially unprovisioned lab-owned repo.
/// Marker text exists only in maintainer replies, never in raw seed input/actions.</summary>
internal static class GalateaRecapFixture {
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    internal const string MainConnectionId = "test";
    internal const string RecapConnectionId = "recap-maintainer";
    internal static string World(int generation) => "GALATEA_LAB_WORLD_RECAP_V" + generation;
    internal static string Autobiography(int generation) => "GALATEA_LAB_AUTOBIOGRAPHY_RECAP_V" + generation;
    internal static string RecapReply(string column, int generation) => column switch {
        "world-understanding" => World(generation),
        "autobiography" => Autobiography(generation),
        _ => throw new InvalidOperationException("Unexpected recap column.")
    };

    internal static GalateaScenarioLab CreateLab(string name, ICompletionClientFactory factory,
        CompletionConnectionConfig mainConnection, CompletionConnectionConfig recapConnection,
        Action<string>? reportArtifact = null) {
        Assert.Equal(MainConnectionId, mainConnection.Id);
        Assert.Equal(RecapConnectionId, recapConnection.Id);
        GalateaScenarioLab lab = GalateaScenarioLab.Create(name, factory,
            connections: [mainConnection, recapConnection], reportArtifact: reportArtifact,
            provisionRawOnly: false,
            recapMaintenanceConnectionId: recapConnection.Id);
        try {
            // No Host/DI access until all production-readable configuration exists.
            Repository fixture;
            using (var engine = SessionJournalEngine.Open(lab.SessionDirectory)) {
                fixture = Provision(engine, new O200kBaseHistoryUnitLoadEstimator());
            }
            string configDirectory = Path.GetDirectoryName(lab.Host.ConfigPath)!;
            File.WriteAllBytes(Path.Combine(configDirectory, "recap-grid-profile.json"),
                RecapGridAgentControlProfile.Create("test-profile", fixture.Admission).ToCanonicalBytes());
            File.WriteAllBytes(Path.Combine(configDirectory, "recap-grid-routes.json"),
                Routes(fixture.Family.Digest).ToCanonicalBytes());
            return lab;
        }
        catch {
            try { lab.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception cleanup) when (GalateaExceptionClassifier.IsNonFatal(cleanup)) {
                Atelia.Diagnostics.DebugUtil.Warning("Galatea.ScenarioLab",
                    "Recap provisioning cleanup failed; synthetic root retained: " + lab.RootDirectory);
            }
            throw;
        }
    }

    internal static RecapGridRouteManifest Routes(FamilyDefinitionDigest family) => RecapGridRouteManifest.Create([
        new RecapGridRouteManifestEntry(new RecapCompletionRouteKey(family,
            RecapRewriterProtocolV3.RuntimeProtocolId, semanticModelId: null),
            RecapConnectionId, maximumConcurrency: 2, dispatchTimeout: TimeSpan.FromSeconds(20))
    ]);

    internal static Repository Provision(SessionJournalEngine engine, IHistoryUnitLoadEstimator estimator,
        RecapGridControlRegistrationBundle? inactiveAdditionalBundle = null) {
        string path = engine.Path;
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV7,
            new GalateaRecapGridAssetParameters(
                new GalateaCharacterName("Galatea")
            ),
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
            new[] { family.Digest }.Concat(inactiveAdditionalBundle?.Families.Select(
                static value => value.Digest) ?? Enumerable.Empty<FamilyDefinitionDigest>()),
            bundle.Definitions.Concat(inactiveAdditionalBundle?.Definitions ?? Enumerable.Empty<MaintainerDefinitionRevision>())
                .Select(static value =>
                value.Capability.CapabilityFingerprint),
            bundle.Definitions.Concat(inactiveAdditionalBundle?.Definitions ?? Enumerable.Empty<MaintainerDefinitionRevision>())
                .Select(static value => value.Target.Carrier),
            new[] { "world", "autobiography" }.Concat(inactiveAdditionalBundle?
                .Definitions.Select(static value => value.LogicalColumnId.Value!) ?? Enumerable.Empty<string>()),
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1_024
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
                estimator
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
                GalateaRecapGridAssets.RollingRewriteZhCnV7,
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
        if (inactiveAdditionalBundle is not null) {
            foreach (FamilyDefinition additional in inactiveAdditionalBundle.Families) {
                registered = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutFamilyDefinition(registered, additional)).Head;
            }
            foreach (MaintainerDefinitionRevision additional in inactiveAdditionalBundle.Definitions) {
                registered = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutMaintainerDefinition(registered, additional)).Head;
            }
            BuildTarget additionalTarget = BuildTarget.Create(inactiveAdditionalBundle.Definitions.Select(
                static definition => new BuildTargetColumn(definition.LogicalColumnId, definition.Digest)));
            GridBuildRecipe additionalRecipe = GridBuildRecipe.CreateFull(timeline.TimelineId,
                bootstrapThroughRowId: null, additionalTarget);
            registered = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutBuildRecipe(registered, timeline, additionalRecipe,
                    bootstrapWitness: null)).Head;
        }
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
        return new Repository(
            path,
            engine.BranchRefId,
            family,
            world,
            autobiography,
            recipe,
            admission
        );
    }

    internal sealed record Repository(string Path, RefId RefId, FamilyDefinition Family,
        MaintainerDefinitionRevision World, MaintainerDefinitionRevision Autobiography,
        GridBuildRecipe Recipe, RecapGridControlAdmission Admission);

    internal static async Task RunFreshAsync(GalateaScenarioLab lab, string text) {
        using HttpClient http = lab.Host.CreateClient();
        http.Timeout = Deadline;
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        (GalateaHostService service, CharacterSessionHost session) = await SessionAsync(lab);
        using HttpResponseMessage accepted = await http.PostAsJsonAsync("/api/v1/characters/alice/chat/turns",
            new ChatStreamRequest(text, MainConnectionId));
        Assert.Equal("completed", (await WaitAsync(accepted, service, session)).Status);
    }

    internal static async Task SeedAsync(GalateaScenarioLab lab, Factory factory) {
        // The first HTTP turn has no previous history to seal. Prove that raw
        // bootstrap explicitly before a second turn actually adopts recap.
        await RunFreshAsync(lab, "Synthetic raw bootstrap observation.");
        Assert.Equal(1, factory.MainCalls);
        Assert.Equal(0, factory.RecapCalls);
        Assert.DoesNotContain(World(1), System.Text.Encoding.UTF8.GetString(Assert.Single(factory.MainRequests)),
            StringComparison.Ordinal);
        await RunFreshAsync(lab, "Synthetic recap adoption observation.");
        factory.AssertComplete();
    }

    internal static async Task<(GalateaHostService, CharacterSessionHost)> SessionAsync(GalateaScenarioLab lab) {
        var service = lab.Host.Factory.Services.GetRequiredService<GalateaHostService>();
        using var timeout = new CancellationTokenSource(Deadline);
        return (service, await service.GetSessionAsync("alice", timeout.Token));
    }

    internal static async Task<GalateaLiveTurn> WaitAsync(HttpResponseMessage response,
        GalateaHostService service, CharacterSessionHost session) {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = Assert.IsType<StartTurnResponseDto>(
            await response.Content.ReadFromJsonAsync<StartTurnResponseDto>());
        var turn = Assert.IsType<GalateaLiveTurn>(service.FindTurn(session, started.TurnId));
        await Assert.IsAssignableFrom<Task>(turn.RunTask).WaitAsync(Deadline);
        return turn;
    }

    // Offline APIs: callers must stop every owner before reading/reconstructing.
    internal static SessionPreparedRequestReconstruction ReadLatestPrepared(string repository) {
        EventAddress prepared;
        using (var engine = SessionJournalEngine.OpenReadOnly(repository)) {
            var events = new List<SessionJournalAuditEvent>();
            engine.ScanCheckedAuditEvents(events.Add);
            prepared = events.Last(entry => entry.Kind == SessionEventKind.CompletionRequestPrepared).Address;
        }
        using Journal journal = Journal.OpenReadOnlyExisting(repository);
        return SessionPreparedRequestReconstructor.Reconstruct(
            new SessionJournalEventReader(journal), prepared,
            projector: GalateaInputProjector.Instance);
    }

    internal static void AssertAdopted(SessionPreparedRequestReconstruction frozen, int generation) {
        Assert.Null(frozen.Manifest.Commitment);
        Assert.Empty(frozen.Manifest.Plan.ExactContextInputs);
        Assert.NotEmpty(frozen.Manifest.Plan.SemanticContributions);
        string observations = string.Join("\n", frozen.Manifest.Plan.SemanticContributions
            .Where(input => input.Target.Carrier == ContextHeaderCarrier.Observation)
            .Select(input => input.ExactText));
        string actions = string.Join("\n", frozen.Manifest.Plan.SemanticContributions
            .Where(input => input.Target.Carrier == ContextHeaderCarrier.Action)
            .Select(input => input.ExactText));
        Assert.Contains(World(generation), observations, StringComparison.Ordinal);
        Assert.Contains(Autobiography(generation), actions, StringComparison.Ordinal);
    }

    internal static IReadOnlyList<SessionRequestCommitment> ReadAttemptCommitments(
        string repository, EventAddress prepared
    ) {
        var events = new List<SessionJournalAuditEvent>();
        using (var engine = SessionJournalEngine.OpenReadOnly(repository)) {
            engine.ScanCheckedAuditEvents(events.Add);
        }
        var byAddress = events.ToDictionary(static entry => entry.Address);
        using Journal journal = Journal.OpenReadOnlyExisting(repository);
        var reader = new SessionJournalEventReader(journal);
        var result = new List<SessionRequestCommitment>();
        foreach (SessionJournalAuditEvent entry in events.Where(static entry =>
                     entry.Kind == SessionEventKind.CompletionAttemptStarted)) {
            EventAddress? parent = entry.Parent;
            while (parent is { } address && address != prepared
                   && byAddress[address].Kind == SessionEventKind.CompletionAttemptStarted) {
                parent = byAddress[address].Parent;
            }
            if (parent != prepared) { continue; }
            using var frame = reader.ReadEvent(entry.Address).Unwrap();
            var body = Assert.IsType<CompletionAttemptStartedBody>(SessionEventCodec.Decode(
                SessionEventKind.CompletionAttemptStarted, frame.Payload, out int version));
            Assert.Equal(2, version);
            result.Add(Assert.IsType<SessionRequestCommitment>(body.Commitment));
        }
        return result;
    }

    internal static string ReadPlayerText(SessionInputContent content) {
        if (!content.IsStructured) {
            return PlayerTurnObservationEnvelope.TryUnwrap(content.TextValue, out PlayerTurnObservation old)
                ? old.PlayerText : content.TextValue;
        }
        Assert.True(GalateaObservationContent.TryReadPlayerText(content, out string text));
        return text;
    }

    internal static (RecapCellArtifact World, RecapCellArtifact Autobiography) ReadHeadCells(string repository) {
        RefId refId;
        using (var engine = SessionJournalEngine.OpenReadOnly(repository)) { refId = engine.BranchRefId; }
        TimelineHeadRef head = ReadTimelineHead(repository, refId);
        using RecapGridControlReaderHandle control = Assert.IsType<RecapGridControlReaderOpenResult.Opened>(
            RecapGridControlFactory.OpenReader(repository, refId)).Handle;
        var controlState = Assert.IsType<RecapGridControlSnapshotResult.Available>(control.Reader.ReadSnapshot()).Snapshot;
        using RecapGridStoreReaderHandle store = Assert.IsType<RecapGridStoreReaderOpenResult.Opened>(
            RecapGridStoreFactory.OpenReader(repository)).Handle;
        RecapRowView view = Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(
            store.Reader.ReadViewAt(new RowViewAssignmentKey(refId, head.TimelineId,
                controlState.Head.ActiveRecipeDigest!.Value, head.HeadRowId!.Value))).Value;
        RecapCellArtifact[] cells = view.OrderedCells.Select(member =>
            Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                store.Reader.ReadCell(member.CellId)).Value).ToArray();
        Assert.Equal(2, cells.Length);
        return (cells[0], cells[1]);
    }

    private static TimelineHeadRef ReadTimelineHead(string path, RefId refId) {
        using HistoryTimelineReaderHandle timeline = Assert.IsType<HistoryTimelineReaderOpenResult.Opened>(
            HistoryTimelineMaintenance.OpenReader(path, refId)).Handle;
        return Assert.IsType<HistoryTimelineSnapshotResult.Available>(timeline.Reader.ReadSnapshot()).Head;
    }

    /// <summary>Exact per-epoch quotas; unknown routes and extra calls fail immediately.</summary>
    internal sealed class Factory(int generation, int expectedMainCalls = 1, int expectedRecapCalls = 2)
        : ICompletionClientFactory {
        private int Generation { get; } = generation;
        private int ExpectedMainCalls { get; } = expectedMainCalls;
        private int ExpectedRecapCalls { get; } = expectedRecapCalls;
        private int _mainCalls;
        private int _recapCalls;
        private readonly ConcurrentDictionary<string, byte> _columns = new(StringComparer.Ordinal);
        internal int MainCalls => Volatile.Read(ref _mainCalls);
        internal int RecapCalls => Volatile.Read(ref _recapCalls);
        internal ConcurrentQueue<byte[]> MainRequests { get; } = new();

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Assert.Equal("openai-responses", connection.Kind);
            Assert.Contains(connection.Id, new[] { MainConnectionId, RecapConnectionId });
            return new Client(this, connection);
        }

        internal void AssertComplete() {
            Assert.Equal(ExpectedMainCalls, MainCalls);
            Assert.Equal(ExpectedRecapCalls, RecapCalls);
            if (ExpectedRecapCalls != 0) {
                Assert.Equal(0, ExpectedRecapCalls % 2);
                string[] expected = Enumerable.Range(Generation, ExpectedRecapCalls / 2)
                    .SelectMany(version => new[] { version + ":autobiography", version + ":world-understanding" })
                    .Order(StringComparer.Ordinal).ToArray();
                Assert.Equal(expected, _columns.Keys.Order(StringComparer.Ordinal));
            }
        }

        private sealed class Client(Factory owner, CompletionConnectionConfig connection) : ICompletionClient {
            public string Name => new Uri(connection.BaseAddress!).Host;
            public string ApiSpecId => "openai-responses-v2";
            public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request,
                CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
                cancellationToken.ThrowIfCancellationRequested();
                string answer;
                if (connection.Id == MainConnectionId) {
                    int count = Interlocked.Increment(ref owner._mainCalls);
                    Assert.InRange(count, 1, owner.ExpectedMainCalls);
                    owner.MainRequests.Enqueue(SessionRequestCanonicalizer.Canonicalize(request));
                    answer = "The character continues the synthetic investigation.";
                }
                else {
                    int count = Interlocked.Increment(ref owner._recapCalls);
                    Assert.InRange(count, 1, owner.ExpectedRecapCalls);
                    ObservationMessage tail = Assert.IsType<ObservationMessage>(Assert.Single(request.TailMessages));
                    using JsonDocument document = JsonDocument.Parse(tail.Content!);
                    string column = document.RootElement.GetProperty("logicalColumnId").GetString()!;
                    string prior = Assert.IsType<ObservationMessage>(request.PromptPrefix.SharedContextMessages[0]).Content!;
                    using JsonDocument priorDocument = JsonDocument.Parse(prior);
                    JsonElement[] columns = priorDocument.RootElement.GetProperty("columns").EnumerateArray().ToArray();
                    int replyGeneration = Assert.Single(Enumerable.Range(owner.Generation, owner.ExpectedRecapCalls / 2),
                        version => version == 1 ? columns.Length == 0 : columns.Length == 2
                            && columns.Any(entry => entry.GetProperty("logicalColumnId").GetString() == "world-understanding"
                                && entry.GetProperty("content").GetString() == World(version - 1))
                            && columns.Any(entry => entry.GetProperty("logicalColumnId").GetString() == "autobiography"
                                && entry.GetProperty("content").GetString() == Autobiography(version - 1)));
                    if (replyGeneration > owner.Generation) {
                        Assert.True(owner._columns.ContainsKey((replyGeneration - 1) + ":world-understanding"));
                        Assert.True(owner._columns.ContainsKey((replyGeneration - 1) + ":autobiography"));
                    }
                    Assert.True(owner._columns.TryAdd(replyGeneration + ":" + column, 0),
                        "Repeated same-column recap call within a planned row.");
                    answer = RecapReply(column, replyGeneration);
                }
                observer?.OnTextDelta(answer);
                return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.Text(answer)]),
                    new CompletionDescriptor(Name, ApiSpecId, request.ModelId)));
            }
        }
    }
}
