using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.MemoPod;
using Atelia.MdJson;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaAutonomyPostProcessingTests {
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);
    private const string NoteText = "remember autonomous blue";
    private const string TerminalAction = """
        [Galatea] I sent mail body to Alice and completed sending.
        [Galatea] I submitted a long-term Note save request with exact text: remember autonomous blue, and completed the submission.
        """;

    [Fact]
    public async Task TwoHeartbeatTurnsSaveAndReceiveNoteWithoutPlayerInput() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig helper = Connection("helper");
        var mainClient = new MainClient(main);
        var helperClient = new ExtractorClient();
        var recall = new AutomaticRecallProvider();
        var clock = new ManualTimeProvider(new DateTimeOffset(
            2030,
            1,
            2,
            3,
            4,
            5,
            TimeSpan.Zero
        ));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = mainClient,
                [helper.Id] = helperClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, helper],
            connectionOptionIds: [main.Id],
            outboundMailExtractorConnectionId: helper.Id,
            characterNoteExtractorConnectionId: helper.Id,
            playerTurnRecallProviderFactory: (_, _) => recall,
            timeProvider: clock,
            autonomyCharacterIds: ["alice"]
        );
        using HttpClient http = host.CreateClient();
        using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        await AssertWaitingPulseAsync(http);
        for (int pulse = 1; pulse < 60; pulse++) {
            clock.Advance(TimeSpan.FromSeconds(10));
            await AssertWaitingPulseAsync(http);
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        LoopPulseAcceptedTurnDto accepted;
        using (HttpResponseMessage response = await http.PostAsJsonAsync(
                   "/api/v1/characters/alice/mailbox/ready-turn",
                   new ReadyReplyTurnRequest())) {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            accepted = Assert.IsType<LoopPulseAcceptedTurnDto>(
                await response.Content
                    .ReadFromJsonAsync<LoopPulseAcceptedTurnDto>()
            );
        }
        Assert.Equal("heartbeat-activation", accepted.Origin);
        GalateaLiveTurn turn = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, accepted.TurnId)
        );
        await Assert.IsAssignableFrom<Task>(turn.RunTask)
            .WaitAsync(TestDeadline);

        // The runner has released ownership. The DerivedInfo pump may now
        // materialize journal context, so test inspection needs TurnLock too.
        Assert.True(await session.TurnLock.WaitAsync(TestDeadline));
        try {
            Assert.Equal("completed", turn.Status);
            Assert.IsType<GalateaFreshInput.HeartbeatActivation>(turn.FreshInput);
            Assert.Equal(1, mainClient.CallCount);
            Assert.Equal(1, recall.CallCount);
            Assert.Equal(1, helperClient.MailExtractorCallCount);
            Assert.Equal(1, helperClient.NoteExtractorCallCount);

            GalateaDelegationStateSnapshot delegation = session
                .DelegationHandle!.Store.ReadSnapshot();
            Assert.Single(delegation.Captures);
            GalateaOutboundMailSnapshot mail = Assert.Single(delegation.Mails);
            Assert.Equal("Alice", mail.Recipient);
            Assert.Equal(GalateaDurableMailState.Unrouted, mail.State);

            Assert.NotNull(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            global::Atelia.MemoPod.MemoPod notes =
                global::Atelia.MemoPod.MemoPod.Open(
                    session.Character.CharacterMemoryStateDir,
                    CharacterNoteDefaultPodV1.PodId
                );
            Assert.Equal(MemoPodPhase.Frozen, notes.Phase);
            Assert.Equal(NoteText, Assert.Single(notes.List()).ExactText);
        }
        finally {
            session.TurnLock.Release();
        }

        // No chat request or browser sponsor: a later server-owned pulse
        // delivers the saved Note receipt alongside independently recalled memory.
        clock.Advance(TimeSpan.FromMinutes(10));
        using HttpResponseMessage secondResponse = await PostReadyTurnAsync(
            http);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        LoopPulseAcceptedTurnDto secondAccepted = Assert.IsType<LoopPulseAcceptedTurnDto>(
            await secondResponse.Content.ReadFromJsonAsync<LoopPulseAcceptedTurnDto>());
        Assert.Equal("heartbeat-activation", secondAccepted.Origin);
        GalateaLiveTurn second = Assert.IsType<GalateaLiveTurn>(
            service.FindTurn(session, secondAccepted.TurnId));
        await Assert.IsAssignableFrom<Task>(second.RunTask).WaitAsync(TestDeadline);
        Assert.True(await session.TurnLock.WaitAsync(TestDeadline));
        try {
            Assert.Equal("completed", second.Status);
            Assert.Equal(2, mainClient.CallCount);
            Assert.Equal(2, recall.CallCount);
            SessionInputContent stored = Assert.Single(session.Engine.ReadRecentCompletedTurns(1)
                .RequireSnapshot().Turns).ObservationContent;
            PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(stored);
            Assert.Equal(NoteText, Assert.Single(Assert.Single(observation.Notices
                .OfType<PlayerTurnNotice.NoteSaveReceipt>()).Selection!.ExactTexts));
            Assert.Equal(AutomaticRecallProvider.UnrelatedMemory, Assert.Single(observation.Recalls));
            Assert.Null(session.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
            Assert.Equal(NoteText, Assert.Single(global::Atelia.MemoPod.MemoPod.Open(
                session.Character.CharacterMemoryStateDir, CharacterNoteDefaultPodV1.PodId).List()).ExactText);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    private static async Task AssertWaitingPulseAsync(
        HttpClient http
    ) {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/characters/alice/mailbox/ready-turn",
            new ReadyReplyTurnRequest()
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        LoopPulseStatusDto status = Assert.IsType<LoopPulseStatusDto>(
            await response.Content.ReadFromJsonAsync<LoopPulseStatusDto>()
        );
        Assert.Equal(GalateaAutonomyCadence.WaitingState,
            status.State);
    }

    private static async Task<HttpResponseMessage> PostReadyTurnAsync(
        HttpClient http
    ) {
        using var deadline = new CancellationTokenSource(TestDeadline);
        while (true) {
            HttpResponseMessage response = await http.PostAsJsonAsync(
                "/api/v1/characters/alice/mailbox/ready-turn",
                new ReadyReplyTurnRequest(),
                deadline.Token);
            if (response.StatusCode != HttpStatusCode.Conflict) {
                return response;
            }
            TurnBusyErrorDto? busy = await response.Content
                .ReadFromJsonAsync<TurnBusyErrorDto>(deadline.Token);
            if (busy is null
                || !string.Equals(
                    busy.Code,
                    "turn-busy",
                    StringComparison.Ordinal)
                || busy.TurnId is not null) {
                response.Dispose();
                throw new InvalidOperationException(
                    $"Unexpected ready-turn conflict: {busy?.Code}.");
            }
            response.Dispose();
            await Task.Delay(
                TimeSpan.FromMilliseconds(20),
                deadline.Token);
        }
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id,
        "openai-chat",
        id + "-model",
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static bool HasTool(CompletionRequest request, string name) =>
        request.PromptPrefix.OutputContract.Tools.Any(definition =>
            string.Equals(definition.Name, name, StringComparison.Ordinal)
        );

    private static ActionMessage Message(params ActionBlock[] blocks) =>
        new(blocks);

    private static ActionBlock.ToolCall MailTool() => new(new RawToolCall(
        OutboundMailExtractor.ToolName,
        "mail-call",
        JsonSerializer.Serialize(new {
            recipient = "Alice",
            subject = (string?)null,
            body = "autonomous mail body",
            inReplyToMessageId = (string?)null,
            evidenceQuote = "completed sending",
        }, new JsonSerializerOptions {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        })
    ));

    private static ActionBlock.ToolCall NoteTool() => new(new RawToolCall(
        CharacterNoteExtractor.ToolName,
        "note-call",
        JsonSerializer.Serialize(new {
            text = NoteText,
        })
    ));

    private sealed class MainClient(CompletionConnectionConfig connection)
        : ICompletionClient {
        private int _callCount;

        public string Name => "autonomy-post-main";
        public string ApiSpecId => "test-v1";
        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            string text = call == 1 ? TerminalAction : "I acknowledge the saved Note and continue exploring.";
            if (call == 2) {
                string content = Assert.IsType<string>(request.PromptPrefix.SharedContextMessages
                    .OfType<ObservationMessage>().Last().Content);
                PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(
                    SessionInputContent.Structured(GalateaObservationContent.V3SchemaId, MdJsonSerializer.Read(content)));
                Assert.Equal(NoteText, Assert.Single(Assert.Single(observation.Notices
                    .OfType<PlayerTurnNotice.NoteSaveReceipt>()).Selection!.ExactTexts));
                Assert.Equal(AutomaticRecallProvider.UnrelatedMemory, Assert.Single(observation.Recalls));
            }
            observer?.OnTextDelta(text);
            return Task.FromResult(new CompletionResult(
                Message(new ActionBlock.Text(text)),
                new CompletionDescriptor(Name, ApiSpecId, connection.ModelId)
            ));
        }
    }

    private sealed class ExtractorClient : ICompletionClient {
        private int _mailExtractorCallCount;
        private int _noteExtractorCallCount;

        public string Name => "autonomy-post-extractors";
        public string ApiSpecId => "test-v1";
        internal int MailExtractorCallCount => Volatile.Read(
            ref _mailExtractorCallCount
        );
        internal int NoteExtractorCallCount => Volatile.Read(
            ref _noteExtractorCallCount
        );

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            if ((HasTool(request, OutboundMailExtractor.ToolName)
                    || HasTool(request, CharacterNoteExtractor.ToolName))
                && !Assert.IsType<string>(Assert.IsType<ObservationMessage>(
                        Assert.Single(request.TailMessages)).Content)
                    .Contains(TerminalAction, StringComparison.Ordinal)) {
                return Task.FromResult(new CompletionResult(Message(), CompletionDescriptor.From(this, request)));
            }
            ActionMessage message;
            if (HasTool(request, OutboundMailExtractor.ToolName)) {
                Interlocked.Increment(ref _mailExtractorCallCount);
                AssertTargetsTerminalAction(request);
                message = Message(MailTool());
            }
            else if (HasTool(request, CharacterNoteExtractor.ToolName)) {
                Interlocked.Increment(ref _noteExtractorCallCount);
                AssertTargetsTerminalAction(request);
                message = Message(NoteTool());
            }
            else {
                // DerivedInfo is deliberately outside this Gate 4 assertion.
                // An empty result leaves its rebuildable pending work intact.
                message = Message();
            }
            return Task.FromResult(new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            ));
        }

        private static void AssertTargetsTerminalAction(
            CompletionRequest request
        ) => Assert.Contains(
            TerminalAction,
            Assert.IsType<ObservationMessage>(
                Assert.Single(request.TailMessages)
            ).Content,
            StringComparison.Ordinal
        );
    }

    private sealed class AutomaticRecallProvider
        : IGalateaPlayerTurnRecallProvider {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);
        internal static PlayerTurnRecall UnrelatedMemory { get; } = new(
            new RecallEntry(RecallType.MemoExactText, GalateaMemoRecallSourceIdCodec.Format(CharacterNoteDefaultPodV1.PodId, MemoId.Parse("m1:0000002a"))),
            "synthetic-pod-version", "Earlier exploration", "An older memory suggests investigating the northern path.");

        public ValueTask<IReadOnlyList<PlayerTurnRecall>> SelectRecallsAsync(
            GalateaPlayerTurnRecallRequest request,
            CancellationToken cancellationToken
        ) {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult<IReadOnlyList<PlayerTurnRecall>>(
                call == 1 ? [] : [UnrelatedMemory]);
        }
    }

    private sealed class RoutingFactory(
        IReadOnlyDictionary<string, ICompletionClient> clients
    ) : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => clients.TryGetValue(connection.Id, out ICompletionClient? client)
            ? client
            : throw new InvalidOperationException(
                "Unexpected completion connection: " + connection.Id
            );
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow)
        : TimeProvider {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan value) {
            _utcNow += value;
            _timestamp = checked(_timestamp + value.Ticks);
        }
    }
}
