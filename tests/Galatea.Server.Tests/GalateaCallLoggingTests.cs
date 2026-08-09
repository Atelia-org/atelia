using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

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
            AssertLogContext(
                agentLog,
                "galatea/agent",
                expectedMaintainerId: null,
                expectedPromptCacheReuseHint: "connectionDefault"
            );

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
            RecapExecutionLane lane =
                GalateaCompletionLogging.CreateMaintainerLane(
                    new RecapExecutionLaneInterner(),
                    inner,
                    connection,
                    callLogDirectory
                );
            Assert.Same(inner, lane.RawClient);
            Assert.Equal(connection.ModelId, lane.ModelId);
            BoundRecapBlockMaintainer maintainer =
                new RecapRuntimeGroupInterner().GetOrAdd(
                    lane,
                    descriptor.Definition.Family
                )
                .Bind(descriptor.Definition);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await MaintainAsync(
                    maintainer,
                    new RecapMaintenanceEpochInput(
                        ContextHeaderSnapshot.Empty,
                        [new ObservationMessage("fixture")]
                    )
                )
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
                descriptor.MaintainerId,
                "noReuseExpected"
            );
            Assert.DoesNotContain(
                Directory.EnumerateFiles(
                    host.SessionDirectory,
                    "*.json",
                    SearchOption.AllDirectories
                ),
                static path => File.ReadAllText(path).Contains(
                    "atelia.completion.call-log.",
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

    [Fact]
    public async Task ConfigRejectsSymlinkedCallLogAncestorBeforeCreatingAnythingInRepo() {
        if (!OperatingSystem.IsLinux()) {
            throw SkipException.ForSkip(
                "This focused call-log symlink gate runs on Linux."
            );
        }
        string root = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-call-log-symlink-tests",
            Guid.NewGuid().ToString("N")
        );
        string sessionDirectory = Path.Combine(root, "session");
        string targetInsideSession = Path.Combine(
            sessionDirectory,
            "call-log-target"
        );
        string alias = Path.Combine(root, "apparently-external");
        string escapedCallLogDirectory = Path.Combine(
            alias,
            "new-call-logs"
        );
        Directory.CreateDirectory(root);
        using (SessionJournalEngine.Create(
                   sessionDirectory,
                   new SessionCreateOptions(
                       "model-a",
                       "prompt-a",
                       "surface-a"
                   )
               )) {
        }
        Directory.CreateDirectory(targetInsideSession);
        try {
            try {
                Directory.CreateSymbolicLink(
                    alias,
                    targetInsideSession
                );
            }
            catch (Exception symlinkException) when (
                symlinkException is IOException
                    or NotSupportedException
                    or UnauthorizedAccessException
            ) {
                throw SkipException.ForSkip(
                    "Directory symbolic links are unavailable: "
                    + symlinkException.Message
                );
            }
            var factory = new RecordingCompletionClientFactory();
            await using GalateaTestHost host =
                GalateaTestHost.OpenExisting(
                    sessionDirectory,
                    [Connection("test", "model-a", "surface-a")],
                    "test",
                    factory,
                    DisabledGalateaUserMessageNormalizer.Instance,
                    callLogDirectory: escapedCallLogDirectory
                );

            InvalidOperationException validationException = Assert.Throws<
                InvalidOperationException
            >(() => GalateaConfigLoader.Load(host.ConfigPath));

            Assert.Contains(
                "symlink or reparse point",
                validationException.Message,
                StringComparison.Ordinal
            );
            Assert.Equal(0, factory.CreateCallCount);
            Assert.False(Directory.Exists(Path.Combine(
                targetInsideSession,
                "new-call-logs"
            )));
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                targetInsideSession
            ));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(
                    sessionDirectory,
                    "*.json",
                    SearchOption.AllDirectories
                ),
                static path => File.ReadAllText(path).Contains(
                    "atelia.completion.call-log.",
                    StringComparison.Ordinal
                )
            );
        }
        finally {
            if (Directory.Exists(alias)) {
                Directory.Delete(alias);
            }
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
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
        string? expectedMaintainerId,
        string expectedPromptCacheReuseHint
    ) {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path)
        );
        Assert.Equal(
            "atelia.completion.call-log.v7",
            document.RootElement.GetProperty("schema").GetString()
        );
        Assert.Equal(
            expectedPromptCacheReuseHint,
            document.RootElement
                .GetProperty("invocationOptions")
                .GetProperty("promptCacheReuseHint")
                .GetString()
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

    private static ValueTask<RecapMaintenanceSuccess> MaintainAsync(
        BoundRecapBlockMaintainer maintainer,
        RecapMaintenanceEpochInput input
    ) => maintainer.MaintainAsync(
        maintainer.CreateGroupExecution(input),
        new ImmediateCallControl(),
        CancellationToken.None
    );

    private sealed class ImmediateCallControl
        : IRecapMaintainerCallControl {
        public RecapMaintainerCallRole Role =>
            RecapMaintainerCallRole.Leader;

        public ValueTask WaitForDispatchPermissionAsync(
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void MarkDispatchStarted() {
        }

        public void MarkLaneAdmissionRequested() {
        }
    }

    private sealed class RecordingCompletionClientFactory
        : ICompletionClientFactory {
        private int _createCallCount;

        internal RecordingCompletionClient Client { get; } = new();

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            Interlocked.Increment(ref _createCallCount);
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
