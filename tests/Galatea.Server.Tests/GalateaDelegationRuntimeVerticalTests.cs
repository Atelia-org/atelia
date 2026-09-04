using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaDelegationRuntimeVerticalTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReadyReplyTurn_IsConditionalAtomicAndBypassesNormalizer() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        const string Reply = "reply delivered by the durable delegate";
        using var automaticCompletionStarted = new ManualResetEventSlim();
        using var releaseAutomaticCompletion = new ManualResetEventSlim();
        var mainClient = new QueueClient(
            _ => Completed(main, "[Galatea] sent one letter."),
            _ => {
                automaticCompletionStarted.Set();
                Assert.True(releaseAutomaticCompletion.Wait(Deadline));
                return Completed(main, "received the automatic reply");
            }
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTools(
                extractor,
                MailTool("mail-ready-turn", "automatic task")
            ),
            _ => Completed(extractor, "no mail")
        );
        var normalizer = new CountingNormalizer();
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractor.Id] = extractorClient,
        });
        var backend = new DurableBackend();
        await using GalateaTestHost host = GalateaTestHost.Create(
            factory,
            normalizer,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateTransport: new DurableTransport(backend)
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        Atelia.EventJournal.EventAddress? initialHead = session.Engine
            .ReadCurrentHead();

        using (HttpResponseMessage empty = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest(main.Id))) {
            Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
            Assert.Empty(await empty.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(initialHead, session.Engine.ReadCurrentHead());
        Assert.Null(session.GetCurrentTurn());
        Assert.Null(session.DelegationHandle!.Store.ReadSnapshot().ActiveLease);
        Assert.Equal(0, mainClient.CallCount);
        Assert.Equal(0, extractorClient.CallCount);
        Assert.Equal(0, normalizer.ShouldNormalizeCallCount);
        Assert.Equal(0, normalizer.NormalizeCallCount);

        GalateaLiveTurn sent = await StartAndWaitAsync(
            http,
            service,
            session,
            "send one"
        );
        Assert.Equal("completed", sent.Status);
        await WaitUntilAsync(() => backend.StartCallCount == 1);
        backend.Complete(0, Reply);
        _ = session.DelegationHandle.Signal();
        await WaitUntilAsync(() => session.DelegationHandle.Store
            .ReadSnapshot().Notices.SingleOrDefault()?.State
                == GalateaReplyNoticeState.Ready);
        int shouldNormalizeBeforeReady = normalizer.ShouldNormalizeCallCount;
        int normalizeBeforeReady = normalizer.NormalizeCallCount;

        using (HttpResponseMessage unknown = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest("unknown"))) {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }
        GalateaDelegationStateSnapshot afterUnknown = session.DelegationHandle
            .Store.ReadSnapshot();
        Assert.Null(afterUnknown.ActiveLease);
        Assert.Equal(
            GalateaReplyNoticeState.Ready,
            Assert.Single(afterUnknown.Notices).State
        );

        StartTurnResponseDto accepted;
        using (HttpResponseMessage response = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest(main.Id))) {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            accepted = Assert.IsType<StartTurnResponseDto>(
                await response.Content.ReadFromJsonAsync<
                    StartTurnResponseDto>()
            );
        }
        GalateaLiveTurn received = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, accepted.TurnId)
        );
        Assert.True(automaticCompletionStarted.Wait(Deadline));
        GalateaDelegationStateSnapshot leased = session.DelegationHandle
            .Store.ReadSnapshot();
        Assert.NotNull(leased.ActiveLease);
        Assert.Equal(
            GalateaReplyNoticeState.Leased,
            Assert.Single(leased.Notices).State
        );
        try {
            using HttpResponseMessage busy = await http.PostAsJsonAsync(
                "/api/v1/mailbox/ready-turn",
                new ReadyReplyTurnRequest(main.Id)
            );
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            using JsonDocument body = JsonDocument.Parse(
                await busy.Content.ReadAsStringAsync()
            );
            Assert.Equal(
                "turn-busy",
                body.RootElement.GetProperty("code").GetString()
            );
        }
        finally {
            releaseAutomaticCompletion.Set();
        }
        await Assert.IsAssignableFrom<Task>(received.RunTask)
            .WaitAsync(Deadline);
        Assert.Equal("completed", received.Status);
        Assert.Equal(2, mainClient.CallCount);
        Assert.Equal(
            shouldNormalizeBeforeReady,
            normalizer.ShouldNormalizeCallCount
        );
        Assert.Equal(normalizeBeforeReady, normalizer.NormalizeCallCount);

        SessionCompletedTurnProjection completed = session.Engine
            .ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single();
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
            completed.ObservationContent,
            out PlayerTurnObservation observation
        ));
        Assert.Equal(
            GalateaHostService.ReadyReplyTurnPlayerText,
            observation.PlayerText
        );
        Assert.NotNull(observation.ExternalLocalTimestamp);
        Assert.Equal(
            [Reply],
            observation.Notices.Select(static notice => notice.Body)
                .ToArray()
        );
        GalateaDelegationStateSnapshot consumed = session.DelegationHandle
            .Store.ReadSnapshot();
        Assert.Null(consumed.ActiveLease);
        Assert.Equal(
            GalateaReplyNoticeState.Consumed,
            Assert.Single(consumed.Notices).State
        );
        Atelia.EventJournal.EventAddress? completedHead = session.Engine
            .ReadCurrentHead();
        int extractorCallsAfterCompleted = extractorClient.CallCount;

        using (HttpResponseMessage empty = await http.PostAsJsonAsync(
                   "/api/v1/mailbox/ready-turn",
                   new ReadyReplyTurnRequest(main.Id))) {
            Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
        }
        Assert.Equal(completedHead, session.Engine.ReadCurrentHead());
        Assert.Equal(2, mainClient.CallCount);
        Assert.Equal(extractorCallsAfterCompleted, extractorClient.CallCount);
        Assert.Null(session.DelegationHandle.Store.ReadSnapshot().ActiveLease);
    }

    [Fact]
    public async Task DurableRoundTrip_UsesOneFixedThreadAndUndoDoesNotRearm() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        const string ReplyOne = "first reply ```nested```";
        const string ReplyTwo = "second reply <tag>值</tag>";
        var mainClient = new QueueClient(
            _ => Completed(main, "[Galatea] sent two letters."),
            _ => Completed(main, "received both replies"),
            _ => Completed(main, "after undo")
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTools(
                extractor,
                MailTool("mail-1", "first task"),
                MailTool("mail-2", "second task")
            ),
            _ => Completed(extractor, "no mail"),
            _ => Completed(extractor, "no mail")
        );
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractor.Id] = extractorClient,
        });
        var backend = new DurableBackend();
        await using GalateaTestHost host = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateTransport: new DurableTransport(backend)
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
            "send them"
        );
        Assert.Equal("completed", sent.Status);
        await WaitUntilAsync(() => backend.StartCallCount == 1);
        backend.Complete(0, ReplyOne);
        _ = session.DelegationHandle!.Signal();
        await WaitUntilAsync(() => backend.StartCallCount == 2);
        backend.Complete(1, ReplyTwo);
        _ = session.DelegationHandle.Signal();
        await WaitUntilAsync(() => session.DelegationHandle.Store
            .ReadSnapshot().Notices.Count == 2);

        Assert.Equal(1, backend.EnsureCallCount);
        Assert.Equal(
            ["thread-fixed", "thread-fixed"],
            backend.StartRequests.Select(static request => request.ThreadId)
                .ToArray()
        );

        GalateaLiveTurn received = await StartAndWaitAsync(
            http,
            service,
            session,
            "player text"
        );
        Assert.Equal("completed", received.Status);
        SessionCompletedTurnProjection receiving = session.Engine
            .ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns.Single();
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
            receiving.ObservationContent,
            out PlayerTurnObservation composite
        ));
        Assert.Equal("player text", composite.PlayerText);
        Assert.NotNull(composite.ExternalLocalTimestamp);
        Assert.Equal(
            [ReplyOne, ReplyTwo],
            composite.Notices.Select(static notice => notice.Body)
                .ToArray()
        );
        GalateaDelegationStateSnapshot consumed = session.DelegationHandle
            .Store.ReadSnapshot();
        Assert.All(consumed.Notices, static notice => {
            Assert.Equal(GalateaReplyNoticeState.Consumed, notice.State);
            Assert.NotNull(notice.ConsumedActionAddress);
        });

        RecentTurnsResponseDto recent = (await http.GetFromJsonAsync<
            RecentTurnsResponseDto>("/api/v1/recent-turns"))!;
        using HttpResponseMessage undo = await http.PostAsJsonAsync(
            "/api/v1/chat/turns/pop-latest",
            new { rewindLatestToken = recent.RewindLatestToken }
        );
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.All(
            session.DelegationHandle.Store.ReadSnapshot().Notices,
            static notice => Assert.Equal(
                GalateaReplyNoticeState.Consumed,
                notice.State
            )
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
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
            afterUndo.ObservationContent,
            out PlayerTurnObservation afterUndoComposite
        ));
        Assert.Empty(afterUndoComposite.Notices);
        Assert.NotNull(afterUndoComposite.ExternalLocalTimestamp);
        Assert.Equal(2, backend.StartCallCount);
    }

    [Fact]
    public async Task AcceptedMail_RestartInspectsWithoutSecondStart() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        var mainClient = new QueueClient(
            _ => Completed(main, "[Galatea] sent one letter."),
            _ => Completed(main, "received restart reply")
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTools(
                extractor,
                MailTool("mail-restart", "restart task")
            ),
            _ => Completed(extractor, "no mail")
        );
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractor.Id] = extractorClient,
        });
        var backend = new DurableBackend();
        var first = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            deleteFilesOnDispose: false,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateTransport: new DurableTransport(backend)
        );
        GalateaTestHost? restarted = null;
        try {
            using (HttpClient http = first.CreateClient()) {
                await LoginAsync(http);
                GalateaHostService service = first.Factory.Services
                    .GetRequiredService<GalateaHostService>();
                UserSessionHost session = await service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                );
                _ = await StartAndWaitAsync(
                    http,
                    service,
                    session,
                    "send before restart"
                );
                await WaitUntilAsync(() => session.DelegationHandle!.Store
                    .ReadSnapshot().Mails.Single().State
                    == GalateaDurableMailState.Accepted);
            }
            Assert.Equal(1, backend.StartCallCount);
            await first.DisposeAsync();
            backend.Complete(0, "reply recovered after restart");

            restarted = first.CreateRestarted(
                factory,
                DisabledGalateaUserMessageNormalizer.Instance,
                new DurableTransport(backend)
            );
            using HttpClient restartedHttp = restarted.CreateClient();
            await LoginAsync(restartedHttp);
            GalateaHostService restartedService = restarted.Factory.Services
                .GetRequiredService<GalateaHostService>();
            UserSessionHost restartedSession =
                await restartedService.GetSessionAsync(
                "alice",
                CancellationToken.None
            );
            await WaitUntilAsync(() => restartedSession.DelegationHandle!.Store
                .ReadSnapshot().Notices.SingleOrDefault()?.State
                == GalateaReplyNoticeState.Ready);

            _ = await StartAndWaitAsync(
                restartedHttp,
                restartedService,
                restartedSession,
                "receive after restart"
            );
            Assert.Equal(1, backend.StartCallCount);
            Assert.True(backend.InspectCallCount > 0);
            SessionCompletedTurnProjection receiving = restartedSession.Engine
                .ReadRecentCompletedTurns(1)
                .RequireSnapshot().Turns.Single();
            Assert.Contains(
                "reply recovered after restart",
                receiving.ObservationContent,
                StringComparison.Ordinal
            );
        }
        finally {
            if (restarted is not null) {
                await restarted.DisposeAsync();
            }
            else {
                await first.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task AcceptedMail_GracefulRestartConsumesInterruptedFailure() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractor = Connection("mail-helper");
        var mainClient = new QueueClient(
            _ => Completed(main, "[Galatea] sent one letter."),
            _ => Completed(main, "received interruption notice")
        );
        var extractorClient = new QueueClient(
            _ => CompletedWithTools(
                extractor,
                MailTool("mail-interrupted", "interrupted task")
            ),
            _ => Completed(extractor, "no mail")
        );
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractor.Id] = extractorClient,
        });
        var backend = new DurableBackend();
        var first = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            deleteFilesOnDispose: false,
            connections: [main, extractor],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractor.Id,
            delegateTransport: new DurableTransport(
                backend,
                interruptRunningOnDispose: true
            )
        );
        GalateaTestHost? restarted = null;
        try {
            using (HttpClient http = first.CreateClient()) {
                await LoginAsync(http);
                GalateaHostService service = first.Factory.Services
                    .GetRequiredService<GalateaHostService>();
                UserSessionHost session = await service.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                );
                _ = await StartAndWaitAsync(
                    http,
                    service,
                    session,
                    "send before interruption"
                );
                await WaitUntilAsync(() => session.DelegationHandle!.Store
                    .ReadSnapshot().Mails.Single().State
                    == GalateaDurableMailState.Accepted);
            }
            Assert.Equal(1, backend.StartCallCount);
            await first.DisposeAsync();

            restarted = first.CreateRestarted(
                factory,
                DisabledGalateaUserMessageNormalizer.Instance,
                new DurableTransport(backend)
            );
            using HttpClient restartedHttp = restarted.CreateClient();
            await LoginAsync(restartedHttp);
            GalateaHostService restartedService = restarted.Factory.Services
                .GetRequiredService<GalateaHostService>();
            UserSessionHost restartedSession =
                await restartedService.GetSessionAsync(
                    "alice",
                    CancellationToken.None
                );
            await WaitUntilAsync(() => restartedSession.DelegationHandle!.Store
                .ReadSnapshot().Notices.SingleOrDefault()?.State
                == GalateaReplyNoticeState.Ready);

            GalateaReplyNoticeSnapshot ready = restartedSession
                .DelegationHandle!.Store.ReadSnapshot().Notices.Single();
            Assert.Equal(GalateaReplyNoticeKind.DeliveryFailure, ready.Kind);
            Assert.Equal("inspect-dispatch", ready.Stage);
            Assert.Equal("TURN_INTERRUPTED", ready.Code);
            Assert.Equal(1, backend.StartCallCount);

            _ = await StartAndWaitAsync(
                restartedHttp,
                restartedService,
                restartedSession,
                "receive interruption notice"
            );
            SessionCompletedTurnProjection receiving = restartedSession.Engine
                .ReadRecentCompletedTurns(1)
                .RequireSnapshot().Turns.Single();
            Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(
                receiving.ObservationContent,
                out PlayerTurnObservation observation
            ));
            Assert.Contains(
                ready.Body,
                observation.Notices.Select(static notice => notice.Body)
            );
            Assert.Equal(
                GalateaReplyNoticeState.Consumed,
                restartedSession.DelegationHandle.Store.ReadSnapshot()
                    .Notices.Single().State
            );
            Assert.Equal(1, backend.StartCallCount);
            Assert.True(backend.InspectCallCount > 0);
        }
        finally {
            if (restarted is not null) {
                await restarted.DisposeAsync();
            }
            else {
                await first.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task MaintenanceHost_PerformsNoDurableTransportCall() {
        CompletionConnectionConfig main = Connection("test");
        var backend = new DurableBackend();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                [main.Id] = new QueueClient(
                    _ => Completed(main, "unused")
                )
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            maintenanceMode: true,
            connections: [main],
            delegateTransport: new DurableTransport(backend)
        );
        using HttpClient http = host.CreateClient();
        await LoginAsync(http);

        using HttpResponseMessage response = await http.GetAsync(
            "/api/v1/recent-turns"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, backend.TotalCallCount);
    }

    private static async Task<GalateaLiveTurn> StartAndWaitAsync(
        HttpClient http,
        GalateaHostService service,
        UserSessionHost session,
        string message
    ) {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/chat/turns",
            new ChatStreamRequest(message, "test")
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        StartTurnResponseDto started = Assert.IsType<StartTurnResponseDto>(
            await response.Content.ReadFromJsonAsync<StartTurnResponseDto>()
        );
        GalateaLiveTurn turn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, started.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(turn.RunTask)
            .WaitAsync(Deadline);
        return turn;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate) {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Deadline;
        while (!predicate()) {
            if (DateTimeOffset.UtcNow >= deadline) {
                throw new TimeoutException("Durable vertical condition timed out.");
            }
            await Task.Delay(25);
        }
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
        CompletionConnectionConfig connection,
        string text
    ) => new(
        new ActionMessage([new ActionBlock.Text(text)]),
        new CompletionDescriptor(
            "delegation-runtime-test",
            "test-v1",
            connection.ModelId
        )
    );

    private static CompletionResult CompletedWithTools(
        CompletionConnectionConfig connection,
        params ActionBlock.ToolCall[] tools
    ) => new(
        new ActionMessage(tools),
        new CompletionDescriptor(
            "delegation-runtime-test",
            "test-v1",
            connection.ModelId
        )
    );

    private static ActionBlock.ToolCall MailTool(string id, string body) =>
        new(new RawToolCall(
            OutboundMailExtractor.ToolName,
            id,
            JsonSerializer.Serialize(new {
                recipient = "Codex",
                subject = (string?)null,
                body,
                inReplyToMessageId = (string?)null,
                evidenceQuote = "sent",
            }, new JsonSerializerOptions {
                DefaultIgnoreCondition = System.Text.Json.Serialization
                    .JsonIgnoreCondition.WhenWritingNull
            })
        ));

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
        private int _callCount;

        public string Name => "delegation-runtime-test";
        public string ApiSpecId => "test-v1";
        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            CompletionResult result = _scripts.Dequeue()(request);
            foreach (ActionBlock.Text text in result.Message.Blocks
                         .OfType<ActionBlock.Text>()) {
                observer?.OnTextDelta(text.Content);
            }
            return Task.FromResult(result);
        }
    }

    private sealed class CountingNormalizer : IGalateaUserMessageNormalizer {
        private int _shouldNormalizeCallCount;
        private int _normalizeCallCount;

        internal int ShouldNormalizeCallCount => Volatile.Read(
            ref _shouldNormalizeCallCount
        );
        internal int NormalizeCallCount => Volatile.Read(
            ref _normalizeCallCount
        );

        public bool ShouldNormalize(string userMessage) {
            Interlocked.Increment(ref _shouldNormalizeCallCount);
            return true;
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _normalizeCallCount);
            return ValueTask.FromResult("normalized: " + userMessage);
        }
    }

    private sealed class DurableBackend {
        private readonly ConcurrentDictionary<string, DurableCall> _calls =
            new(StringComparer.Ordinal);
        private readonly List<GalateaStartDelegateTurnRequest> _starts = [];
        private int _ensureCallCount;
        private int _inspectCallCount;

        internal int EnsureCallCount => Volatile.Read(ref _ensureCallCount);
        internal int InspectCallCount => Volatile.Read(ref _inspectCallCount);
        internal int StartCallCount {
            get { lock (_starts) { return _starts.Count; } }
        }
        internal int TotalCallCount =>
            EnsureCallCount + StartCallCount + InspectCallCount;
        internal GalateaStartDelegateTurnRequest[] StartRequests {
            get { lock (_starts) { return [.. _starts]; } }
        }

        internal GalateaDelegateBindingEstablished Ensure(
            GalateaEnsureDelegateBindingRequest request
        ) {
            Interlocked.Increment(ref _ensureCallCount);
            return new(request.BindingOperationId, "thread-fixed");
        }

        internal GalateaDelegateTurnAccepted Start(
            GalateaStartDelegateTurnRequest request
        ) {
            int ordinal;
            lock (_starts) {
                ordinal = _starts.Count;
                _starts.Add(request);
            }
            string turnId = "turn-" + ordinal;
            if (!_calls.TryAdd(
                    request.DispatchId,
                    new DurableCall(turnId))) {
                throw new InvalidOperationException("duplicate start");
            }
            return new(request.DispatchId, request.ThreadId, turnId);
        }

        internal GalateaDelegateDispatchInspection Inspect(
            GalateaInspectDelegateDispatchRequest request
        ) {
            Interlocked.Increment(ref _inspectCallCount);
            DurableCall call = _calls[request.DispatchId];
            if (!string.Equals(
                    request.ExpectedTurnId,
                    call.TurnId,
                    StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    "Accepted vertical inspection did not select its exact turn."
                );
            }
            string? failureCode = Volatile.Read(ref call.FailureCode);
            if (failureCode is not null) {
                return new GalateaDelegateDispatchInspection.Failed(
                    request.DispatchId,
                    request.ThreadId,
                    call.TurnId,
                    failureCode
                );
            }
            string? final = Volatile.Read(ref call.Final);
            return final is null
                ? new GalateaDelegateDispatchInspection.Running(
                    request.DispatchId,
                    request.ThreadId,
                    call.TurnId
                )
                : new GalateaDelegateDispatchInspection.Completed(
                    request.DispatchId,
                    request.ThreadId,
                    call.TurnId,
                    final
                );
        }

        internal void Complete(int ordinal, string final) {
            GalateaStartDelegateTurnRequest request;
            lock (_starts) { request = _starts[ordinal]; }
            Volatile.Write(ref _calls[request.DispatchId].Final, final);
        }

        internal void InterruptRunning() {
            foreach (DurableCall call in _calls.Values) {
                if (Volatile.Read(ref call.Final) is null) {
                    Volatile.Write(ref call.FailureCode, "TURN_INTERRUPTED");
                }
            }
        }
    }

    private sealed class DurableCall(string turnId) {
        internal string TurnId { get; } = turnId;
        internal string? Final;
        internal string? FailureCode;
    }

    private sealed class DurableTransport(
        DurableBackend backend,
        bool interruptRunningOnDispose = false
    )
        : IGalateaDurableDelegateTransport {
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
            GalateaEnsureDelegateBindingRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(backend.Ensure(request));
        }

        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(
            GalateaStartDelegateTurnRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(backend.Start(request));
        }

        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
            GalateaInspectDelegateDispatchRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(backend.Inspect(request));
        }

        public ValueTask DisposeAsync() {
            if (interruptRunningOnDispose) {
                backend.InterruptRunning();
            }
            return ValueTask.CompletedTask;
        }
    }
}
