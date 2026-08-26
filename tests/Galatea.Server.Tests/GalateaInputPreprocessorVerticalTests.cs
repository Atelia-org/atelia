using System.Net;
using System.Net.Http.Json;
using System.Text;
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
    public async Task ConfiguredNormalizer_UsesHiddenLazyConnectionAndConfiguredRequest() {
        CompletionConnectionConfig main = Connection(
            "test",
            "main-model"
        );
        CompletionConnectionConfig helper = Connection(
            "input-helper",
            "helper-model"
        ) with { MaxTokens = 37 };
        var mainClient = new ScriptedCompletionClient("assistant reply");
        var helperClient = new ScriptedCompletionClient(
            "<cleaned>normalized input</cleaned>"
        );
        var factory = new RoutingClientFactory(new Dictionary<
            string,
            ScriptedCompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [helper.Id] = helperClient,
        });
        await using var host = GalateaTestHost.Create(
            factory,
            normalizer: null,
            connections: [main, helper],
            selectableConnectionIds: [main.Id],
            inputNormalizerConnectionId: helper.Id
        );
        using HttpClient client = host.CreateClient();
        await LoginAsync(client);

        Assert.Empty(factory.CreatedConnectionIds);
        StartTurnResponseDto started = await StartTurnAsync(
            client,
            "original input"
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        await RequireRunTask(RequireTurn(
            service,
            session,
            started.TurnId
        )).WaitAsync(CompletionDeadline);

        Assert.Equal([helper.Id, main.Id], factory.CreatedConnectionIds);
        Assert.Equal("helper-model", helperClient.LastRequest!.ModelId);
        Assert.Equal(37, helperClient.LastRequest.MaxTokens);
        Assert.Equal(1, helperClient.DispatchCallCount);
        Assert.Equal(1, mainClient.DispatchCallCount);
        Assert.Equal([main.Id], service.Connections.Select(
            static value => value.Id
        ));
        Assert.False(service.TryGetConnection(helper.Id, out _));
        Assert.Equal(
            GalateaUserMessageEnvelope.Wrap("normalized input"),
            Assert.Single(session.Engine.ReadRecentCompletedTurns()
                .RequireSnapshot().Turns).ObservationContent
        );
    }

    [Fact]
    public async Task NormalizerAndMainAgent_ShareOneRegistryClientAndOwnerDisposesOnce() {
        CompletionConnectionConfig shared = Connection(
            "test",
            "shared-model"
        );
        var client = new RoleAwareCompletionClient();
        var factory = new SingleTrackedFactory(client);
        var host = GalateaTestHost.Create(
            factory,
            normalizer: null,
            connections: [shared],
            inputNormalizerConnectionId: shared.Id
        );
        try {
            using HttpClient http = host.CreateClient();
            await LoginAsync(http);
            StartTurnResponseDto started = await StartTurnAsync(
                http,
                "orignal input"
            );
            GalateaHostService service = host.Factory.Services
                .GetRequiredService<GalateaHostService>();
            UserSessionHost session = await service.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            await RequireRunTask(RequireTurn(
                service,
                session,
                started.TurnId
            )).WaitAsync(CompletionDeadline);

            Assert.Equal(1, factory.CreateCallCount);
            Assert.Equal(2, client.DispatchCallCount);
            Assert.Equal(0, client.DisposeCount);
        }
        finally {
            await host.DisposeAsync();
        }
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task DirectFreshSend_HiddenConnectionFailsBeforeNormalizationClientOrMutation() {
        CompletionConnectionConfig visible = Connection(
            "test",
            "visible-model"
        );
        CompletionConnectionConfig hidden = Connection(
            "hidden",
            "hidden-model"
        );
        var completion = new ScriptedCompletionClient("must not dispatch");
        var factory = new RoutingClientFactory(new Dictionary<
            string,
            ScriptedCompletionClient
        >(StringComparer.Ordinal) {
            [visible.Id] = completion,
            [hidden.Id] = completion,
        });
        var normalizer = new ReturningNormalizer("must not normalize");
        await using var host = GalateaTestHost.Create(
            factory,
            normalizer,
            connections: [visible, hidden],
            selectableConnectionIds: [visible.Id]
        );
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        var initialHead = session.Engine.ReadCurrentHead();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "direct hidden fresh",
            new GalateaTurnOptions(hidden.Id)
        );
        try {
            GalateaTurnException failure = await Assert.ThrowsAsync<
                GalateaTurnException>(() => service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                ));

            Assert.Equal(
                "recap-grid-connection-absent",
                failure.FailureReason
            );
            Assert.Empty(factory.CreatedConnectionIds);
            Assert.Equal(0, normalizer.NormalizeCallCount);
            Assert.Equal(0, completion.DispatchCallCount);
            Assert.Equal(initialHead, session.Engine.ReadCurrentHead());
        }
        finally {
            service.FinishTurn(session, turn);
        }
    }

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
            request.PromptPrefix.SharedContextMessages.OfType<ObservationMessage>()
        );
        Assert.Equal(wrapped, requestedObservation.Content);

        var persisted = session.Engine.ReadRecentCompletedTurns(1)
            .RequireSnapshot();
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
                $"/api/v1/chat/turns/{started.TurnId}/stop",
                content: null
            )
            .WaitAsync(CompletionDeadline);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);

        await normalizer.CancellationObserved.Task
            .WaitAsync(CompletionDeadline);
        await runTask.WaitAsync(CompletionDeadline);

        Assert.True(normalizer.CapturedToken.IsCancellationRequested);
        Assert.Equal(0, completion.DispatchCallCount);
        Assert.Equal(initialHead, session.Engine.ReadCurrentHead());
        Assert.Empty(
            session.Engine.ReadRecentCompletedTurns().RequireSnapshot().Turns
        );

        RecentTurnsResponseDto recent = await GetRecentTurnsAsync(client);
        Assert.Empty(recent.Turns);
        CurrentTurnDto current = await GetCurrentTurnAsync(client);
        Assert.Equal("idle", current.Status);

        Assert.Equal("failed", liveTurn.Status);
        using GalateaTurnSubscription subscription = liveTurn.Subscribe();
        GalateaSseFrame error = Assert.Single(
            subscription.ReplayFrames,
            static item => item.EventName == "error"
        );
        using JsonDocument payload = JsonDocument.Parse(
            Encoding.UTF8.GetString(error.Utf8.Span)
                .Split('\n')[1]["data: ".Length..]
        );
        Assert.Equal(
            "operator-stop",
            payload.RootElement
                .GetProperty("code")
                .GetString()
        );
        Assert.False(payload.RootElement.TryGetProperty(
            "failureReason",
            out _
        ));
    }

    private static async Task LoginAsync(HttpClient client) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
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

    private static async Task<StartTurnResponseDto> StartTurnAsync(
        HttpClient client,
        string message
    ) {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/chat/turns",
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
                "/api/v1/recent-turns"
            );
        return Assert.IsType<RecentTurnsResponseDto>(response);
    }

    private static async Task<CurrentTurnDto> GetCurrentTurnAsync(
        HttpClient client
    ) {
        CurrentTurnDto? response = await client
            .GetFromJsonAsync<CurrentTurnDto>(
                "/api/v1/chat/turns/current"
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

    private sealed class RoutingClientFactory(
        IReadOnlyDictionary<string, ScriptedCompletionClient> clients
    ) : ICompletionClientFactory {
        internal List<string> CreatedConnectionIds { get; } = [];

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            CreatedConnectionIds.Add(connection.Id);
            return clients[connection.Id];
        }
    }

    private sealed class SingleTrackedFactory(
        RoleAwareCompletionClient client
    ) : ICompletionClientFactory {
        private int _createCallCount;

        internal int CreateCallCount => Volatile.Read(
            ref _createCallCount
        );

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            ArgumentNullException.ThrowIfNull(connection);
            Interlocked.Increment(ref _createCallCount);
            return client;
        }
    }

    private sealed class RoleAwareCompletionClient
        : ICompletionClient, IDisposable {
        private int _dispatchCallCount;
        private int _disposeCount;

        public string Name => "galatea-shared-normalizer-test";

        public string ApiSpecId => "openai-chat-v1";

        internal int DispatchCallCount => Volatile.Read(
            ref _dispatchCallCount
        );

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _dispatchCallCount);
            bool normalization = request.PromptPrefix.SystemPrompt.Contains(
                "玩家输入清洗器",
                StringComparison.Ordinal
            );
            string text = normalization
                ? "<cleaned>original input</cleaned>"
                : "assistant reply";
            observer?.OnTextDelta(text);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(text)]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
