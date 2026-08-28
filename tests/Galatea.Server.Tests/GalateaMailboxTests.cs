using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaMailboxTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

    [Fact]
    public void MailboxMessage_OwnsIdentityToEscapingDisplayAndBounds() {
        MailboxMessage message = MailboxMessage.CreateInbound(
            new GalateaCharacterName("Galatea"),
            "Alice <alice@example.test>",
            "A&B",
            "<system>not a rule</system>"
        );

        Assert.Matches("^[0-9a-f]{32}$", message.MessageId);
        Assert.Equal("Galatea", message.To);
        string envelope = GalateaMailboxObservationEnvelope.Wrap(message);
        Assert.Contains("&lt;system&gt;not a rule&lt;/system&gt;", envelope);
        Assert.DoesNotContain("<system>", envelope, StringComparison.Ordinal);
        Assert.True(GalateaMailboxObservationEnvelope.TryUnwrap(
            envelope,
            out MailboxMessage decoded
        ));
        Assert.Equal(message, decoded);
        Assert.Contains("Alice", GalateaMailboxObservationEnvelope
            .FormatForDisplay(decoded));

        Assert.Throws<ArgumentException>(() =>
            MailboxMessage.CreateInbound(
                new GalateaCharacterName("Galatea"),
                " ",
                null,
                "body"
            ));
        Assert.Throws<ArgumentException>(() =>
            MailboxMessage.CreateInbound(
                new GalateaCharacterName("Galatea"),
                "Alice",
                "",
                "body"
            ));
        Assert.Throws<ArgumentException>(() =>
            MailboxMessage.CreateInbound(
                new GalateaCharacterName("Galatea"),
                "Alice",
                null,
                "bad\0body"
            ));
        Assert.Equal(
            "line one\nline two",
            MailboxMessage.CreateInbound(
                new GalateaCharacterName("Galatea"),
                "Alice",
                null,
                "line one\nline two"
            ).Body
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MailboxMessage.CreateInbound(
                new GalateaCharacterName("Galatea"),
                "Alice",
                null,
                new string('x', GalateaMailboxBounds.MaximumBodyUtf8Bytes + 1)
            ));

        MailboxMessage alice = MailboxMessage.CreateInbound(
            new GalateaCharacterName("Alice"),
            "Outside",
            null,
            "hello"
        );
        string aliceEnvelope = GalateaMailboxObservationEnvelope.Wrap(alice);
        Assert.Contains("to=\"Alice\"", aliceEnvelope,
            StringComparison.Ordinal);
        Assert.True(GalateaMailboxObservationEnvelope.TryUnwrap(
            aliceEnvelope,
            out MailboxMessage decodedAlice
        ));
        Assert.Equal("Alice", decodedAlice.To);
        Assert.Equal(aliceEnvelope,
            GalateaMailboxObservationEnvelope.Wrap(decodedAlice));
        Assert.False(GalateaMailboxObservationEnvelope.TryUnwrap(
            aliceEnvelope.Replace(
                "to=\"Alice\"",
                "to=\"Bad[Name]\"",
                StringComparison.Ordinal
            ),
            out _
        ));
    }

    [Fact]
    public async Task Extractor_CharacterPromptAndContractAreImmutablePerCharacter() {
        var client = new QueueClient(
            _ => Message(),
            _ => Message()
        );
        CompletionConnectionConfig connection = Connection("extractor");
        IReadOnlyDictionary<string, GalateaUserConfig> users = new[] {
            User("alice", "Alice"),
            User("bob", "Bob"),
            User("alice-again", "Alice")
        }.ToDictionary(static value => value.UserId, StringComparer.Ordinal);
        IReadOnlyDictionary<string, IOutboundMailExtractor> extractors =
            GalateaHostService.CreateOutboundMailExtractors(
                users,
                connection,
                () => client
            );
        IOutboundMailExtractor alice = extractors["alice"];
        IOutboundMailExtractor bob = extractors["bob"];
        IOutboundMailExtractor aliceAgain = extractors["alice-again"];

        _ = new OutboundMailExtractor(
            new GalateaCharacterName("ConstructionProbe"),
            connection,
            () => throw new Xunit.Sdk.XunitException(
                "Contract construction must not create the shared client."
            )
        );

        Assert.Empty(client.Requests);
        Assert.Equal(alice.ContractId, aliceAgain.ContractId);
        Assert.NotEqual(alice.ContractId, bob.ContractId);
        Assert.Matches(
            "^atelia\\.galatea\\.outbound-mail-extractor\\.v2\\.[0-9a-f]{64}$",
            alice.ContractId
        );

        _ = await alice.ExtractAsync(
            "[Alice] only drafted a note.",
            CancellationToken.None
        );
        _ = await bob.ExtractAsync(
            "[Bob] only drafted a note.",
            CancellationToken.None
        );

        Assert.Equal(2, client.Requests.Count);
        CompletionRequest aliceRequest = client.Requests[0];
        CompletionRequest bobRequest = client.Requests[1];
        Assert.Contains("[Alice]", aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Galatea",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains("[Bob]", bobRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("${characterName}",
            aliceRequest.PromptPrefix.SystemPrompt,
            StringComparison.Ordinal);
        string schema = ToolSchemaTextRenderer.RenderDefinitions(
            aliceRequest.PromptPrefix.OutputContract.Tools
        );
        Assert.Contains("configured story character", schema,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Galatea", schema,
            StringComparison.Ordinal);
        ObservationMessage aliceTail = Assert.IsType<ObservationMessage>(
            Assert.Single(aliceRequest.TailMessages)
        );
        ObservationMessage bobTail = Assert.IsType<ObservationMessage>(
            Assert.Single(bobRequest.TailMessages)
        );
        Assert.Contains("mails that Alice actually sent", aliceTail.Content,
            StringComparison.Ordinal);
        Assert.Contains("mails that Bob actually sent", bobTail.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extractor_ReturnsZeroAndOrderedTypedMailsAndRejectsInvalidHeaders() {
        CompletionConnectionConfig connection = Connection("extractor");
        var client = new QueueClient(
            _ => Message(),
            _ => Message(
                Tool("c1", "Alice", "S1", "first body", null,
                    "sent first body"),
                Tool("c2", "Bob", null, "second body",
                    "0123456789abcdef0123456789abcdef",
                    "sent second body")
            ),
            _ => Message(
                Tool("c3", "Alice\nBcc", null, "body", null,
                    "sent body")
            ),
            _ => Message(
                Tool("c4", "Alice", "subject\u2028Injected", "body",
                    null, "sent body")
            )
        );
        var extractor = new OutboundMailExtractor(
            new GalateaCharacterName("Galatea"),
            connection,
            () => client
        );

        Assert.Empty(await extractor.ExtractAsync(
            "[Galatea] I only drafted a note.",
            CancellationToken.None
        ));
        string contract = client.Requests[0].PromptPrefix.SystemPrompt;
        Assert.Contains("composite GM carrier", contract,
            StringComparison.Ordinal);
        Assert.Contains("Plans, wishes, suggestions, drafts", contract,
            StringComparison.Ordinal);
        Assert.Contains("Never attribute another character's acts", contract,
            StringComparison.Ordinal);
        IReadOnlyList<SendMailIntent> mails = await extractor.ExtractAsync(
            "[Galatea] sent first body to Alice with S1; then sent second body to Bob replying to 0123456789abcdef0123456789abcdef.",
            CancellationToken.None
        );
        Assert.Equal(["Alice", "Bob"], mails.Select(static x => x.Recipient));
        Assert.Equal(["first body", "second body"], mails.Select(static x => x.Body));

        await Assert.ThrowsAsync<TextExtractionException>(() => extractor
            .ExtractAsync(
                "[Galatea] sent body to Alice\nBcc and sent body.",
                CancellationToken.None
            )
            .AsTask());
        await Assert.ThrowsAsync<TextExtractionException>(() => extractor
            .ExtractAsync(
                "[Galatea] sent body to Alice with subject\u2028Injected; sent body.",
                CancellationToken.None
            )
            .AsTask());
    }

    [Fact]
    public async Task TypedSemanticOutput_IsNotMechanicallyGroundedAgainstRawAction() {
        const string ReplyId = "0123456789abcdef0123456789abcdef";
        var client = new QueueClient(_ => Message(Tool(
                "semantic-output",
                "Codex",
                "Status",
                "Semantically extracted body.",
                ReplyId,
                "The model judged that Galatea completed the send."
            )));
        var extractor = new OutboundMailExtractor(
            new GalateaCharacterName("Galatea"),
            Connection("extractor"),
            () => client
        );

        SendMailIntent intent = Assert.Single(await extractor.ExtractAsync(
            "[Galatea] completed a visually formatted dispatch.",
            CancellationToken.None
        ));
        Assert.Equal("Codex", intent.Recipient);
        Assert.Equal("Status", intent.Subject);
        Assert.Equal("Semantically extracted body.", intent.Body);
        Assert.Equal(ReplyId, intent.InReplyToMessageId);
        Assert.Equal(
            "The model judged that Galatea completed the send.",
            intent.EvidenceQuote
        );
    }

    [Fact]
    public void VisibleRenderer_UsesOnlyTextOrderAndStripsWholeThinkBlocks() {
        var invocation = new CompletionDescriptor("n", "a", "m");
        string visible = GalateaVisibleActionTextRenderer.Render(
            new ActionMessage([
                new ActionBlock.Text("before <thi"),
                new ActionBlock.TextReasoningBlock("secret", invocation),
                new ActionBlock.Text("nk>hidden</think> after"),
                new ActionBlock.ToolCall(new RawToolCall("x", "c", "{}")),
            ])
        );

        Assert.Equal("before  after", visible);
    }

    [Theory]
    [InlineData("Alice\rBcc")]
    [InlineData("Alice\nBcc")]
    [InlineData("Alice\vBcc")]
    [InlineData("Alice\fBcc")]
    [InlineData("Alice\u0085Bcc")]
    [InlineData("Alice\u2028Bcc")]
    [InlineData("Alice\u2029Bcc")]
    public void MailHeaders_RejectEveryCodeOwnedLineBreak(string injected) {
        Assert.Throws<ArgumentException>(() =>
            MailboxMessage.CreateInbound(
                new GalateaCharacterName("Galatea"),
                injected,
                null,
                "body"
            ));
        Assert.Throws<ArgumentException>(() =>
            MailboxMessage.CreateInbound(
                new GalateaCharacterName("Galatea"),
                "Alice",
                injected,
                "body"
            ));

        string summary = GalateaMailboxText.SummarizeForLog(
            injected + new string('界', 200)
        );
        Assert.DoesNotContain('\r', summary);
        Assert.DoesNotContain('\n', summary);
        Assert.DoesNotContain('\v', summary);
        Assert.DoesNotContain('\f', summary);
        Assert.DoesNotContain('\u0085', summary);
        Assert.DoesNotContain('\u2028', summary);
        Assert.DoesNotContain('\u2029', summary);
        Assert.InRange(
            Encoding.UTF8.GetByteCount(summary),
            1,
            GalateaMailboxText.MaximumLogSummaryUtf8Bytes
        );
    }

    [Fact]
    public async Task InboundEndpoint_BypassesNormalizerPersistsTrustedMailAndCapturesCandidate() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractorConnection = Connection("mail-helper");
        const string Action = "[Galatea] 我把邮件发送给 Alice。主题 S1，完整正文是：hello Alice。发送完成。";
        var mainClient = new QueueClient(_ => Message(
            new ActionBlock.Text(Action)
        ));
        var extractorClient = new QueueClient(_ => Message(
            Tool("mail-1", "Alice", "S1", "hello Alice", null,
                "发送完成")
        ));
        var factory = new RoutingFactory(new Dictionary<string, ICompletionClient>(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractorConnection.Id] = extractorClient,
        });
        var normalizer = new RejectingNormalizer();
        await using GalateaTestHost host = GalateaTestHost.Create(
            factory,
            normalizer,
            connections: [main, extractorConnection],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractorConnection.Id
        );
        using HttpClient http = host.CreateClient();
        await Login(http);

        HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/mailbox/inbound",
            new { from = "Outside Alice", subject = "Question", body = "Please reply <carefully>." }
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        InboundMailboxAcceptedDto accepted = (await response.Content
            .ReadFromJsonAsync<InboundMailboxAcceptedDto>())!;
        Assert.Matches("^[0-9a-f]{32}$", accepted.MessageId);

        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        GalateaLiveTurn turn = service.FindTurn(session, accepted.TurnId)!;
        await turn.RunTask!.WaitAsync(Deadline);

        Assert.Equal("completed", turn.Status);
        Assert.Equal(0, normalizer.CallCount);
        Assert.Equal([main.Id, extractorConnection.Id], factory.CreatedIds);
        Assert.Equal([main.Id], service.Connections.Select(static x => x.Id));
        SessionCompletedTurnProjection persisted = Assert.Single(
            session.Engine.ReadRecentCompletedTurns(1)
                .RequireSnapshot().Turns
        );
        Assert.True(GalateaMailboxObservationEnvelope.TryUnwrap(
            persisted.ObservationContent,
            out MailboxMessage mail
        ));
        Assert.Equal(accepted.MessageId, mail.MessageId);
        Assert.Equal("Galatea", mail.To);
        Assert.Equal("Please reply <carefully>.", mail.Body);

        GalateaOutboundMailSnapshot candidate = Assert.Single(
            session.DelegationHandle!.Store.ReadSnapshot().Mails
        );
        Assert.Equal(
            EventAddressTextCodec.Format(persisted.TerminalAction.Address),
            candidate.SourceActionAddress
        );
        Assert.Equal("Alice", candidate.Recipient);
        Assert.Equal("hello Alice", candidate.Body);
        Assert.Equal(
            GalateaDurableMailState.Unrouted,
            candidate.State
        );

        RecentTurnsResponseDto recent = (await http.GetFromJsonAsync<
            RecentTurnsResponseDto>("/api/v1/recent-turns"))!;
        Assert.Null(recent.RewindLatestToken);
        Assert.Contains("Outside Alice", Assert.Single(recent.Turns).UserText);
        Assert.Contains("Please reply <carefully>.", recent.Turns[0].UserText);
    }

    [Fact]
    public async Task ExtractorGap_RetriesBeforeAdmissionAndUndoKeepsCapture() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractorConnection = Connection("mail-helper");
        const string Action = "[Galatea] sent body text to Alice and completed sending.";
        var mainClient = new QueueClient(
            _ => Message(new ActionBlock.Text(Action)),
            _ => Message(new ActionBlock.Text(Action))
        );
        var extractorClient = new QueueClient(
            _ => throw new IOException("extractor unavailable"),
            _ => Message(Tool(
                "mail-2", "Alice", null, "body text", null,
                "completed sending"
            )),
            _ => Message()
        );
        var factory = new RoutingFactory(new Dictionary<string, ICompletionClient>(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractorConnection.Id] = extractorClient,
        });
        await using GalateaTestHost host = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, extractorConnection],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractorConnection.Id
        );
        using HttpClient http = host.CreateClient();
        await Login(http);

        StartTurnResponseDto failedExtraction = await PostPlayerTurn(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice", CancellationToken.None);
        await service.FindTurn(session, failedExtraction.TurnId)!
            .RunTask!.WaitAsync(Deadline);
        Assert.Equal("failed",
            service.FindTurn(session, failedExtraction.TurnId)!.Status);
        Assert.Empty(session.DelegationHandle!.Store.ReadSnapshot().Captures);

        StartTurnResponseDto extracted = await PostPlayerTurn(http);
        await service.FindTurn(session, extracted.TurnId)!
            .RunTask!.WaitAsync(Deadline);
        Assert.Single(session.DelegationHandle.Store.ReadSnapshot().Mails);
        RecentTurnsResponseDto recent = (await http.GetFromJsonAsync<
            RecentTurnsResponseDto>("/api/v1/recent-turns"))!;
        Assert.NotNull(recent.RewindLatestToken);

        HttpResponseMessage pop = await http.PostAsJsonAsync(
            "/api/v1/chat/turns/pop-latest",
            new { rewindLatestToken = recent.RewindLatestToken }
        );
        Assert.Equal(HttpStatusCode.OK, pop.StatusCode);
        Assert.Equal(
            GalateaDurableMailState.Unrouted,
            Assert.Single(
                session.DelegationHandle.Store.ReadSnapshot().Mails
            ).State
        );
    }

    [Fact]
    public async Task ExtractionHasNoElapsedDeadlineAndWaitsForProviderCompletion() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig extractorConnection =
            Connection("mail-helper");
        const string Action =
            "[Galatea] sent body text to Alice and completed sending.";
        var mainClient = new QueueClient(
            _ => Message(new ActionBlock.Text(Action))
        );
        var extractorClient = new GatedClient(Message(Tool(
            "mail-gated",
            "Alice",
            null,
            "body text",
            null,
            "completed sending"
        )));
        var factory = new RoutingFactory(new Dictionary<
            string,
            ICompletionClient
        >(StringComparer.Ordinal) {
            [main.Id] = mainClient,
            [extractorConnection.Id] = extractorClient,
        });
        await using GalateaTestHost host = GalateaTestHost.Create(
            factory,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, extractorConnection],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: extractorConnection.Id
        );
        using HttpClient http = host.CreateClient();
        await Login(http);
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );

        StartTurnResponseDto turn = await PostPlayerTurn(http);
        GalateaLiveTurn liveTurn = service.FindTurn(session, turn.TurnId)!;
        await extractorClient.Entered.Task.WaitAsync(Deadline);
        Assert.False(liveTurn.RunTask!.IsCompleted);
        Assert.False(session.TurnLock.Wait(0));

        extractorClient.Release();
        await liveTurn.RunTask.WaitAsync(Deadline);
        Assert.Equal("completed",
            service.FindTurn(session, turn.TurnId)!.Status);
        Assert.Single(session.Engine.ReadRecentCompletedTurns(1)
            .RequireSnapshot().Turns);
        Assert.Single(
            session.DelegationHandle!.Store.ReadSnapshot().Mails
        );
        Assert.Equal(1, extractorClient.DispatchCount);
        Assert.Equal(0, extractorClient.CancellationCount);
        Assert.True(session.TurnLock.Wait(0));
        session.TurnLock.Release();
    }

    [Fact]
    public async Task InboundEndpoint_IsStrictBoundedAuthenticatedAndMaintenanceProtected() {
        var completion = new QueueClient(_ => Message());
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                ["test"] = completion,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection("test")]
        );
        using HttpClient http = host.CreateClient();

        HttpResponseMessage unauthenticated = await http.PostAsJsonAsync(
            "/api/v1/mailbox/inbound",
            new { from = "Alice", body = "hello" }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        await Login(http);

        HttpResponseMessage extra = await http.PostAsync(
            "/api/v1/mailbox/inbound",
            new StringContent(
                "{\"from\":\"Alice\",\"body\":\"hello\",\"to\":\"Mallory\"}",
                Encoding.UTF8,
                "application/json"
            )
        );
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        HttpResponseMessage blank = await http.PostAsJsonAsync(
            "/api/v1/mailbox/inbound",
            new { from = " ", body = "hello" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        HttpResponseMessage injectedFrom = await http.PostAsJsonAsync(
            "/api/v1/mailbox/inbound",
            new { from = "Alice\nBcc: Mallory", body = "hello" }
        );
        Assert.Equal(HttpStatusCode.BadRequest, injectedFrom.StatusCode);
        HttpResponseMessage injectedSubject = await http.PostAsJsonAsync(
            "/api/v1/mailbox/inbound",
            new {
                from = "Alice",
                subject = "hello\u2028Injected",
                body = "hello"
            }
        );
        Assert.Equal(HttpStatusCode.BadRequest,
            injectedSubject.StatusCode);
        HttpResponseMessage large = await http.PostAsJsonAsync(
            "/api/v1/mailbox/inbound",
            new {
                from = "Alice",
                body = new string(
                    'x',
                    GalateaMailboxBounds.MaximumBodyUtf8Bytes + 1
                )
            }
        );
        Assert.Equal(HttpStatusCode.BadRequest, large.StatusCode);

        await using GalateaTestHost maintenance = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient> {
                ["test"] = completion,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            maintenanceMode: true,
            connections: [Connection("test")]
        );
        using HttpClient maintenanceHttp = maintenance.CreateClient();
        await Login(maintenanceHttp);
        HttpResponseMessage blocked = await maintenanceHttp.PostAsJsonAsync(
            "/api/v1/mailbox/inbound",
            new { from = "Alice", body = "hello" }
        );
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
    }

    private static async Task<StartTurnResponseDto> PostPlayerTurn(
        HttpClient http
    ) {
        HttpResponseMessage response = await http.PostAsJsonAsync(
            "/api/v1/chat/turns",
            new { message = "please continue", connectionId = "test" }
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<
            StartTurnResponseDto>())!;
    }

    private static async Task Login(HttpClient http) {
        HttpResponseMessage response = await GalateaTestHost.LoginAsync(http);
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

    private static ActionBlock.ToolCall Tool(
        string callId,
        string recipient,
        string? subject,
        string body,
        string? replyId,
        string evidence
    ) => new(new RawToolCall(
        OutboundMailExtractor.ToolName,
        callId,
        JsonSerializer.Serialize(
            new {
                recipient,
                subject,
                body,
                inReplyToMessageId = replyId,
                evidenceQuote = evidence,
            },
            new JsonSerializerOptions {
                DefaultIgnoreCondition =
                    System.Text.Json.Serialization
                        .JsonIgnoreCondition.WhenWritingNull,
            }
        )
    ));

    private static SendMailIntent Intent(
        string recipient,
        string body,
        string evidence
    ) => new(recipient, null, body, null, evidence);

    private static GalateaUserConfig User(
        string userId,
        string characterName
    ) => new(
        userId,
        "password",
        new GalateaCharacterName(characterName),
        new GalateaPlayerName("Player"),
        "/tmp/session-" + userId,
        "/tmp/delegation-" + userId,
        GalateaSessionProvisioning.ExistingOnly,
        "system " + characterName
    );

    private static ActionMessage Message(params ActionBlock[] blocks) =>
        new(blocks);

    private sealed class QueueClient(
        params Func<CompletionRequest, ActionMessage>[] scripts
    ) : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, ActionMessage>>
            _scripts = new(scripts);

        public string Name => "galatea-mailbox-test";
        public string ApiSpecId => "test-v1";

        internal List<CompletionRequest> Requests { get; } = [];

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            Func<CompletionRequest, ActionMessage> script = _scripts.Dequeue();
            ActionMessage message = script(request);
            foreach (ActionBlock.Text text in message.Blocks
                         .OfType<ActionBlock.Text>()) {
                observer?.OnTextDelta(text.Content);
            }
            return Task.FromResult(new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            ));
        }
    }

    private sealed class GatedClient(ActionMessage message)
        : ICompletionClient {
        private int _dispatchCount;
        private int _cancellationCount;
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public string Name => "galatea-mailbox-gated";
        public string ApiSpecId => "test-v1";
        internal int DispatchCount => Volatile.Read(ref _dispatchCount);
        internal int CancellationCount => Volatile.Read(
            ref _cancellationCount
        );
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal void Release() => _release.TrySetResult();

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            _ = Interlocked.Increment(ref _dispatchCount);
            using CancellationTokenRegistration registration =
                cancellationToken.Register(
                () => _ = Interlocked.Increment(
                    ref _cancellationCount
                )
            );
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            );
        }
    }

    private sealed class RoutingFactory(
        IReadOnlyDictionary<string, ICompletionClient> clients
    ) : ICompletionClientFactory {
        internal List<string> CreatedIds { get; } = [];

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CreatedIds.Add(connection.Id);
            return clients[connection.Id];
        }
    }

    private sealed class RejectingNormalizer
        : IGalateaUserMessageNormalizer {
        internal int CallCount { get; private set; }

        public bool ShouldNormalize(string userMessage) {
            CallCount++;
            throw new InvalidOperationException(
                "Inbound mail must bypass normalization."
            );
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "Inbound mail must bypass normalization."
        );
    }
}
