using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteRuntimeTests {
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    private const string Action = """
        [Galatea] I sent mail body to Alice and completed sending.
        [Galatea] I completed recording my long-term Note: remember blue.
        """;
    private const string NoteText = "remember blue";
    private const string NoteEvidence =
        "I completed recording my long-term Note: remember blue.";

    [Fact]
    public async Task SharedClientOverlapsMailAndNoteThenReceiptAttachesOnce() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig helper = Connection("helper");
        var mainClient = new QueueClient(Message(
            new ActionBlock.Text(Action)
        ));
        var helperClient = new OverlapExtractorClient();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = mainClient,
                [helper.Id] = helperClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, helper],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: helper.Id,
            characterNoteExtractorConnectionId: helper.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "first",
                new GalateaTurnOptions(main.Id)
            );
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );

            await helperClient.BothEntered.Task.WaitAsync(Deadline);
            Assert.Equal(2, helperClient.MaximumActive);
            Assert.False(run.IsCompleted);
            helperClient.Release();
            await run.WaitAsync(Deadline);
            service.FinishTurn(session, turn);

            Assert.Equal("completed", turn.Status);
            Assert.Equal(2, helperClient.Requests.Count);
            Assert.All(helperClient.Requests, request => Assert.Contains(
                Action,
                Assert.IsType<ObservationMessage>(
                    Assert.Single(request.TailMessages)
                ).Content,
                StringComparison.Ordinal
            ));
            GalateaDelegationStateSnapshot durable = session
                .DelegationHandle!.Store.ReadSnapshot();
            Assert.Single(durable.Captures);
            Assert.Equal("Alice", Assert.Single(durable.Mails).Recipient);
            Assert.Equal(1, session.NoteRequestReceipts.Count);

            GalateaLiveTurn receiptTurn = service.StartTurn(
                session,
                "second",
                new GalateaTurnOptions(main.Id)
            );
            GalateaFreshInput.PlayerAction receiptInput = Assert.IsType<
                GalateaFreshInput.PlayerAction>(receiptTurn.FreshInput);
            Assert.IsType<PlayerTurnNotice.NoteRequestReceipt>(
                Assert.Single(receiptInput.Notices)
            );
            Assert.Equal(0, session.NoteRequestReceipts.Count);
            service.FinishTurn(session, receiptTurn);

            GalateaLiveTurn next = service.StartTurn(
                session,
                "third",
                new GalateaTurnOptions(main.Id)
            );
            Assert.Empty(Assert.IsType<GalateaFreshInput.PlayerAction>(
                next.FreshInput
            ).Notices);
            service.FinishTurn(session, next);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Theory]
    [InlineData(NoteOutcome.Zero)]
    [InlineData(NoteOutcome.ProviderFailure)]
    [InlineData(NoteOutcome.Invalid)]
    [InlineData(NoteOutcome.Timeout)]
    public async Task NoteBestEffortFailureMatrixKeepsMailAndMainSuccessful(
        NoteOutcome outcome
    ) {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new OutcomeNoteClient(outcome);
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new QueueClient(Message()),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        if (outcome == NoteOutcome.Timeout) {
            service.CharacterNoteExtractionDeadlineForTest =
                TimeSpan.FromMilliseconds(100);
        }

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "failure matrix",
                new GalateaTurnOptions(main.Id)
            );
            await service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, turn);

            Assert.Equal("completed", turn.Status);
            Assert.Single(session.DelegationHandle!.Store
                .ReadSnapshot().Captures);
            Assert.Equal(0, session.NoteRequestReceipts.Count);
            if (outcome == NoteOutcome.Timeout) {
                Assert.True(noteClient.CancellationObserved);
            }
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task CompletedNoteIsNotRetimedOutWhileMailFinishesLater() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var mailClient = new GatedMessageClient(Message());
        var noteClient = new QueueClient(Message(NoteTool()));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = mailClient,
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        service.CharacterNoteExtractionDeadlineForTest =
            TimeSpan.FromMilliseconds(100);

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "slow mail",
                new GalateaTurnOptions(main.Id)
            );
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
            await mailClient.Entered.Task.WaitAsync(Deadline);
            await WaitUntilAsync(() => noteClient.DispatchCount == 1);
            await Task.Delay(200);
            mailClient.Release();
            await run.WaitAsync(Deadline);
            service.FinishTurn(session, turn);

            Assert.Equal("completed", turn.Status);
            Assert.Equal(1, session.NoteRequestReceipts.Count);
        }
        finally {
            mailClient.Release();
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task MailFailureCancelsAndDrainsBlockedNote() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new BlockingClient();
        var mailClient = new FailAfterSignalClient(noteClient.Entered.Task);
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = mailClient,
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "mail failure",
            new GalateaTurnOptions(main.Id)
        );
        try {
            GalateaTurnException failure = await Assert.ThrowsAsync<
                GalateaTurnException>(() => service.RunTurnAsync(
                    session,
                    turn,
                    CancellationToken.None
                ));

            Assert.Equal("delegation-extraction-unavailable",
                failure.FailureReason);
            await noteClient.Drained.Task.WaitAsync(Deadline);
            Assert.Equal(0, noteClient.ActiveCalls);
            Assert.True(noteClient.CancellationObserved);
            Assert.Empty(session.DelegationHandle!.Store
                .ReadSnapshot().Captures);
            Assert.Equal(0, session.NoteRequestReceipts.Count);
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task CallerCancellationDrainsBothExtractorsAndPropagates() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var mailClient = new BlockingClient();
        var noteClient = new BlockingClient();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = mailClient,
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        using var callerCts = new CancellationTokenSource();

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "cancel",
            new GalateaTurnOptions(main.Id)
        );
        try {
            Task run = service.RunTurnAsync(
                session,
                turn,
                callerCts.Token
            );
            await mailClient.Entered.Task.WaitAsync(Deadline);
            await noteClient.Entered.Task.WaitAsync(Deadline);
            callerCts.Cancel();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run
            );
            await mailClient.Drained.Task.WaitAsync(Deadline);
            await noteClient.Drained.Task.WaitAsync(Deadline);
            Assert.Equal(0, mailClient.ActiveCalls);
            Assert.Equal(0, noteClient.ActiveCalls);
            Assert.Equal(0, session.NoteRequestReceipts.Count);
        }
        finally {
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task HeadChangeAfterMailCaptureDropsSuccessfulNoteReceipt() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new GatedNoteClient();
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new QueueClient(Message()),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);

        await session.TurnLock.WaitAsync();
        GalateaLiveTurn turn = service.StartTurn(
            session,
            "head fence",
            new GalateaTurnOptions(main.Id)
        );
        try {
            Task run = service.RunTurnAsync(
                session,
                turn,
                CancellationToken.None
            );
            await noteClient.Entered.Task.WaitAsync(Deadline);
            await WaitUntilAsync(
                () => session.DelegationHandle!.Store
                    .ReadSnapshot().Captures.Count == 1
            );
            EventAddress action = session.Engine.ReadCurrentHead()
                ?? throw new Xunit.Sdk.XunitException(
                    "The completed Action head is unavailable."
                );
            _ = Assert.IsType<SessionTurnRetractionResult.Moved>(
                session.Engine.RewindLatestCompletedTurn(action)
            );
            noteClient.Release();

            GalateaTurnException failure = await Assert.ThrowsAsync<
                GalateaTurnException>(() => run);
            Assert.Equal("delegation-state-changed", failure.FailureReason);
            Assert.Single(session.DelegationHandle!.Store
                .ReadSnapshot().Captures);
            Assert.Equal(0, session.NoteRequestReceipts.Count);
        }
        finally {
            noteClient.Release();
            service.FinishTurn(session, turn);
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task QueueIsClaimedOnlyByOrdinaryEmptyPlayerTurn() {
        CompletionConnectionConfig main = Connection("test");
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message()),
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main]
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        CharacterNoteRequestReceipt receipt = CreateReceipt();
        Assert.True(session.NoteRequestReceipts.TryEnqueue(receipt));

        await session.TurnLock.WaitAsync();
        try {
            Assert.IsType<GalateaReadyReplyTurnStartResult.Empty>(
                service.StartReadyReplyTurn(
                    session,
                    new GalateaTurnOptions(main.Id)
                )
            );
            Assert.Equal(1, session.NoteRequestReceipts.Count);

            GalateaLiveTurn inbound = service.StartInboundMailTurn(
                session,
                MailboxMessage.CreateInbound(
                    session.User.CharacterName,
                    "outside",
                    null,
                    "body"
                ),
                new GalateaTurnOptions(main.Id)
            );
            Assert.Equal(1, session.NoteRequestReceipts.Count);
            service.FinishTurn(session, inbound);

            GalateaLiveTurn recovery = service.StartRecovery(
                session,
                new GalateaTurnOptions(
                    main.Id,
                    GalateaTurnMode.Resume
                )
            );
            Assert.Equal(1, session.NoteRequestReceipts.Count);
            service.FinishTurn(session, recovery);

            GalateaLiveTurn ordinary = service.StartTurn(
                session,
                "ordinary",
                new GalateaTurnOptions(main.Id)
            );
            Assert.Same(
                receipt.Notice,
                Assert.Single(Assert.IsType<GalateaFreshInput.PlayerAction>(
                    ordinary.FreshInput
                ).Notices)
            );
            Assert.Equal(0, session.NoteRequestReceipts.Count);
            service.FinishTurn(session, ordinary);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task CreatedReplyCutoffDoesNotClaimQueuedNoteReceipt() {
        CompletionConnectionConfig main = Connection("test");
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message()),
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main]
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        ProduceReadyReply(session);
        CharacterNoteRequestReceipt receipt = CreateReceipt();
        Assert.True(session.NoteRequestReceipts.TryEnqueue(receipt));

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn turn = service.StartTurn(
                session,
                "ordinary with reply",
                new GalateaTurnOptions(main.Id)
            );
            GalateaFreshInput.PlayerAction input = Assert.IsType<
                GalateaFreshInput.PlayerAction>(turn.FreshInput);
            Assert.IsType<PlayerTurnNotice.Reply>(
                Assert.Single(input.Notices)
            );
            Assert.Equal(1, session.NoteRequestReceipts.Count);
            turn.DurableReplyLease!.RollbackBeforeEffect();
            service.FinishTurn(session, turn);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task RecoveryCompletionRunsNoteButAdmissionDoesNot() {
        CompletionConnectionConfig main = Connection("test");
        CompletionConnectionConfig mail = Connection("mail");
        CompletionConnectionConfig note = Connection("note");
        var noteClient = new QueueClient(Message(NoteTool()));
        await using GalateaTestHost host = GalateaTestHost.Create(
            new RoutingFactory(new Dictionary<string, ICompletionClient>(
                StringComparer.Ordinal
            ) {
                [main.Id] = new QueueClient(Message(
                    new ActionBlock.Text(Action)
                )),
                [mail.Id] = new QueueClient(Message()),
                [note.Id] = noteClient,
            }),
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [main, mail, note],
            selectableConnectionIds: [main.Id],
            outboundMailExtractorConnectionId: mail.Id,
            characterNoteExtractorConnectionId: note.Id
        );
        (GalateaHostService service, UserSessionHost session) =
            await GetRuntimeAsync(host);
        Assert.Equal(0, noteClient.DispatchCount);
        EventAddress observationHead = session.Engine.AppendObservation(
            GalateaHostService.WrapUserMessageForEngine(
                "recover",
                DateTimeOffset.UnixEpoch
            )
        );

        await session.TurnLock.WaitAsync();
        try {
            GalateaLiveTurn recovery = service.StartRecovery(
                session,
                new GalateaTurnOptions(
                    main.Id,
                    GalateaTurnMode.Resume,
                    ExpectedHead: observationHead
                )
            );
            await service.RunTurnAsync(
                    session,
                    recovery,
                    CancellationToken.None
                )
                .WaitAsync(Deadline);
            service.FinishTurn(session, recovery);

            Assert.Equal(1, noteClient.DispatchCount);
            Assert.Equal(1, session.NoteRequestReceipts.Count);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    private static async Task<(GalateaHostService Service,
        UserSessionHost Session)> GetRuntimeAsync(GalateaTestHost host) {
        GalateaHostService service = host.Factory.Services
            .GetRequiredService<GalateaHostService>();
        UserSessionHost session = await service.GetSessionAsync(
            "alice",
            CancellationToken.None
        );
        return (service, session);
    }

    private static CharacterNoteRequestReceipt CreateReceipt() {
        Assert.True(CharacterNoteRequestReceipt.TryCreate(
            [new CharacterNoteIntent("queued note", "completed")],
            out CharacterNoteRequestReceipt? receipt
        ));
        return receipt;
    }

    private static void ProduceReadyReply(UserSessionHost session) {
        const string VisibleAction = "ready reply source";
        GalateaDelegationSqliteStore store = session.DelegationHandle!.Store;
        string sourceAction = EventAddressTextCodec.Format(
            session.Engine.ReadCurrentHead()
                ?? throw new Xunit.Sdk.XunitException(
                    "The test session has no current head."
                )
        );
        GalateaDelegationCaptureResult captured = store.CaptureActionBatch(
            new GalateaDelegationCaptureRequest(
                sourceAction,
                Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(VisibleAction)
                )).ToLowerInvariant(),
                Encoding.UTF8.GetByteCount(VisibleAction),
                "runtime-test-extractor-contract",
                [new SendMailIntent(
                    GalateaDelegateConfigReader.CanonicalRecipient,
                    Subject: null,
                    Body: "task",
                    InReplyToMessageId: null,
                    EvidenceQuote: "evidence"
                )]
            )
        );
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        GalateaRouteBindingSnapshot binding = store.BeginThreadBinding(
            "runtime-test-binding",
            snapshot.Route.Revision
        );
        _ = store.CompleteThreadBinding(
            binding.BindingOperationId!,
            "runtime-test-thread",
            binding.Revision
        );
        snapshot = store.ReadSnapshot();
        string dispatchId = Assert.Single(captured.DispatchIds);
        GalateaOutboundMailSnapshot mail = snapshot.Mails.Single(value =>
            string.Equals(
                value.DispatchId,
                dispatchId,
                StringComparison.Ordinal
            )
        );
        GalateaOutboundMailSnapshot started = store.StartQueuedMail(
            dispatchId,
            mail.Revision,
            snapshot.Route.Revision
        );
        _ = store.RecordCompletedMail(
            dispatchId,
            started.Revision,
            "runtime-test-thread",
            "runtime-test-turn",
            "ready reply"
        );
    }

    private static async Task WaitUntilAsync(Func<bool> condition) {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!condition()) {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id,
        "openai-chat",
        string.Equals(id, "test", StringComparison.Ordinal)
            ? "model-a"
            : id + "-model",
        "openai-chat/strict",
        "http://localhost:8000/",
        ApiKey: "test-key"
    );

    private static bool HasTool(CompletionRequest request, string name) =>
        request.PromptPrefix.OutputContract.Tools.Any(definition =>
            string.Equals(definition.Name, name, StringComparison.Ordinal)
        );

    private static ActionBlock.ToolCall MailTool() => new(new RawToolCall(
        OutboundMailExtractor.ToolName,
        "mail-call",
        JsonSerializer.Serialize(new {
            recipient = "Alice",
            subject = (string?)null,
            body = "mail body",
            inReplyToMessageId = (string?)null,
            evidenceQuote = "completed sending",
        }, new JsonSerializerOptions {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        })
    ));

    private static ActionBlock.ToolCall NoteTool(
        string exactText = NoteText,
        string evidenceQuote = NoteEvidence
    ) => new(new RawToolCall(
        CharacterNoteExtractor.ToolName,
        "note-call",
        JsonSerializer.Serialize(new { exactText, evidenceQuote })
    ));

    private static ActionMessage Message(params ActionBlock[] blocks) =>
        new(blocks);

    public enum NoteOutcome {
        Zero,
        ProviderFailure,
        Invalid,
        Timeout,
    }

    private sealed class OutcomeNoteClient(NoteOutcome outcome)
        : ICompletionClient {
        private int _cancellationObserved;

        public string Name => "character-note-outcome";
        public string ApiSpecId => "test-v1";
        internal bool CancellationObserved =>
            Volatile.Read(ref _cancellationObserved) != 0;

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            ActionMessage message;
            switch (outcome) {
                case NoteOutcome.Zero:
                    message = Message();
                    break;
                case NoteOutcome.ProviderFailure:
                    throw new IOException("note provider unavailable");
                case NoteOutcome.Invalid:
                    message = Message(NoteTool("not grounded", NoteEvidence));
                    break;
                case NoteOutcome.Timeout:
                    try {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            cancellationToken
                        );
                    }
                    catch (OperationCanceledException) {
                        Interlocked.Exchange(ref _cancellationObserved, 1);
                        throw;
                    }
                    throw new Xunit.Sdk.XunitException(
                        "Infinite delay unexpectedly completed."
                    );
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            );
        }
    }

    private sealed class OverlapExtractorClient : ICompletionClient {
        private readonly ConcurrentQueue<CompletionRequest> _requests = new();
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _active;
        private int _maximumActive;

        public string Name => "overlap-extractors";
        public string ApiSpecId => "test-v1";
        internal TaskCompletionSource BothEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal int MaximumActive => Volatile.Read(ref _maximumActive);
        internal IReadOnlyList<CompletionRequest> Requests =>
            _requests.ToArray();

        internal void Release() => _release.TrySetResult();

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            _requests.Enqueue(request);
            int active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            if (active == 2) { BothEntered.TrySetResult(); }
            try {
                await _release.Task.WaitAsync(cancellationToken);
                ActionMessage message = HasTool(
                    request,
                    OutboundMailExtractor.ToolName
                )
                    ? Message(MailTool())
                    : HasTool(request, CharacterNoteExtractor.ToolName)
                        ? Message(NoteTool())
                        : throw new Xunit.Sdk.XunitException(
                            "Unexpected extractor request."
                        );
                return new CompletionResult(
                    message,
                    CompletionDescriptor.From(this, request)
                );
            }
            finally {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int candidate) {
            int current;
            while (candidate > (current = Volatile.Read(
                       ref _maximumActive))) {
                if (Interlocked.CompareExchange(
                        ref _maximumActive,
                        candidate,
                        current) == current) {
                    return;
                }
            }
        }
    }

    private sealed class BlockingClient : ICompletionClient {
        private int _activeCalls;
        private int _cancellationObserved;

        public string Name => "blocking-extractor";
        public string ApiSpecId => "test-v1";
        internal int ActiveCalls => Volatile.Read(ref _activeCalls);
        internal bool CancellationObserved =>
            Volatile.Read(ref _cancellationObserved) != 0;
        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal TaskCompletionSource Drained { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            Interlocked.Increment(ref _activeCalls);
            Entered.TrySetResult();
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new Xunit.Sdk.XunitException(
                    "Infinite delay unexpectedly completed."
                );
            }
            catch (OperationCanceledException) {
                Interlocked.Exchange(ref _cancellationObserved, 1);
                throw;
            }
            finally {
                if (Interlocked.Decrement(ref _activeCalls) == 0) {
                    Drained.TrySetResult();
                }
            }
        }
    }

    private sealed class FailAfterSignalClient(Task signal)
        : ICompletionClient {
        public string Name => "mail-failure";
        public string ApiSpecId => "test-v1";

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            await signal.WaitAsync(cancellationToken);
            throw new IOException("mail extractor unavailable");
        }
    }

    private sealed class GatedNoteClient : ICompletionClient {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public string Name => "gated-note";
        public string ApiSpecId => "test-v1";
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
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new CompletionResult(
                Message(NoteTool()),
                CompletionDescriptor.From(this, request)
            );
        }
    }

    private sealed class GatedMessageClient(ActionMessage message)
        : ICompletionClient {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public string Name => "gated-message";
        public string ApiSpecId => "test-v1";
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
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new CompletionResult(
                message,
                CompletionDescriptor.From(this, request)
            );
        }
    }

    private sealed class QueueClient(params ActionMessage[] messages)
        : ICompletionClient {
        private readonly Queue<ActionMessage> _messages = new(messages);
        private readonly object _gate = new();
        private int _dispatchCount;

        public string Name => "queued-runtime";
        public string ApiSpecId => "test-v1";
        internal int DispatchCount => Volatile.Read(ref _dispatchCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            ActionMessage message;
            lock (_gate) {
                message = _messages.Dequeue();
            }
            Interlocked.Increment(ref _dispatchCount);
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

    private sealed class RoutingFactory(
        IReadOnlyDictionary<string, ICompletionClient> clients
    ) : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => clients[connection.Id];
    }
}
