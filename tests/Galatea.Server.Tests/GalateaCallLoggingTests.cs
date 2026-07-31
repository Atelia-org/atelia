using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaCallLoggingTests {
    private static readonly TimeSpan OperationDeadline =
        TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConfigResolvesRelativeCallLogDirAndRejectsRepoNesting() {
        string repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-call-log-config-tests",
            Guid.NewGuid().ToString("N")
        );
        string sessionDirectory = Path.Combine(
            repositoryRoot,
            "session"
        );
        Directory.CreateDirectory(repositoryRoot);
        using (SessionJournalEngine.Create(
                   sessionDirectory,
                   new SessionCreateOptions(
                       "model-a",
                       "prompt-a",
                       "surface-a"
                   )
               )) {
        }
        try {
            var factory = new RecordingCompletionClientFactory();
            GalateaTestHost relative = GalateaTestHost.OpenExisting(
                sessionDirectory,
                [Connection("test", "model-a", "surface-a")],
                "test",
                factory,
                DisabledGalateaUserMessageNormalizer.Instance,
                callLogDirectory: "call-logs"
            );
            string ownedConfigRoot = relative.RootDirectory;
            try {
                GalateaConfig config = GalateaConfigLoader.Load(
                    relative.ConfigPath
                );
                Assert.Equal(
                    Path.Combine(ownedConfigRoot, "call-logs"),
                    config.CallLogDir
                );
            }
            finally {
                await relative.DisposeAsync();
            }
            Assert.False(Directory.Exists(ownedConfigRoot));
            Assert.True(Directory.Exists(sessionDirectory));

            await using GalateaTestHost nested =
                GalateaTestHost.OpenExisting(
                    sessionDirectory,
                    [Connection("test", "model-a", "surface-a")],
                    "test",
                    factory,
                    DisabledGalateaUserMessageNormalizer.Instance,
                    callLogDirectory: Path.Combine(
                        sessionDirectory,
                        "call-logs"
                    )
                );
            InvalidOperationException exception = Assert.Throws<
                InvalidOperationException
            >(() => GalateaConfigLoader.Load(nested.ConfigPath));
            Assert.Contains(
                "must be disjoint",
                exception.Message,
                StringComparison.Ordinal
            );
            Assert.False(Directory.Exists(
                Path.Combine(sessionDirectory, "call-logs")
            ));
        }
        finally {
            if (Directory.Exists(repositoryRoot)) {
                Directory.Delete(repositoryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EnabledLoggingWritesAgentAndMaintainerLogsOutsideRepoWithoutIdentityDrift() {
        string callLogDirectory = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-call-log-tests",
            Guid.NewGuid().ToString("N")
        );
        var factory = new RecordingCompletionClientFactory();
        try {
            await using var host = GalateaTestHost.Create(
                factory,
                DisabledGalateaUserMessageNormalizer.Instance,
                callLogDirectory: callLogDirectory
            );
            using HttpClient client = host.CreateClient();
            await LoginAsync(client);
            GalateaHostService service = host.Factory.Services
                .GetRequiredService<GalateaHostService>();
            UserSessionHost session = await service.GetSessionAsync(
                "alice",
                CancellationToken.None
            );

            await CompleteTurnAsync(
                client,
                service,
                session,
                "call log probe"
            );

            string agentLog = Assert.Single(
                Directory.EnumerateFiles(
                    Path.Combine(callLogDirectory, "agent"),
                    "*.json"
                )
            );
            AssertLogContext(agentLog, "galatea/agent", null);

            CompletionConnectionConfig connection = host.Factory
                .Services.GetRequiredService<GalateaConfig>()
                .Connections.Single();
            ICompletionClient inner = factory.Client;
            ICompletionClient agent =
                GalateaCompletionLogging.CreateAgentClient(
                    inner,
                    connection,
                    callLogDirectory
                );
            Assert.Equal(
                CompletionDispatchIdentityFactory.Create(
                    connection,
                    inner
                ),
                CompletionDispatchIdentityFactory.Create(
                    connection,
                    agent
                )
            );

            RecapMaintainerProfileDescriptor descriptor =
                RecapMaintainerProfileCatalog.BuiltIn.All.First();
            ICompletionClient maintainer =
                GalateaCompletionLogging.CreateMaintainerClient(
                    inner,
                    connection,
                    callLogDirectory,
                    descriptor
                );
            _ = await maintainer.StreamCompletionAsync(
                new CompletionRequest(
                    connection.ModelId,
                    "maintainer test",
                    [new ObservationMessage("fixture")],
                    []
                ),
                observer: null
            );
            string maintainerLog = Assert.Single(
                Directory.EnumerateFiles(
                    Path.Combine(callLogDirectory, "maintenance"),
                    "*.json",
                    SearchOption.AllDirectories
                )
            );
            AssertLogContext(
                maintainerLog,
                "galatea/maintenance",
                descriptor.MaintainerId
            );
            Assert.DoesNotContain(
                Directory.EnumerateFiles(
                    host.SessionDirectory,
                    "*.json",
                    SearchOption.AllDirectories
                ),
                static path => File.ReadAllText(path).Contains(
                    "atelia.completion.call-log.v1",
                    StringComparison.Ordinal
                )
            );
        }
        finally {
            if (Directory.Exists(callLogDirectory)) {
                Directory.Delete(callLogDirectory, recursive: true);
            }
        }
    }

    private static CompletionConnectionConfig Connection(
        string id,
        string modelId,
        string surfaceId
    ) => new(
        id,
        "openai-chat",
        modelId,
        surfaceId,
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static async Task LoginAsync(HttpClient client) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task CompleteTurnAsync(
        HttpClient client,
        GalateaHostService service,
        UserSessionHost session,
        string message
    ) {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/chat/turns",
            new ChatStreamRequest(message, ConnectionId: "test")
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        Assert.NotNull(started);
        GalateaLiveTurn turn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started!.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(turn.RunTask)
            .WaitAsync(OperationDeadline);
        Assert.Equal("completed", turn.Status);
    }

    private static void AssertLogContext(
        string path,
        string expectedCommand,
        string? expectedMaintainerId
    ) {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path)
        );
        Assert.Equal(
            "atelia.completion.call-log.v1",
            document.RootElement.GetProperty("schema").GetString()
        );
        JsonElement context =
            document.RootElement.GetProperty("context");
        Assert.Equal(
            expectedCommand,
            context.GetProperty("command").GetString()
        );
        if (expectedMaintainerId is not null) {
            Assert.Equal(
                expectedMaintainerId,
                context.GetProperty("maintainerId").GetString()
            );
        }
    }

    private sealed class RecordingCompletionClientFactory
        : ICompletionClientFactory {
        internal RecordingCompletionClient Client { get; } = new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            return Client;
        }
    }

    private sealed class RecordingCompletionClient
        : ICompletionClient {
        public string Name => "galatea-call-log-test";

        public string ApiSpecId => "openai-chat-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            const string reply = "logged answer";
            observer?.OnTextDelta(reply);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(reply)]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }
}
