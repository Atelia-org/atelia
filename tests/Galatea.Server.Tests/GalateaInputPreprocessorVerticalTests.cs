using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaInputPreprocessorVerticalTests {
    private static readonly TimeSpan CompletionDeadline =
        TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NormalizedInput_ReachesRequestPersistenceAndRecentDisplay() {
        var completion = new ScriptedCompletionClient("assistant reply");
        var normalizer = new ReturningNormalizer("normalized input");
        await using var host = GalateaTestHost.Create(
            new SingleClientFactory(completion),
            normalizer
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        var hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        var session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        StartTurnResponseDto started = await StartTurnAsync(
            client,
            "original input"
        );
        GalateaLiveTurn liveTurn = RequireTurn(
            hostService,
            session,
            started.TurnId
        );
        await RequireRunTask(liveTurn).WaitAsync(CompletionDeadline);

        Assert.Equal("completed", liveTurn.Status);
        Assert.Equal("original input", normalizer.ReceivedMessage);
        Assert.Equal(1, normalizer.NormalizeCallCount);
        Assert.Equal(1, completion.DispatchCallCount);

        string wrapped = GalateaUserMessageEnvelope.Wrap(
            "normalized input"
        );
        CompletionRequest request = Assert.IsType<CompletionRequest>(
            completion.LastRequest
        );
        ObservationMessage requestedObservation = Assert.Single(
            request.Context.OfType<ObservationMessage>()
        );
        Assert.Equal(wrapped, requestedObservation.Content);

        var persisted = session.Engine.ReadRecentCompletedTurns(1);
        Assert.Equal(
            wrapped,
            Assert.Single(persisted.Turns).ObservationContent
        );

        RecentTurnsResponseDto recent = await GetRecentTurnsAsync(client);
        RecentTurnDto recentTurn = Assert.Single(recent.Turns);
        Assert.Equal("normalized input", recentTurn.UserText);
        Assert.Equal("assistant reply", recentTurn.Assistant.Text);

        CurrentTurnDto current = await GetCurrentTurnAsync(client);
        Assert.Equal("idle", current.Status);
    }

    [Fact]
    public async Task StopDuringNormalization_CancelsBeforeDispatchAndPersistsNothing() {
        var completion = new ScriptedCompletionClient("must not dispatch");
        var normalizer = new BlockingNormalizer();
        await using var host = GalateaTestHost.Create(
            new SingleClientFactory(completion),
            normalizer
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        var hostService = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        var session = await hostService.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        var initialHead = session.Engine.ReadCurrentHead();

        StartTurnResponseDto started = await StartTurnAsync(
            client,
            "blocked input"
        );
        GalateaLiveTurn liveTurn = RequireTurn(
            hostService,
            session,
            started.TurnId
        );
        Task runTask = RequireRunTask(liveTurn);
        await normalizer.Entered.Task.WaitAsync(CompletionDeadline);

        using HttpResponseMessage stop = await client.PostAsync(
                $"/api/chat/turns/{started.TurnId}/stop",
                content: null
            )
            .WaitAsync(CompletionDeadline);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);

        await normalizer.CancellationObserved.Task
            .WaitAsync(CompletionDeadline);
        await runTask.WaitAsync(CompletionDeadline);

        Assert.True(normalizer.CapturedToken.IsCancellationRequested);
        Assert.Equal(0, completion.DispatchCallCount);
        Assert.Equal(initialHead, session.Engine.ReadCurrentHead());
        Assert.Empty(
            session.Engine.ReadRecentCompletedTurns().Turns
        );

        RecentTurnsResponseDto recent = await GetRecentTurnsAsync(client);
        Assert.Empty(recent.Turns);
        CurrentTurnDto current = await GetCurrentTurnAsync(client);
        Assert.Equal("idle", current.Status);

        Assert.Equal("failed", liveTurn.Status);
        using GalateaTurnSubscription subscription = liveTurn.Subscribe();
        StreamEventDto error = Assert.Single(
            subscription.ReplayEvents,
            static item => item.Type == "error"
        );
        using JsonDocument payload = JsonDocument.Parse(
            JsonSerializer.Serialize(error.Payload)
        );
        Assert.Equal(
            "stopped-before-dispatch",
            payload.RootElement
                .GetProperty("failureReason")
                .GetString()
        );
    }

    private static async Task LoginAsync(HttpClient client) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<StartTurnResponseDto> StartTurnAsync(
        HttpClient client,
        string message
    ) {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/chat/turns",
            new ChatStreamRequest(message, ConnectionId: "test")
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto? started = await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>();
        return Assert.IsType<StartTurnResponseDto>(started);
    }

    private static async Task<RecentTurnsResponseDto>
        GetRecentTurnsAsync(HttpClient client) {
        RecentTurnsResponseDto? response = await client
            .GetFromJsonAsync<RecentTurnsResponseDto>(
                "/api/recent-turns"
            );
        return Assert.IsType<RecentTurnsResponseDto>(response);
    }

    private static async Task<CurrentTurnDto> GetCurrentTurnAsync(
        HttpClient client
    ) {
        CurrentTurnDto? response = await client
            .GetFromJsonAsync<CurrentTurnDto>(
                "/api/chat/turns/current"
            );
        return Assert.IsType<CurrentTurnDto>(response);
    }

    private static GalateaLiveTurn RequireTurn(
        GalateaHostService hostService,
        UserSessionHost session,
        string turnId
    ) => Assert.IsType<GalateaLiveTurn>(
        hostService.FindTurn(session, turnId)
    );

    private static Task RequireRunTask(GalateaLiveTurn liveTurn) =>
        Assert.IsAssignableFrom<Task>(liveTurn.RunTask);

    private sealed class ReturningNormalizer(string normalized)
        : IGalateaUserMessageNormalizer {
        private int _normalizeCallCount;

        internal string? ReceivedMessage { get; private set; }

        internal int NormalizeCallCount => Volatile.Read(
            ref _normalizeCallCount
        );

        public bool ShouldNormalize(string userMessage) {
            ReceivedMessage = userMessage;
            return true;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            ReceivedMessage = userMessage;
            Interlocked.Increment(ref _normalizeCallCount);
            return ValueTask.FromResult(normalized);
        }
    }

    private sealed class BlockingNormalizer
        : IGalateaUserMessageNormalizer {
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken CapturedToken { get; private set; }

        public bool ShouldNormalize(string userMessage) {
            _ = userMessage;
            return true;
        }

        public async ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            _ = userMessage;
            CapturedToken = ct;
            Entered.TrySetResult();
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "unreachable";
            }
            catch (OperationCanceledException) when (
                ct.IsCancellationRequested
            ) {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ScriptedCompletionClient(string reply)
        : ICompletionClient {
        private int _dispatchCallCount;

        public string Name => "galatea-preprocessor-vertical-test";

        public string ApiSpecId => "openai-chat-v1";

        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        internal CompletionRequest? LastRequest { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            Interlocked.Increment(ref _dispatchCallCount);
            observer?.OnTextDelta(reply);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(reply)]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }
    }

    private sealed class SingleClientFactory(ICompletionClient client)
        : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            return client;
        }
    }
}
