using System.Net;
using System.Net.Http.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Planner;
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
        private int _normalizeCallCount;

        internal int NormalizeCallCount => Volatile.Read(
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
