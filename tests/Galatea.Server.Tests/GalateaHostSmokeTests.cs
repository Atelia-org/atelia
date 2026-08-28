using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaHostSmokeTests {
    [Fact]
    public async Task RecapCadenceProgressEndpoint_FirstGetUsesExistingCreateIfMissingPolicy() {
        var completionFactory = new TrackingCompletionClientFactory();
        await using var host = GalateaTestHost.CreateMissingSession(
            completionFactory,
            DisabledGalateaUserMessageNormalizer.Instance
        );
        Assert.False(Directory.Exists(host.SessionDirectory));
        using HttpClient client = host.CreateClient();
        _ = await GalateaTestHost.LoginAsync(client);
        Assert.False(Directory.Exists(host.SessionDirectory));

        RecapCadenceProgressSnapshotDto? progress = await client
            .GetFromJsonAsync<RecapCadenceProgressSnapshotDto>(
                "/api/v1/recap-cadence-progress"
            );

        Assert.NotNull(progress);
        Assert.True(Directory.Exists(host.SessionDirectory));
        Assert.Equal("exact", progress!.Freshness);
        Assert.Equal("below-target", progress.State);
        Assert.NotNull(progress.ObservedRawHead);
        Assert.NotNull(progress.CadenceBaseline);
        Assert.NotNull(progress.RecentHistoryLoad);
        Assert.NotNull(progress.RecapIntervalHistoryLoad);
        Assert.NotNull(progress.MinimumRecentHistoryLoad);
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);
    }

    [Fact]
    public async Task RecapCadenceProgressEndpoint_ReturnsExactClosedTelemetryWithoutProviderWork() {
        var completionFactory = new TrackingCompletionClientFactory();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            DisabledGalateaUserMessageNormalizer.Instance
        );
        using HttpClient client = host.CreateClient();
        using HttpResponseMessage anonymous = await client.GetAsync(
            "/api/v1/recap-cadence-progress"
        );
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        _ = await GalateaTestHost.LoginAsync(client);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        string expectedHead = EventAddressTextCodec.Format(
            Assert.IsType<Atelia.EventJournal.EventAddress>(
                session.Engine.ReadCurrentHead()
            )
        );

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/recap-cadence-progress"
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            [
                "buildThresholdHistoryLoad",
                "cadenceBaseline",
                "code",
                "detail",
                "freshness",
                "historyLoadEstimatorId",
                "minimumRecentHistoryLoad",
                "observedRawHead",
                "recapIntervalHistoryLoad",
                "recentHistoryLoad",
                "recentHistoryPlanningUnitCount",
                "remainingHistoryLoad",
                "state"
            ],
            document.RootElement.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
        );
        RecapCadenceProgressSnapshotDto progress = Assert.IsType<
            RecapCadenceProgressSnapshotDto
        >(JsonSerializer.Deserialize<RecapCadenceProgressSnapshotDto>(
            json,
            GalateaJson.Options
        ));
        Assert.Equal("exact", progress.Freshness);
        Assert.Contains(
            progress.State,
            new[] {
                "below-target",
                "awaiting-replay-safe-boundary",
                "awaiting-recent-reserve",
                "cadence-ready"
            }
        );
        Assert.Equal(expectedHead, progress.ObservedRawHead);
        Assert.NotNull(progress.CadenceBaseline);
        Assert.NotNull(progress.RecentHistoryPlanningUnitCount);
        Assert.NotNull(progress.RecentHistoryLoad);
        Assert.Equal("1", progress.RecapIntervalHistoryLoad);
        Assert.Equal("1", progress.MinimumRecentHistoryLoad);
        Assert.NotNull(progress.BuildThresholdHistoryLoad);
        Assert.NotNull(progress.RemainingHistoryLoad);
        Assert.Equal(
            "atelia.history-load.o200k-base.history-unit-v1",
            progress.HistoryLoadEstimatorId
        );
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);
        Assert.False(File.Exists(Path.Combine(
            host.SessionDirectory,
            "derived",
            "recap-grid",
            "v1",
            "grid.sqlite"
        )));

        using JsonDocument recent = JsonDocument.Parse(
            await client.GetStringAsync("/api/v1/recent-turns")
        );
        Assert.Equal(
            [
                "contextHeader",
                "recapGridReadiness",
                "rewindLatestToken",
                "turns"
            ],
            recent.RootElement.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public async Task ActualProgram_UsesAuthenticationAndInjectedServices() {
        var completionFactory = new TrackingCompletionClientFactory();
        var normalizer = new TrackingNormalizer();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            normalizer,
            provisionRawOnly: false
        );
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage anonymous = await client.GetAsync(
            "/api/v1/me"
        );
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(
            client
        );
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);

        GalateaMeDto? me = await client.GetFromJsonAsync<GalateaMeDto>(
            "/api/v1/me"
        );
        Assert.NotNull(me);
        Assert.Equal("alice", me!.UserId);
        Assert.False(me.MaintenanceMode);

        Assert.Same(
            completionFactory,
            host.Factory.Services
                .GetRequiredService<ICompletionClientFactory>()
        );
        Assert.IsNotType<GalateaUserMessageNormalizerFactory>(
            host.Factory.Services.GetRequiredService<
                IGalateaUserMessageNormalizerFactory>()
        );

        RecentTurnsResponseDto? recent = await client
            .GetFromJsonAsync<RecentTurnsResponseDto>("/api/v1/recent-turns");
        Assert.NotNull(recent);
        Assert.Empty(recent!.Turns);
        Assert.Equal(ContextHeaderDto.Empty, recent.ContextHeader);
        RecapGridReadinessSnapshotDto recap = Assert.IsType<
            RecapGridReadinessSnapshotDto
        >(recent.RecapGridReadiness);
        Assert.Equal("exact", recap.Freshness);
        Assert.Equal("unprovisioned", recap.State);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Assert.Equal(
            EventAddressTextCodec.Format(
                Assert.IsType<Atelia.EventJournal.EventAddress>(
                    session.Engine.ReadCurrentHead()
                )
            ),
            recap.ObservedRawHead
        );
        Assert.Equal(0, completionFactory.CreateCallCount);
        Assert.Equal(0, completionFactory.Client.DispatchCallCount);
        Assert.Equal(0, normalizer.NormalizeCallCount);
    }

    [Fact]
    public async Task RecapGridUnprovisioned_DoesNotSuppressRecentTurns() {
        var completionFactory = new TrackingCompletionClientFactory();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            DisabledGalateaUserMessageNormalizer.Instance,
            provisionRawOnly: false
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        _ = session.Engine.AppendObservation(
            GalateaUserMessageEnvelope.Wrap("visible user")
        );
        _ = session.Engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("visible assistant")
            ]),
            new CompletionDescriptor(
                "planner-unavailable-fixture",
                "fixture-v1",
                "model-a"
            )
        );
        using HttpClient client = host.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(
            client
        );
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/recent-turns"
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RecentTurnsResponseDto? recent = await response.Content
            .ReadFromJsonAsync<RecentTurnsResponseDto>();
        Assert.NotNull(recent);
        RecentTurnDto turn = Assert.Single(recent!.Turns);
        Assert.Equal("visible user", turn.UserText);
        Assert.Equal("visible assistant", turn.Assistant.Text);
        Assert.Equal(ContextHeaderDto.Empty, recent.ContextHeader);
        Assert.NotNull(recent.RewindLatestToken);
        RecapGridReadinessSnapshotDto recap = Assert.IsType<
            RecapGridReadinessSnapshotDto
        >(recent.RecapGridReadiness);
        Assert.Equal("exact", recap.Freshness);
        Assert.Equal("unprovisioned", recap.State);
        Assert.Equal(0, completionFactory.CreateCallCount);
    }

    [Fact]
    public async Task ActualProgram_FreshRequestDoesNotInjectAgentControl() {
        var completionFactory = new CapturingCompletionClientFactory();
        await using var host = GalateaTestHost.Create(
            completionFactory,
            DisabledGalateaUserMessageNormalizer.Instance
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "no control tool",
            new GalateaTurnOptions("test")
        );

        await service.RunTurnAsync(session, turn, CancellationToken.None);
        service.FinishTurn(session, turn);

        Assert.Equal("completed", turn.Status);
        CompletionRequest request = Assert.Single(
            completionFactory.Client.Requests
        );
        Assert.Empty(request.PromptPrefix.OutputContract.Tools);
    }

    private sealed class TrackingCompletionClientFactory
        : ICompletionClientFactory {
        private int _createCallCount;

        public TrackingCompletionClient Client { get; } = new();

        public int CreateCallCount => Volatile.Read(
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

    private sealed class CapturingCompletionClientFactory
        : ICompletionClientFactory {
        internal CapturingCompletionClient Client { get; } = new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            return Client;
        }
    }

    private sealed class CapturingCompletionClient : ICompletionClient {
        private readonly List<CompletionRequest> _requests = [];

        public string Name => "galatea-capturing-test";

        public string ApiSpecId => "openai-chat-v1";

        internal IReadOnlyList<CompletionRequest> Requests {
            get {
                lock (_requests) {
                    return _requests.ToArray();
                }
            }
        }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_requests) {
                _requests.Add(request);
            }
            observer?.OnTextDelta("captured");
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text("captured")]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }

    private sealed class TrackingCompletionClient : ICompletionClient {
        private int _dispatchCallCount;

        public string Name => "galatea-test";

        public string ApiSpecId => "openai-chat-v1";

        public int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            throw new InvalidOperationException(
                "Smoke test must not dispatch a completion request."
            );
        }
    }

    private sealed class TrackingNormalizer
        : IGalateaUserMessageNormalizer {
        private int _normalizeCallCount;

        public int NormalizeCallCount => Volatile.Read(
            ref _normalizeCallCount
        );

        public bool ShouldNormalize(string userMessage) {
            _ = userMessage;
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
}
