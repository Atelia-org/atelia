using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegationRuntimeVerticalTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OutboundDispatch_DoesNotAwaitCodex_AndReplyIsOneShotCompositeThroughUndo() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        const string SentAction =
            "[Galatea] I sent task alpha to Codex and completed sending.";
        const string Reply =
            "result ```inside``` <tag attr=\"&\">值</tag>\n~~~~~\n终";
        var mainClient = new QueueClient(
            _ => Completed(mainClient: null, main, SentAction),
            _ => Completed(mainClient: null, main, "received reply"),
            _ => Completed(mainClient: null, main, "after undo")
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTool(
                extractor,
                MailTool(
                    "mail-1",
                    "Codex",
                    "task alpha",
                    "completed sending"
                )
            ),
            _ => Completed(mainClient: null, extractor, "no mail"),
            _ => Completed(mainClient: null, extractor, "no mail")
        );
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractor.Id] = extractorClient,
        });
        var normalizer = new RecordingNormalizer();
        var sidecar = new GateSidecar();
        await using GalateaTestHost host = GalateaTestHost.Create(
            factory,
            normalizer,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateSidecar: sidecar
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        GalateaLiveTurn sent = await StartAndWaitAsync(
            http,
            service,
            session,
            "send it"
        );
        Assert.Equal("completed", sent.Status);
        Assert.True(session.TurnLock.Wait(0));
        session.TurnLock.Release();

        GateCall call = await sidecar.NextCallAsync();
        Assert.Null(call.Request.ThreadId);
        Assert.Equal("task alpha", call.Request.Body);
        Assert.False(call.Accepted.Task.IsCompleted);
        using (GalateaTurnSubscription sentReplay = sent.Subscribe()) {
            Assert.Contains(
                sentReplay.ReplayFrames,
                static frame => frame.EventName == "done"
            );
        }

        call.Accept("thread-fixed", "codex-turn-1");
        call.Complete(Reply);
        await session.DelegationCoordinator.PumpTaskForTest
            .WaitAsync(Deadline);

        GalateaLiveTurn received = await StartAndWaitAsync(
            http,
            service,
            session,
            "玩家正文"
        );
        SessionCompletedTurnProjection receivingTurn = session.Engine
            .ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single();
        Assert.True(GalateaPlayerObservationEnvelope.TryUnwrap(
            receivingTurn.ObservationContent,
            out GalateaPlayerObservation composite
        ));
        Assert.Equal("玩家正文", composite.PlayerText);
        Assert.Equal(Reply, Assert.Single(composite.ReadyNotices).Body);
        Assert.Equal(["send it", "玩家正文"], normalizer.Received);

        CompletionRequest receivingRequest = mainClient.Requests[1];
        ObservationMessage observation = Assert.Single(
            receivingRequest.PromptPrefix.SharedContextMessages
                .OfType<ObservationMessage>(),
            message => string.Equals(
                message.Content,
                receivingTurn.ObservationContent,
                StringComparison.Ordinal)
        );
        Assert.Equal(receivingTurn.ObservationContent, observation.Content);

        RecentTurnsResponseDto recent = (await http.GetFromJsonAsync<
            RecentTurnsResponseDto>("/api/v1/recent-turns"))!;
        Assert.Contains(Reply, recent.Turns[0].UserText,
            StringComparison.Ordinal);
        RecentTurnsResponseDto sseRecent = ReadDoneRecent(received);
        Assert.Contains(Reply, sseRecent.Turns[0].UserText,
            StringComparison.Ordinal);
        Assert.NotNull(recent.RewindLatestToken);

        using HttpResponseMessage undo = await http.PostAsJsonAsync(
            "/api/v1/chat/turns/pop-latest",
            new { rewindLatestToken = recent.RewindLatestToken }
        );
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(
            GalateaDelegateCandidateState.Consumed,
            Assert.Single(session.DelegationCoordinator.Snapshot()).State
        );

        _ = await StartAndWaitAsync(
            http,
            service,
            session,
            "after undo"
        );
        SessionCompletedTurnProjection afterUndo = session.Engine
            .ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single();
        Assert.True(GalateaPlayerObservationEnvelope.TryUnwrap(
            afterUndo.ObservationContent,
            out GalateaPlayerObservation afterUndoComposite
        ));
        Assert.Empty(afterUndoComposite.ReadyNotices);
    }

    [Fact]
    public async Task CutoffExcludesLaterTerminalUntilFollowingPlayerTurn() {
        var mainClient = new QueueClient(
            request => Completed(mainClient: null, Connection("test"), "one"),
            request => Completed(mainClient: null, Connection("test"), "two")
        );
        var sidecar = new GateSidecar();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                ["test"] = mainClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection("test")],
            delegateSidecar: sidecar
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        session.DelegationCoordinator.TryCaptureBatch(
            "source",
            Head(100),
            [Mail("task")]
        );
        GateCall call = await sidecar.NextCallAsync();

        StartTurnResponseDto started = await StartAsync(http, "cutoff now");
        call.Accept("thread-fixed", "codex-turn");
        call.Complete("later reply");
        await session.DelegationCoordinator.PumpTaskForTest
            .WaitAsync(Deadline);
        GalateaLiveTurn current = RequireTurn(service, session, started);
        await current.RunTask!.WaitAsync(Deadline);

        SessionCompletedTurnProjection first = session.Engine
            .ReadRecentCompletedTurns(1).RequireSnapshot().Turns.Single();
        Assert.True(GalateaPlayerObservationEnvelope.TryUnwrap(
            first.ObservationContent,
            out GalateaPlayerObservation firstComposite
        ));
        Assert.Empty(firstComposite.ReadyNotices);

        _ = await StartAndWaitAsync(
            http,
            service,
            session,
            "following turn"
        );
        SessionCompletedTurnProjection second = session.Engine
            .ReadRecentCompletedTurns(1).RequireSnapshot().Turns.Single();
        Assert.True(GalateaPlayerObservationEnvelope.TryUnwrap(
            second.ObservationContent,
            out GalateaPlayerObservation secondComposite
        ));
        Assert.Equal(
            "later reply",
            Assert.Single(secondComposite.ReadyNotices).Body
        );
    }

    [Fact]
    public async Task PreObservationFailureRollsBackReadyLease() {
        var mainClient = new QueueClient(
            _ => Completed(mainClient: null, Connection("test"), "ok")
        );
        var normalizer = new SequencedNormalizer(
            new string('x', GalateaHttpV1.MaximumMessageUtf8Bytes + 1),
            "accepted"
        );
        var sidecar = new GateSidecar();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                ["test"] = mainClient,
            }),
            normalizer,
            connections: [Connection("test")],
            delegateSidecar: sidecar
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        await MakeReadyAsync(session, sidecar, 1, "retry me");

        GalateaLiveTurn failed = await StartAndWaitAsync(
            http,
            service,
            session,
            "first"
        );
        Assert.Equal("failed", failed.Status);
        Assert.Equal(
            GalateaDelegateCandidateState.ReplyReady,
            Assert.Single(session.DelegationCoordinator.Snapshot()).State
        );
        Assert.Empty(session.Engine.ReadRecentCompletedTurns()
            .RequireSnapshot().Turns);

        _ = await StartAndWaitAsync(
            http,
            service,
            session,
            "second"
        );
        SessionCompletedTurnProjection persisted = session.Engine
            .ReadRecentCompletedTurns(1).RequireSnapshot().Turns.Single();
        Assert.True(GalateaPlayerObservationEnvelope.TryUnwrap(
            persisted.ObservationContent,
            out GalateaPlayerObservation composite
        ));
        Assert.Equal("retry me", Assert.Single(composite.ReadyNotices).Body);
    }

    [Fact]
    public async Task RecoverableFreshFailureKeepsOldLease_AndRecoveryDoesNotClaimNewReady() {
        CompletionConnectionConfig connection = Connection("test");
        var mainClient = new QueueClient(
            _ => throw new HttpRequestException(
                "uncertain",
                inner: null,
                HttpStatusCode.InternalServerError
            ),
            _ => Completed(mainClient: null, connection, "recovered"),
            _ => Completed(mainClient: null, connection, "next")
        );
        var sidecar = new GateSidecar();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                [connection.Id] = mainClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [connection],
            delegateSidecar: sidecar
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        await MakeReadyAsync(session, sidecar, 1, "old reply");

        GalateaLiveTurn failed = await StartAndWaitAsync(
            http,
            service,
            session,
            "recoverable"
        );
        Assert.Equal("failed", failed.Status);
        Assert.Equal(
            SessionExecutionPhase.AwaitingCompletion,
            session.Engine.InspectExecutionBoundary().Phase
        );
        Assert.Equal(
            GalateaDelegateCandidateState.Leased,
            session.DelegationCoordinator.Snapshot()[0].State
        );

        await MakeReadyAsync(session, sidecar, 2, "new reply");
        EventAddress recoveryHead = session.Engine.ReadCurrentHead()!.Value;
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/chat/turns/resume",
            new ResumeTurnRequest(
                EventAddressTextCodec.Format(recoveryHead),
                ConnectionId: null,
                RestartUncertainCompletion: true
            )
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto recoveryStarted = (await response.Content
            .ReadFromJsonAsync<StartTurnResponseDto>())!;
        GalateaLiveTurn recovered = RequireTurn(
            service,
            session,
            recoveryStarted
        );
        await recovered.RunTask!.WaitAsync(Deadline);
        Assert.Equal("completed", recovered.Status);

        SessionCompletedTurnProjection recoveredTurn = session.Engine
            .ReadRecentCompletedTurns(1).RequireSnapshot().Turns.Single();
        Assert.True(GalateaPlayerObservationEnvelope.TryUnwrap(
            recoveredTurn.ObservationContent,
            out GalateaPlayerObservation recoveredComposite
        ));
        Assert.Equal(
            "old reply",
            Assert.Single(recoveredComposite.ReadyNotices).Body
        );
        Assert.Equal(
            [
                GalateaDelegateCandidateState.Consumed,
                GalateaDelegateCandidateState.ReplyReady,
            ],
            session.DelegationCoordinator.Snapshot()
                .Select(static value => value.State)
        );

        _ = await StartAndWaitAsync(
            http,
            service,
            session,
            "after recovery"
        );
        SessionCompletedTurnProjection next = session.Engine
            .ReadRecentCompletedTurns(1).RequireSnapshot().Turns.Single();
        Assert.True(GalateaPlayerObservationEnvelope.TryUnwrap(
            next.ObservationContent,
            out GalateaPlayerObservation nextComposite
        ));
        Assert.Equal(
            "new reply",
            Assert.Single(nextComposite.ReadyNotices).Body
        );
    }

    [Fact]
    public async Task InboundTurnNeverClaimsReadyReply() {
        var mainClient = new QueueClient(
            _ => Completed(mainClient: null, Connection("test"), "mail"),
            _ => Completed(mainClient: null, Connection("test"), "player")
        );
        var sidecar = new GateSidecar();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                ["test"] = mainClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection("test")],
            delegateSidecar: sidecar
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        await MakeReadyAsync(session, sidecar, 1, "held for player");

        using HttpResponseMessage inbound = await http.PostAsJsonAsync(
            "/api/v1/mailbox/inbound",
            new { from = "Alice", body = "hello", connectionId = "test" }
        );
        Assert.Equal(HttpStatusCode.Accepted, inbound.StatusCode);
        InboundMailboxAcceptedDto accepted = (await inbound.Content
            .ReadFromJsonAsync<InboundMailboxAcceptedDto>())!;
        GalateaLiveTurn inboundTurn = service.FindTurn(
            session,
            accepted.TurnId
        )!;
        await inboundTurn.RunTask!.WaitAsync(Deadline);
        Assert.Equal(
            GalateaDelegateCandidateState.ReplyReady,
            Assert.Single(session.DelegationCoordinator.Snapshot()).State
        );
        Assert.True(GalateaMailboxObservationEnvelope.TryUnwrap(
            session.Engine.ReadRecentCompletedTurns(1)
                .RequireSnapshot().Turns.Single().ObservationContent,
            out _
        ));

        _ = await StartAndWaitAsync(
            http,
            service,
            session,
            "ordinary"
        );
        SessionCompletedTurnProjection ordinary = session.Engine
            .ReadRecentCompletedTurns(1).RequireSnapshot().Turns.Single();
        Assert.True(GalateaPlayerObservationEnvelope.TryUnwrap(
            ordinary.ObservationContent,
            out GalateaPlayerObservation composite
        ));
        Assert.Equal(
            "held for player",
            Assert.Single(composite.ReadyNotices).Body
        );
    }

    private static async Task MakeReadyAsync(
        UserSessionHost session,
        GateSidecar sidecar,
        uint ordinal,
        string reply
    ) {
        Assert.True(session.DelegationCoordinator.TryCaptureBatch(
            $"source-{ordinal}",
            Head(ordinal),
            [Mail($"task-{ordinal}")]
        ));
        GateCall call = await sidecar.NextCallAsync();
        call.Accept("thread-fixed", $"codex-turn-{ordinal}");
        call.Complete(reply);
        await session.DelegationCoordinator.PumpTaskForTest
            .WaitAsync(Deadline);
    }

    private static async Task<GalateaLiveTurn> StartAndWaitAsync(
        HttpClient http,
        GalateaHostService service,
        UserSessionHost session,
        string message
    ) {
        StartTurnResponseDto started = await StartAsync(http, message);
        GalateaLiveTurn turn = RequireTurn(service, session, started);
        await turn.RunTask!.WaitAsync(Deadline);
        return turn;
    }

    private static async Task<StartTurnResponseDto> StartAsync(
        HttpClient http,
        string message
    ) {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/chat/turns",
            new ChatStreamRequest(message, ConnectionId: "test")
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<
            StartTurnResponseDto>())!;
    }

    private static GalateaLiveTurn RequireTurn(
        GalateaHostService service,
        UserSessionHost session,
        StartTurnResponseDto started
    ) => Assert.IsType<GalateaLiveTurn>(
        service.FindTurn(session, started.TurnId)
    );

    private static RecentTurnsResponseDto ReadDoneRecent(
        GalateaLiveTurn turn
    ) {
        using GalateaTurnSubscription subscription = turn.Subscribe();
        GalateaSseFrame done = Assert.Single(
            subscription.ReplayFrames,
            static frame => frame.EventName == "done"
        );
        string wire = Encoding.UTF8.GetString(done.Utf8.Span);
        string data = wire.Split('\n', StringSplitOptions.None)
            .Single(static line => line.StartsWith(
                "data: ",
                StringComparison.Ordinal
            ))["data: ".Length..];
        using JsonDocument document = JsonDocument.Parse(data);
        RecentTurnsResponseDto? recent = document.RootElement
            .GetProperty("recent")
            .Deserialize<RecentTurnsResponseDto>(GalateaJson.Options);
        return Assert.IsType<RecentTurnsResponseDto>(recent);
    }

    private static async Task LoginAsync(HttpClient http) {
        using HttpResponseMessage response =
            await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id,
        "openai-chat",
        id + "-model",
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static CompletionResult Completed(
        ICompletionClient? mainClient,
        CompletionConnectionConfig connection,
        string text
    ) {
        _ = mainClient;
        return new CompletionResult(
            new ActionMessage([new ActionBlock.Text(text)]),
            new CompletionDescriptor(
                "delegation-runtime-test",
                "test-v1",
                connection.ModelId
            )
        );
    }

    private static CompletionResult CompletedWithTool(
        CompletionConnectionConfig connection,
        ActionBlock.ToolCall tool
    ) => new(
        new ActionMessage([tool]),
        new CompletionDescriptor(
            "delegation-runtime-test",
            "test-v1",
            connection.ModelId
        )
    );

    private static ActionBlock.ToolCall MailTool(
        string id,
        string recipient,
        string body,
        string evidence
    ) => new(new RawToolCall(
        OutboundMailExtractor.ToolName,
        id,
        JsonSerializer.Serialize(new {
            recipient,
            subject = (string?)null,
            body,
            inReplyToMessageId = (string?)null,
            evidenceQuote = evidence,
        }, new JsonSerializerOptions {
            DefaultIgnoreCondition = System.Text.Json.Serialization
                .JsonIgnoreCondition.WhenWritingNull,
        })
    ));

    private static SendMailIntent Mail(string task) => new(
        "Codex",
        Subject: null,
        task,
        InReplyToMessageId: null,
        EvidenceQuote: "sent"
    );

    private static EventAddress Head(uint value) =>
        EventAddressTextCodec.Parse(
            $"ej1:{value:x16}{value:x8}{value:x8}"
        );

    private sealed class RecordingNormalizer
        : IGalateaUserMessageNormalizer {
        internal List<string> Received { get; } = [];

        public bool ShouldNormalize(string userMessage) {
            Received.Add(userMessage);
            return false;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException();
    }

    private sealed class SequencedNormalizer(params string[] results)
        : IGalateaUserMessageNormalizer {
        private readonly Queue<string> _results = new(results);

        public bool ShouldNormalize(string userMessage) => true;

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class RoutingFactory(
        IReadOnlyDictionary<string, ICompletionClient> clients
    ) : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => clients[connection.Id];
    }

    private sealed class QueueClient(
        params Func<CompletionRequest, CompletionResult>[] scripts
    ) : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionResult>>
            _scripts = new(scripts);

        public string Name => "delegation-runtime-test";
        public string ApiSpecId => "test-v1";
        internal List<CompletionRequest> Requests { get; } = [];

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            CompletionResult result = _scripts.Dequeue()(request);
            foreach (ActionBlock.Text text in result.Message.Blocks
                         .OfType<ActionBlock.Text>()) {
                observer?.OnTextDelta(text.Content);
            }
            return Task.FromResult(result);
        }
    }

    private sealed class GateSidecar : IGalateaDelegateSidecar {
        private readonly Channel<GateCall> _calls =
            Channel.CreateUnbounded<GateCall>();
        private readonly ConcurrentQueue<GalateaDelegateDispatchRequest>
            _requests = [];

        public Task<GalateaDelegateAcceptedHandle> StartAsync(
            GalateaDelegateDispatchRequest request,
            CancellationToken ct
        ) {
            _requests.Enqueue(request);
            var call = new GateCall(request);
            Assert.True(_calls.Writer.TryWrite(call));
            return call.Accepted.Task.WaitAsync(ct);
        }

        internal Task<GateCall> NextCallAsync() => _calls.Reader
            .ReadAsync().AsTask().WaitAsync(Deadline);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GateCall(
        GalateaDelegateDispatchRequest request
    ) {
        internal GalateaDelegateDispatchRequest Request { get; } = request;
        internal TaskCompletionSource<GalateaDelegateAcceptedHandle>
            Accepted { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        internal TaskCompletionSource<GalateaDelegateTerminal> Completion {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Accept(string threadId, string turnId) =>
            Assert.True(Accepted.TrySetResult(
                new GalateaDelegateAcceptedHandle(
                    Request.DispatchId,
                    threadId,
                    turnId,
                    Completion.Task
                )
            ));

        internal void Complete(string final) {
            GalateaDelegateAcceptedHandle accepted = Accepted.Task.Result;
            Assert.True(Completion.TrySetResult(
                new GalateaDelegateTerminal.Completed(
                    Request.DispatchId,
                    accepted.ThreadId,
                    accepted.TurnId,
                    final
                )
            ));
        }
    }
}
