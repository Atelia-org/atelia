using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaFreshReadinessVerticalTests {
    private static readonly TimeSpan CompletionDeadline =
        TimeSpan.FromSeconds(10);

    [Fact]
    public async Task MissingPlannerConfig_BlocksBeforeNormalizerClientAndObservation() {
        var factory = new TrackingFactory(
            CompletionTermination.Completed()
        );
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            factory,
            normalizer
        );
        File.Delete(RecapPlannerConfigLoader.GetCanonicalPath(
            host.SessionDirectory
        ));
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);
        var initialHead = session.Engine.ReadCurrentHead();

        GalateaLiveTurn liveTurn = await StartAndAwaitAsync(
            client,
            service,
            session,
            "must remain unconsumed"
        );

        Assert.Equal("failed", liveTurn.Status);
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.Client.DispatchCallCount);
        Assert.Equal(initialHead, session.Engine.ReadCurrentHead());
        Assert.Empty(
            session.Engine.ReadRecentCompletedTurns().Turns
        );
    }

    [Fact]
    public async Task BeyondPrefixFailsClosedBeforeProviderMaintainerLogOrBuildingMutation() {
        string callLogDirectory = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-beyond-prefix-logs",
            Guid.NewGuid().ToString("N")
        );
        var factory = new TrackingFactory(
            CompletionTermination.Completed()
        );
        var normalizer = new TrackingNormalizer();
        try {
            await using var host = GalateaTestHost.Create(
                factory,
                normalizer,
                callLogDirectory: callLogDirectory
            );
            EventAddress buildingAnchor;
            EventAddress expectedHead;
            IReadOnlyDictionary<string, string> derivedBefore;
            using (SessionJournalEngine engine =
                   SessionJournalEngine.Open(host.SessionDirectory)) {
                buildingAnchor = engine.AppendSystemPromptSetup(
                    "test system prompt"
                );
                SessionHistoryPlanningWindow window =
                    engine.ReadHistoryPlanningWindow();
                RecapMaintainerProfileDescriptor profile =
                    RecapMaintainerProfileCatalog.BuiltIn.Resolve(
                        RecapMaintainerProfileCatalog
                            .WorldUnderstandingRewrite
                    );
                SessionContextAnchorSetupReferences anchorSetups =
                    engine.ResolveContextAnchorSetupReferences(
                        buildingAnchor
                    );
                var plan = new MaintainRecapBlockPlan(
                    new RecapBlockId(profile.RecapBlockIdValue),
                    profile.Target,
                    profile.MaintainerId,
                    profile.CapabilityFingerprint,
                    new EmptyRecapMaintainSource(
                        window.StartExclusive,
                        window.StartSetups
                    ),
                    [new RecapReplayBoundary(
                        buildingAnchor,
                        anchorSetups
                    )],
                    EmptyRecapPriorContext.Instance
                );
                DerivedRecapStore store = DerivedRecapStore.Open(
                    host.SessionDirectory,
                    engine.BranchRefId
                );
                Assert.IsType<CreateBuildingResult.Created>(
                    await new DerivedRecapBuildingInstaller(
                        store,
                        engine.ReadView
                    ).InstallAsync(
                        DerivedRecapCodec.CreateManifest(
                            engine.BranchRefId,
                            buildingAnchor,
                            anchorSetups,
                            [plan]
                        ),
                        buildingAnchor
                    )
                );

                for (int index = 0; index < 514; index++) {
                    _ = engine.AppendSystemPromptSetup(
                        $"bounded-prefix-padding-{index}"
                    );
                }
                _ = engine.AppendSystemPromptSetup(
                    "test system prompt"
                );
                expectedHead = Assert.IsType<EventAddress>(
                    engine.ReadCurrentHead()
                );
                var aligned = Assert.IsType<
                    SessionDesiredSetupReconciliationResult.Ready
                >(engine.ReconcileDesiredSetup(
                    expectedHead,
                    new SessionDesiredSetup(
                        "model-a",
                        "openai-chat/strict",
                        "test system prompt"
                    )
                ));
                Assert.False(aligned.RuntimeConfigChanged);
                Assert.False(aligned.SystemPromptChanged);
                Assert.IsType<
                    SessionCurrentLineageAnchorLookup.BeyondPrefix
                >(engine.ReadView
                    .ReadCurrentLineagePrefix(513)
                    .Lookup(buildingAnchor));
                Assert.Equal(expectedHead, engine.ReadCurrentHead());
                derivedBefore = SnapshotDerivedFiles(
                    host.SessionDirectory
                );
            }

            using HttpClient client = host.CreateClient();
            GalateaHostService service = host.Factory.Services
                .GetRequiredService<GalateaHostService>();
            UserSessionHost session = await service.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "must remain unconsumed",
                new GalateaTurnOptions("test")
            );

            GalateaTurnException failure = await Assert.ThrowsAsync<
                GalateaTurnException
            >(() => service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            ));

            Assert.Equal("recap-beyond-prefix", failure.FailureReason);
            Assert.Contains("stage=PreparationBuildingAdmission", failure.Message);
            Assert.Contains(
                EventAddressTextCodec.Format(buildingAnchor),
                failure.Message
            );
            Assert.Equal(0, normalizer.ShouldNormalizeCallCount);
            Assert.Equal(0, normalizer.NormalizeCallCount);
            Assert.Equal(0, factory.CreateCallCount);
            Assert.Equal(0, factory.Client.DispatchCallCount);
            Assert.False(Directory.Exists(callLogDirectory));
            Assert.Equal(expectedHead, session.Engine.ReadCurrentHead());
            Assert.Equal(
                derivedBefore.OrderBy(static item => item.Key),
                SnapshotDerivedFiles(host.SessionDirectory)
                    .OrderBy(static item => item.Key)
            );
        }
        finally {
            if (Directory.Exists(callLogDirectory)) {
                Directory.Delete(callLogDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task KnownCompletionFailure_IsExactlyAbandonedBeforeIdlePromise() {
        var factory = new TrackingFactory(
            CompletionTermination.Incomplete(
                "observer-stopped",
                "Streaming observer stopped by test."
            )
        );
        await using var host = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);
        (GalateaHostService service, UserSessionHost session) =
            await GetSessionAsync(host);
        var initialHead = session.Engine.ReadCurrentHead();

        GalateaLiveTurn liveTurn = await StartAndAwaitAsync(
            client,
            service,
            session,
            "known failure"
        );

        Assert.Equal("failed", liveTurn.Status);
        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(1, factory.Client.DispatchCallCount);
        Assert.Equal(initialHead, session.Engine.ReadCurrentHead());
        Assert.Equal(
            SessionExecutionPhase.Idle,
            session.Engine.InspectExecutionBoundary().Phase
        );
        Assert.Empty(
            session.Engine.ReadRecentCompletedTurns().Turns
        );
    }

    private static async Task<GalateaLiveTurn> StartAndAwaitAsync(
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
            .WaitAsync(CompletionDeadline);
        return turn;
    }

    private static async Task LoginAsync(HttpClient client) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static IReadOnlyDictionary<string, string>
        SnapshotDerivedFiles(string sessionDirectory) {
        string root = Path.Combine(
            sessionDirectory,
            "derived",
            "recap",
            "v4"
        );
        return Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories
            )
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToHexStringLower(
                    SHA256.HashData(File.ReadAllBytes(path))
                ),
                StringComparer.Ordinal
            );
    }

    private static async Task<(
        GalateaHostService Service,
        UserSessionHost Session
    )> GetSessionAsync(GalateaTestHost host) {
        var service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        return (service, session);
    }

    private sealed class TrackingNormalizer
        : IGalateaUserMessageNormalizer {
        private int _shouldNormalizeCallCount;
        private int _normalizeCallCount;

        internal int ShouldNormalizeCallCount => Volatile.Read(
            ref _shouldNormalizeCallCount
        );

        internal int NormalizeCallCount => Volatile.Read(
            ref _normalizeCallCount
        );

        public bool ShouldNormalize(string userMessage) {
            _ = userMessage;
            Interlocked.Increment(ref _shouldNormalizeCallCount);
            return true;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _normalizeCallCount);
            return ValueTask.FromResult(userMessage);
        }
    }

    private sealed class TrackingFactory(
        CompletionTermination termination
    ) : ICompletionClientFactory {
        private int _createCallCount;

        internal TrackingClient Client { get; } = new(termination);

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

    private sealed class TrackingClient(
        CompletionTermination termination
    ) : ICompletionClient {
        private int _dispatchCallCount;

        public string Name => "galatea-fresh-readiness-test";

        public string ApiSpecId => "openai-chat-v1";

        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text("scripted response")
                ]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                ),
                termination: termination
            ));
        }
    }
}
