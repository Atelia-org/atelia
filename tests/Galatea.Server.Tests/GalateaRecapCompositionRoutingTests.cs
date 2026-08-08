using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRecapCompositionRoutingTests {
    [Fact]
    public async Task ExplicitRoutes_NoBuildDoesNotCreateMaintenanceClientsOrLogs() {
        string callLogDirectory = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-recap-no-build-logs",
            Guid.NewGuid().ToString("N")
        );
        var factory = new RecordingFactory();
        try {
            await using var host = GalateaTestHost.Create(
                factory,
                DisabledGalateaUserMessageNormalizer.Instance,
                callLogDirectory: callLogDirectory,
                recapMaintainerConnections: [
                    new(
                        WorldUnderstandingRecapMaintainers.MaintainerId,
                        "test"
                    ),
                    new(
                        AutobiographicalRecapMaintainers.MaintainerId,
                        "test"
                    )
                ]
            );
            using HttpClient client = host.CreateClient();
            using HttpResponseMessage login =
                await GalateaTestHost.LoginAsync(client);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/chat/turns",
                new ChatStreamRequest("no-build route probe", "test")
            );
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            StartTurnResponseDto? started = await response.Content
                .ReadFromJsonAsync<StartTurnResponseDto>();
            Assert.NotNull(started);
            GalateaHostService service = host.Factory.Services
                .GetRequiredService<GalateaHostService>();
            UserSessionHost session = await service.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            GalateaLiveTurn turn = Assert.IsType<GalateaLiveTurn>(
                service.FindTurn(session, started!.TurnId)
            );
            await Assert.IsAssignableFrom<Task>(turn.RunTask)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("completed", turn.Status);
            Assert.Equal(1, factory.CreateCallCount);
            Assert.Single(Directory.EnumerateFiles(
                Path.Combine(callLogDirectory, "agent"),
                "*.json"
            ));
            Assert.False(Directory.Exists(Path.Combine(
                callLogDirectory,
                "maintenance"
            )));
        }
        finally {
            if (Directory.Exists(callLogDirectory)) {
                Directory.Delete(callLogDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExplicitRoutes_CreateDistinctMaintainerClientsAndAccurateLogsLazily() {
        string callLogDirectory = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-recap-routing-logs",
            Guid.NewGuid().ToString("N")
        );
        var factory = new RecordingFactory();
        using var connections = CreateRegistry(factory);
        CompletionConnectionConfig agentConnection = Get(
            connections,
            "agent"
        );
        IReadOnlyDictionary<string, string> routes =
            new Dictionary<string, string>(StringComparer.Ordinal) {
                [WorldUnderstandingRecapMaintainers.MaintainerId] =
                    "world",
                [AutobiographicalRecapMaintainers.MaintainerId] =
                    "autobiography"
            };
        var lanes = new RecapExecutionLaneInterner();
        var groups = new RecapRuntimeGroupInterner();
        IRecapBlockMaintainerRegistry maintainers =
            GalateaRecapComposition.CreateMaintainerRegistry(
                RecapMaintainerProfileCatalog.BuiltIn,
                connections,
                agentConnection,
                routes,
                callLogDirectory,
                lanes,
                groups
            );
        try {
            Assert.Equal(0, factory.CreateCallCount);
            Assert.False(Directory.Exists(callLogDirectory));

            BoundRecapBlockMaintainer world = Resolve(
                maintainers,
                RecapMaintainerProfileCatalog.WorldUnderstandingRewrite
            );
            BoundRecapBlockMaintainer autobiography = Resolve(
                maintainers,
                RecapMaintainerProfileCatalog.AutobiographicalRewrite
            );

            Assert.Equal(2, factory.CreateCallCount);
            Assert.Equal(
                "world-model",
                world.RuntimeGroup.Lane.ModelId
            );
            Assert.Equal(
                "autobiography-model",
                autobiography.RuntimeGroup.Lane.ModelId
            );
            Assert.NotSame(
                world.RuntimeGroup.Lane,
                autobiography.RuntimeGroup.Lane
            );
            Assert.NotSame(world.RuntimeGroup, autobiography.RuntimeGroup);
            Assert.Same(
                world.Definition.Family,
                autobiography.Definition.Family
            );
            Assert.Equal(
                "client/world",
                world.RuntimeGroup.Lane.RawClient.Name
            );
            Assert.Equal(
                "client/autobiography",
                autobiography.RuntimeGroup.Lane.RawClient.Name
            );

            _ = await world.MaintainAsync(
                CreateRequest(world.Target),
                CancellationToken.None
            );

            string logPath = Assert.Single(
                Directory.EnumerateFiles(
                    callLogDirectory,
                    "*.json",
                    SearchOption.AllDirectories
                )
            );
            using JsonDocument log = JsonDocument.Parse(
                File.ReadAllText(logPath)
            );
            JsonElement root = log.RootElement;
            Assert.Equal(
                "world",
                root.GetProperty("connection")
                    .GetProperty("id")
                    .GetString()
            );
            Assert.Equal(
                "world-model",
                root.GetProperty("connection")
                    .GetProperty("modelId")
                    .GetString()
            );
            Assert.Equal(
                WorldUnderstandingRecapMaintainers.MaintainerId,
                root.GetProperty("context")
                    .GetProperty("maintainerId")
                    .GetString()
            );
            Assert.Equal(
                "noReuseExpected",
                root.GetProperty("invocationOptions")
                    .GetProperty("promptCacheReuseHint")
                    .GetString()
            );
        }
        finally {
            if (Directory.Exists(callLogDirectory)) {
                Directory.Delete(callLogDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void MissingRoutes_BuiltInsShareSelectedAgentLaneAndGroup() {
        var factory = new RecordingFactory();
        using var connections = CreateRegistry(factory);
        CompletionConnectionConfig agentConnection = Get(
            connections,
            "agent"
        );
        var lanes = new RecapExecutionLaneInterner();
        var groups = new RecapRuntimeGroupInterner();
        IRecapBlockMaintainerRegistry firstOperation =
            GalateaRecapComposition.CreateMaintainerRegistry(
                RecapMaintainerProfileCatalog.BuiltIn,
                connections,
                agentConnection,
                recapMaintainerConnections: null,
                callLogDirectory: null,
                lanes,
                groups
            );
        IRecapBlockMaintainerRegistry secondOperation =
            GalateaRecapComposition.CreateMaintainerRegistry(
                RecapMaintainerProfileCatalog.BuiltIn,
                connections,
                agentConnection,
                recapMaintainerConnections: null,
                callLogDirectory: null,
                lanes,
                groups
            );

        Assert.Equal(0, factory.CreateCallCount);

        BoundRecapBlockMaintainer world = Resolve(
            firstOperation,
            RecapMaintainerProfileCatalog.WorldUnderstandingRewrite
        );
        BoundRecapBlockMaintainer autobiography = Resolve(
            secondOperation,
            RecapMaintainerProfileCatalog.AutobiographicalRewrite
        );

        Assert.Equal(1, factory.CreateCallCount);
        Assert.Same(world.RuntimeGroup, autobiography.RuntimeGroup);
        Assert.Same(
            world.RuntimeGroup.Lane,
            autobiography.RuntimeGroup.Lane
        );
        Assert.Same(
            world.RuntimeGroup.Lane.RawClient,
            autobiography.RuntimeGroup.Lane.RawClient
        );
        Assert.Equal(
            "agent-model",
            world.RuntimeGroup.Lane.ModelId
        );
        Assert.Equal(
            "client/agent",
            world.RuntimeGroup.Lane.RawClient.Name
        );
    }

    [Fact]
    public void ExplicitRoute_UsesExactLookupWithoutDefaultFallback() {
        var factory = new RecordingFactory();
        using var connections = CreateRegistry(factory);
        CompletionConnectionConfig agentConnection = Get(
            connections,
            "agent"
        );
        IReadOnlyDictionary<string, string> routes =
            new Dictionary<string, string>(StringComparer.Ordinal) {
                [WorldUnderstandingRecapMaintainers.MaintainerId] =
                    "not-registered",
                [AutobiographicalRecapMaintainers.MaintainerId] =
                    "autobiography"
            };
        var lanes = new RecapExecutionLaneInterner();
        var groups = new RecapRuntimeGroupInterner();
        IRecapBlockMaintainerRegistry maintainers =
            GalateaRecapComposition.CreateMaintainerRegistry(
                RecapMaintainerProfileCatalog.BuiltIn,
                connections,
                agentConnection,
                routes,
                callLogDirectory: null,
                lanes,
                groups
            );

        InvalidOperationException failure = Assert.Throws<
            InvalidOperationException
        >(() => Resolve(
            maintainers,
            RecapMaintainerProfileCatalog.WorldUnderstandingRewrite
        ));

        Assert.Contains("unknown connection 'not-registered'", failure.Message);
        Assert.Equal(0, factory.CreateCallCount);
    }

    private static BoundRecapBlockMaintainer Resolve(
        IRecapBlockMaintainerRegistry registry,
        string profileName
    ) {
        RecapMaintainerProfileDescriptor descriptor =
            RecapMaintainerProfileCatalog.BuiltIn.Resolve(profileName);
        Assert.True(registry.TryResolve(
            descriptor.MaintainerId,
            descriptor.Target,
            descriptor.CapabilityFingerprint,
            out IRecapBlockMaintainer? maintainer
        ));
        return Assert.IsType<BoundRecapBlockMaintainer>(maintainer);
    }

    private static RecapMaintenanceEpochInput CreateRequest(
        ContextHeaderBlockPath target
    ) {
        var context = new ContextHeaderPack();
        var block = new ContextHeaderBlock("old recap");
        switch (target.Carrier) {
            case ContextHeaderCarrier.Observation:
                context.Observation.Add(target.BlockKey, block);
                break;
            case ContextHeaderCarrier.Action:
                context.Action.Add(target.BlockKey, block);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
        return new RecapMaintenanceEpochInput(
            context.Render(),
            [new ObservationMessage("new history")]
        );
    }

    private static CompletionConnectionRegistry CreateRegistry(
        RecordingFactory factory
    ) => new(
        new CompletionConnectionsFileConfig(
            [
                Connection("agent", "agent-model"),
                Connection("world", "world-model"),
                Connection("autobiography", "autobiography-model")
            ],
            "agent"
        ),
        factory
    );

    private static CompletionConnectionConfig Get(
        CompletionConnectionRegistry registry,
        string connectionId
    ) {
        Assert.True(registry.TryGet(
            connectionId,
            out CompletionConnectionConfig? connection
        ));
        return connection;
    }

    private static CompletionConnectionConfig Connection(
        string id,
        string modelId
    ) => new(
        id,
        "openai-chat",
        modelId,
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private sealed class RecordingFactory : ICompletionClientFactory {
        private int _createCallCount;

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            Interlocked.Increment(ref _createCallCount);
            return new RoutingClient(connection.Id);
        }
    }

    private sealed class RoutingClient(string connectionId)
        : ICompletionClient {
        public string Name { get; } = $"client/{connectionId}";

        public string ApiSpecId => "openai-chat-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => Complete(request, observer, cancellationToken);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            invocationOptions.Validate();
            return Complete(request, observer, cancellationToken);
        }

        private Task<CompletionResult> Complete(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.PromptPrefix.OutputContract.Tools.Any(
                    static tool => string.Equals(
                        tool.Name,
                        StructuredRecapMaintainerOutputProtocol
                            .SubmitToolName,
                        StringComparison.Ordinal
                    )
                )) {
                const string text = "agent response";
                observer?.OnTextDelta(text);
                return Task.FromResult(new CompletionResult(
                    new ActionMessage([new ActionBlock.Text(text)]),
                    CompletionDescriptor.From(this, request)
                ));
            }
            var call = new RawToolCall(
                StructuredRecapMaintainerOutputProtocol.SubmitToolName,
                "call-1",
                "{\"outcome\":\"updated\",\"content\":\"rewritten recap\"}"
            );
            observer?.OnToolCall(call);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.ToolCall(call)]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }
}
