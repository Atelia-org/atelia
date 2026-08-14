using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Cli;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GalateaOperatorConsoleCollection {
    public const string Name = "Galatea RecapGrid operator console";
}

[Collection(GalateaOperatorConsoleCollection.Name)]
public sealed class GalateaRecapGridPublicOperatorChainTests : IDisposable {
    private const string AgentConnectionId = "agent";
    private const string RecapConnectionId = "recap-maintainer";
    private const string ProfileId = "rolling-operator";
    private readonly string _root = Path.Combine(
        Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
        "atelia-galatea-public-operator-chain-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task PublicCliChainFeedsStrictConfigAndRealHostReadiness() {
        Directory.CreateDirectory(_root);
        string repository = Path.Combine(_root, "session");
        string admission = Path.Combine(_root, "admission.json");
        string profile = Path.Combine(_root, "profile.json");
        string routes = Path.Combine(_root, "routes.json");
        string recipePath = Path.Combine(_root, "full-recipe.json");
        string timelineConfirmation = Path.Combine(
            _root,
            "timeline-head.json"
        );
        var provider = new OperatorChainCompletionFactory();

        Assert.Equal(0, Run(provider,
            "scaffold",
            "--asset", GalateaRecapGridAssets.RollingRewriteZhCnV1,
            "--profile-id", ProfileId,
            "--connection-id", RecapConnectionId,
            "--permission", "create",
            "--permission", "register-family",
            "--permission", "register-definition",
            "--permission", "register-recipe",
            "--permission", "activate",
            "--logical-column-prefix", "world-understanding",
            "--logical-column-prefix", "autobiography",
            "--max-bootstrap-rows", "64",
            "--max-projected-calls", "1024",
            "--max-concurrency", "2",
            "--dispatch-timeout-ms", "30000",
            "--max-output-tokens", "2048",
            "--admission-output", admission,
            "--profile-output", profile,
            "--route-output", routes
        ));
        Assert.Equal(0, provider.CreateCallCount);

        RefId refId;
        using (SessionJournalEngine raw = SessionJournalEngine.Create(
            repository,
            new SessionCreateOptions(
                "agent-model",
                "operator-chain system prompt",
                "openai-chat/strict"
            )
        )) {
            refId = raw.BranchRefId;
        }
        string refText = refId.ToHexString();
        Assert.Equal(0, Run(provider,
            "init",
            "--input", repository,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--confirm-ref", refText,
            "--admission", admission,
            "--partition-algorithm",
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            "--history-load-estimator",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            "--minimum-recent-history-load", "1",
            "--target-history-load", "1",
            "--max-raw-events", "64",
            "--max-rendered-bytes", "1048576"
        ));
        Assert.Equal(0, Run(provider,
            "control", "provision-asset",
            "--input", repository,
            "--confirm-ref", refText,
            "--admission", admission,
            "--asset", GalateaRecapGridAssets.RollingRewriteZhCnV1
        ));
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV1,
            out RecapGridControlRegistrationBundle? created
        ));
        RecapGridControlRegistrationBundle bundle = created!;
        Assert.Equal(
            ["world-understanding", "autobiography"],
            bundle.Definitions.Select(static value =>
                value.LogicalColumnId.Value)
        );
        Assert.Equal(0, Run(provider,
            "control", "compose-full-recipe",
            "--input", repository,
            "--output", recipePath,
            "--definition", bundle.Definitions[0].Digest.Value!,
            "--definition", bundle.Definitions[1].Digest.Value!
        ));
        GridBuildRecipe recipe = GridBuildRecipe.DecodeCanonical(
            File.ReadAllBytes(recipePath)
        );
        Assert.Equal(
            bundle.Definitions.Select(static value => value.Digest),
            recipe.Target.OrderedColumns.Select(static value =>
                value.DefinitionDigest)
        );
        Assert.Equal(0, Run(provider,
            "control", "put-recipe",
            "--input", repository,
            "--confirm-ref", refText,
            "--admission", admission,
            "--recipe", recipePath
        ));

        ControlHeadRef controlHead = ReadControlHead(repository, refId);
        TimelineHeadRef timelineHead = ReadTimelineHead(repository, refId);
        File.WriteAllBytes(
            timelineConfirmation,
            timelineHead.ToCanonicalBytes()
        );
        Assert.Equal(0, Run(provider,
            "control", "activate",
            "--input", repository,
            "--confirm-ref", refText,
            "--admission", admission,
            "--recipe", recipe.Digest.Value!,
            "--confirm-instance", controlHead.InstanceId.Value,
            "--confirm-timeline", controlHead.TimelineId.Value,
            "--confirm-generation", controlHead.Generation.ToString(),
            "--confirm-state", controlHead.StateDigest.Value,
            "--confirm-active", "none",
            "--confirm-timeline-head", timelineConfirmation
        ));
        Assert.Equal(recipe.Digest,
            ReadControlHead(repository, refId).ActiveRecipeDigest);
        Assert.Equal(0, provider.CreateCallCount);

        WriteStrictConfig(repository, profile, routes);
        string configPath = Path.Combine(_root, "config.json");
        GalateaConfig config = GalateaConfigLoader.Load(configPath);
        Assert.NotNull(config.RecapGrid);
        Assert.Equal(0, provider.CreateCallCount);
        await using var service = new GalateaHostService(
            config,
            provider,
            DisabledGalateaUserMessageNormalizer.Instance
        );
        Assert.Equal(0, provider.CreateCallCount);
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(0, provider.CreateCallCount);

        await RunFreshAsync(service, session, "operator chain one");
        await RunFreshAsync(service, session, "operator chain two");
        await RunFreshAsync(service, session, "operator chain three");

        Assert.True(provider.RecapDispatchCount >= 2);
        Assert.True(provider.AgentDispatchCount >= 3);
        int dispatchesBeforeReadiness = provider.DispatchCallCount;
        EventAddress rawHead = session.Engine.ReadCurrentHead()!.Value;
        RecapGridReadinessSnapshotDto readiness =
            GalateaRecapGridReadiness.Inspect(
                session.Engine.ReadView,
                rawHead,
                CancellationToken.None
            );
        Assert.Equal("exact", readiness.Freshness);
        Assert.Equal("ready", readiness.State);
        Assert.Equal(recipe.Digest.Value,
            readiness.Authority?.RecipeDigest);
        Assert.Equal(dispatchesBeforeReadiness,
            provider.DispatchCallCount);
    }

    private void WriteStrictConfig(
        string repository,
        string profile,
        string routes
    ) {
        string configPath = Path.Combine(_root, "config.json");
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                new GalateaUsersFileConfig(
                    [new GalateaUserConfig(
                        "alice",
                        "pw",
                        repository,
                        "operator-chain system prompt"
                    )],
                    RecapGrid: new GalateaRecapGridFileConfig(
                        Path.GetRelativePath(_root, routes),
                        [Path.GetRelativePath(_root, profile)],
                        ProfileId
                    )
                ),
                GalateaJson.Options
            )
        );
        File.WriteAllText(
            Path.Combine(_root, GalateaConfigLoader.ConnectionsFileName),
            JsonSerializer.Serialize(
                new CompletionConnectionsFileConfig(
                    [
                        Connection(AgentConnectionId, "agent-model"),
                        Connection(RecapConnectionId, "recap-model")
                    ],
                    AgentConnectionId
                ),
                GalateaJson.Options
            )
        );
    }

    private static ControlHeadRef ReadControlHead(
        string repository,
        RefId refId
    ) {
        using RecapGridControlReaderHandle control = Assert.IsType<
            RecapGridControlReaderOpenResult.Opened
        >(RecapGridControlFactory.OpenReader(repository, refId)).Handle;
        return Assert.IsType<RecapGridControlSnapshotResult.Available>(
            control.Reader.ReadSnapshot()
        ).Snapshot.Head;
    }

    private static TimelineHeadRef ReadTimelineHead(
        string repository,
        RefId refId
    ) {
        using HistoryTimelineReaderHandle timeline = Assert.IsType<
            HistoryTimelineReaderOpenResult.Opened
        >(HistoryTimelineMaintenance.OpenReader(repository, refId)).Handle;
        return Assert.IsType<HistoryTimelineSnapshotResult.Available>(
            timeline.Reader.ReadSnapshot()
        ).Head;
    }

    private static int Run(
        ICompletionClientFactory provider,
        params string[] arguments
    ) => Atelia.SessionJournal.Cli.Program.MainCore(
        ["recap-grid", .. arguments],
        provider
    );

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

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class OperatorChainCompletionFactory
        : ICompletionClientFactory {
        private int _createCallCount;
        private int _dispatchCallCount;
        private int _agentDispatchCount;
        private int _recapDispatchCount;
        internal int CreateCallCount => Volatile.Read(ref _createCallCount);
        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );
        internal int AgentDispatchCount => Volatile.Read(
            ref _agentDispatchCount
        );
        internal int RecapDispatchCount => Volatile.Read(
            ref _recapDispatchCount
        );

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Interlocked.Increment(ref _createCallCount);
            return new OperatorChainCompletionClient(this, connection.Id);
        }

        private sealed class OperatorChainCompletionClient(
            OperatorChainCompletionFactory owner,
            string connectionId
        ) : ICompletionClient {
            public string Name => "operator-chain-" + connectionId;
            public string ApiSpecId => "openai-chat-v1";

            public Task<CompletionResult> StreamCompletionAsync(
                CompletionRequest request,
                CompletionStreamObserver? observer,
                CancellationToken cancellationToken = default
            ) {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref owner._dispatchCallCount);
                bool recap = request.PromptPrefix.OutputContract.Tools.Any(
                    static tool => string.Equals(
                        tool.Name,
                        RecapRewriterProtocolV1.TerminalToolName,
                        StringComparison.Ordinal
                    )
                );
                if (recap) {
                    Interlocked.Increment(ref owner._recapDispatchCount);
                    return Task.FromResult(new CompletionResult(
                        new ActionMessage([new ActionBlock.ToolCall(
                            new RawToolCall(
                                RecapRewriterProtocolV1.TerminalToolName,
                                "operator-chain-recap",
                                "{\"outcome\":\"updated\","
                                    + "\"content\":\"operator-chain recap\"}"
                            )
                        )]),
                        new CompletionDescriptor(
                            Name,
                            ApiSpecId,
                            request.ModelId
                        )
                    ));
                }
                Interlocked.Increment(ref owner._agentDispatchCount);
                observer?.OnTextDelta("operator-chain agent");
                return Task.FromResult(new CompletionResult(
                    new ActionMessage([
                        new ActionBlock.Text("operator-chain agent")
                    ]),
                    new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
                ));
            }
        }
    }
}
