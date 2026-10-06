using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaStructuredDeliveryRewindGateTests {
    [Fact]
    public async Task ActualHost_BothReceiptPreviewsReachProviderAndFullBusinessBodiesStaySaved() {
        var provider = new NoteExtractorProvider();
        await using GalateaTestHost fixture = CreateHost(provider);
        GalateaHostService service = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try {
            var seed = await SeedDualReceiptsAsync(session);
            GalateaLiveTurn turn = service.StartTurn(session, "Continue after my two submissions.",
                new GalateaTurnOptions("test"), GalateaDelegateTestConfiguration.PlayerSender);
            await service.RunTurnAsync(session, turn, CancellationToken.None);
            service.FinishTurn(session, turn);
            Assert.Equal("completed", turn.Status);
            Assert.Equal(1, provider.MainCalls);
            string currentProviderObservation = Assert.IsType<string>(provider.CurrentObservation);
            // The source Action may still contribute its original complete
            // text elsewhere in request history. Check this exact new input.
            Assert.DoesNotContain(ActionReceiptDeliveryFixture.NoteMiddle, currentProviderObservation, StringComparison.Ordinal);
            Assert.DoesNotContain(ActionReceiptDeliveryFixture.MailMiddle, currentProviderObservation, StringComparison.Ordinal);
            Assert.Contains(ActionReceiptPreview.Create(ActionReceiptDeliveryFixture.NoteText), currentProviderObservation, StringComparison.Ordinal);
            Assert.Contains(ActionReceiptPreview.Create(ActionReceiptDeliveryFixture.MailText), currentProviderObservation, StringComparison.Ordinal);
            SessionCompletedTurnProjection completed = session.Engine.ReadRecentCompletedTurns(1).RequireSnapshot().Turns.Single();
            Assert.Equal(GalateaInputProjector.Instance.Project(completed.ObservationContent), currentProviderObservation);
            Assert.Collection(GalateaObservationContent.ReadPlayerTurn(completed.ObservationContent).Notices,
                notice => Assert.IsType<MailReceiptBatch>(Assert.IsType<PlayerTurnNotice.ActionReceipt>(notice).Batch),
                notice => Assert.IsType<NoteReceiptBatch>(Assert.IsType<PlayerTurnNotice.ActionReceipt>(notice).Batch));
            Assert.Equal(ActionReceiptDeliveryFixture.NoteText, Assert.Single(global::Atelia.MemoPod.MemoPod.Open(
                session.Character.CharacterMemoryStateDir, CharacterNoteDefaultPodV1.PodId).List()).ExactText);
            Assert.Equal(ActionReceiptDeliveryFixture.MailText, Assert.Single(session.DelegationHandle!.Store.ReadSnapshot().Mails).Body);
            Assert.Equal(ActionReceiptDeliveryState.Delivered, session.CharacterMemoryReconciler!.ReadReceiptDeliveryExact(seed.Note.SourceActionAddress)!.State);
            Assert.Equal(ActionReceiptDeliveryState.Delivered, session.DelegationHandle.Store.ReadReceiptDeliveryExact(seed.Mail.SourceActionAddress)!.State);
        }
        finally { session.TurnLock.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormalUndo_CompletesRemainingOwnerBeforeHeadMovesAndColdReopenDoesNotReissue(bool mailFirst) {
        var provider = new NoteExtractorProvider();
        await using GalateaTestHost fixture = CreateHost(provider, deleteFilesOnDispose: false);
        GalateaHostService service = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try {
            var seed = await SeedDualReceiptsAsync(session);
            EventAddress observation = BindBothAndAppend(session, seed);
            EventAddress terminal = session.Engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("Receipt turn completed.")]), ActionReceiptDeliveryFixture.Invocation);
            ActionReceiptDelivery.Reconcile(mailFirst ? session.DelegationHandle!.Store : session.CharacterMemoryReconciler!.ReceiptDeliveryStore, session.Engine);
            Assert.Equal(ActionReceiptDeliveryState.ObservationBound, (mailFirst
                ? session.CharacterMemoryReconciler!.ReadBoundReceiptDelivery() : session.DelegationHandle!.Store.ReadBoundReceiptDelivery())!.State);
            GalateaPreparedPopLatestTurn undo = Assert.IsType<GalateaPreparedPopLatestTurn>(
                service.PrepareAndCommitPopLatestTurn(session, terminal));
            Assert.Equal(seed.Source, session.Engine.ReadCurrentHead());
            Assert.Equal("continue after both receipts", undo.PoppedUserText);
            Assert.Equal(EventAddressTextCodec.Format(observation), session.DelegationHandle!.Store.ReadReceiptDeliveryExact(seed.Mail.SourceActionAddress)!.ObservationAddress);
            Assert.Equal(EventAddressTextCodec.Format(observation), session.CharacterMemoryReconciler!.ReadReceiptDeliveryExact(seed.Note.SourceActionAddress)!.ObservationAddress);
            Assert.Null(session.DelegationHandle.Store.ReadPendingReceiptDelivery());
            Assert.Null(session.CharacterMemoryReconciler.ReadPendingReceiptDelivery());
            Assert.Null(session.DelegationHandle.Store.ReadBoundReceiptDelivery());
            Assert.Null(session.CharacterMemoryReconciler.ReadBoundReceiptDelivery());
        }
        finally { session.TurnLock.Release(); }
        await fixture.DisposeAsync();
        await using GalateaTestHost reopened = fixture.CreateRestarted(new NoteExtractorProvider(),
            DisabledGalateaUserMessageNormalizer.Instance, new NoDispatchTransport());
        CharacterSessionHost cold = await reopened.Factory.Services.GetRequiredService<GalateaHostService>().GetSessionAsync("alice", CancellationToken.None);
        Assert.Null(cold.DelegationHandle!.Store.ReadPendingReceiptDelivery());
        Assert.Null(cold.CharacterMemoryReconciler!.ReadPendingReceiptDelivery());
        Assert.Null(cold.DelegationHandle.Store.ReadBoundReceiptDelivery());
        Assert.Null(cold.CharacterMemoryReconciler.ReadBoundReceiptDelivery());
    }

    [Fact]
    public async Task FormalUndo_RemainingOwnerCannotProveExactInput_LeavesHeadAndBindingUntouched() {
        var provider = new NoteExtractorProvider();
        await using GalateaTestHost fixture = CreateHost(provider);
        GalateaHostService service = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try {
            var seed = await SeedDualReceiptsAsync(session);
            EventAddress observation = BindBothAndAppend(session, seed);
            EventAddress terminal = session.Engine.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text("done")]), ActionReceiptDeliveryFixture.Invocation);
            ActionReceiptDelivery.Reconcile(session.DelegationHandle!.Store, session.Engine);
            // Fault only this synthetic owner's stored evidence. The SQLite
            // schema remains valid; Journal exact proof must reject the base.
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(session.Character.CharacterMemoryStateDir, CharacterMemorySqliteStore.DatabaseFileName),
                Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString())) {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE note_receipt_delivery SET expected_session_head = $head WHERE source_action_address = $source;";
                command.Parameters.AddWithValue("$head", EventAddressTextCodec.Format(observation));
                command.Parameters.AddWithValue("$source", seed.Note.SourceActionAddress);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            var bound = session.CharacterMemoryReconciler!.ReadBoundReceiptDelivery();
            EventJournalPhysicalAppendFrontier frontier = session.Engine.ReadView.ReadPhysicalAppendFrontier();
            Assert.Throws<GalateaTurnException>(() => service.PrepareAndCommitPopLatestTurn(session, terminal));
            Assert.Equal(terminal, session.Engine.ReadCurrentHead());
            Assert.Equal(frontier, session.Engine.ReadView.ReadPhysicalAppendFrontier());
            Assert.Equal(bound, session.CharacterMemoryReconciler.ReadBoundReceiptDelivery());
            Assert.Null(session.CharacterMemoryReconciler.ReadPendingReceiptDelivery());
        }
        finally { session.TurnLock.Release(); }
    }

    [Fact]
    public async Task SharedFreshAdmissionBoundary_SettlesRemainingOwnerBeforeRelayCanPlanNewInput() {
        var provider = new NoteExtractorProvider();
        await using GalateaTestHost fixture = CreateHost(provider);
        GalateaHostService service = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try {
            var seed = await SeedDualReceiptsAsync(session);
            EventAddress observation = BindBothAndAppend(session, seed);
            EventAddress terminal = session.Engine.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text("done")]), ActionReceiptDeliveryFixture.Invocation);
            ActionReceiptDelivery.Reconcile(session.DelegationHandle!.Store, session.Engine);
            Assert.NotNull(session.CharacterMemoryReconciler!.ReadBoundReceiptDelivery());
            // This is the common production boundary used by relay as well as
            // HTTP fresh admission; deliberately bypass durable admission.
            await service.PrepareFreshTurnAdmissionAsync(session,
                session.Engine.InspectRuntimeRecoveryRequirements(), CancellationToken.None);
            Assert.Equal(terminal, session.Engine.ReadCurrentHead());
            Assert.Null(session.CharacterMemoryReconciler.ReadBoundReceiptDelivery());
            Assert.Null(session.CharacterMemoryReconciler.ReadPendingReceiptDelivery());
            Assert.Equal(EventAddressTextCodec.Format(observation), session.CharacterMemoryReconciler.ReadReceiptDeliveryExact(seed.Note.SourceActionAddress)!.ObservationAddress);
        }
        finally { session.TurnLock.Release(); }
    }

    [Fact]
    public async Task UndoGate_SettlesAppendedStructuredReceiptBeforeRefusingAnIncompleteTurn() {
        var provider = new NoteExtractorProvider();
        await using GalateaTestHost fixture = GalateaTestHost.Create(provider,
            DisabledGalateaUserMessageNormalizer.Instance,
            connections: [Connection("test"), Connection("note-helper")],
            connectionOptionIds: ["test"], characterNoteExtractorConnectionId: "note-helper");
        GalateaHostService service = fixture.Factory.Services.GetRequiredService<GalateaHostService>();
        CharacterSessionHost session = await service.GetSessionAsync("alice", CancellationToken.None);
        await session.TurnLock.WaitAsync();
        try {
            var timestamp = new DateTimeOffset(2026, 9, 16, 3, 0, 0, TimeSpan.Zero);
            const string noteAction = "[Galatea] I submitted a Note save request:\nkeep the blue key";
            session.Engine.AppendObservation(GalateaObservationContent.CreatePlayerAction(
                GalateaDelegateTestConfiguration.PlayerSender, "保存蓝钥匙的记录", timestamp));
            EventAddress sourceAction = session.Engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text(noteAction)]),
                new CompletionDescriptor("fixture", "fixture-v1", "model-a"));
            CharacterNoteDefaultPodReconciler memory = session.CharacterMemoryReconciler!;
            Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(await memory.ReconcileTargetAsync(
                session.Engine, new GalateaTerminalActionExtractionTarget(sourceAction, noteAction)));
            ActionReceiptDeliverySnapshot pending = Assert.IsType<ActionReceiptDeliverySnapshot>(
                memory.ReadPendingReceiptDelivery());
            Assert.IsType<NoteReceiptBatch>(pending.FrozenBatch);
            SessionInputContent content = GalateaObservationContent.Create(
                new GalateaFreshInput.PlayerAction("我继续前进。\r\n```\n原文\n```",
                    GalateaDelegateTestConfiguration.PlayerSender), timestamp,
                new GalateaSenderSnapshot("character", session.Character.CharacterId, session.Character.CharacterName.Value),
                [new PlayerTurnNotice.ActionReceipt(pending.FrozenBatch!)]);
            ActionReceiptDelivery.Bind(memory.ReceiptDeliveryStore, session.Engine, pending, sourceAction, content);
            EventAddress appended = session.Engine.AppendObservation(content);

            // A reachable crash window: the input has committed, but neither
            // Completion nor the host's receipt acknowledgement has happened.
            // Do not manufacture a terminal Action to make this turn undoable.
            ActionReceiptDeliverySnapshot bound = Assert.IsType<ActionReceiptDeliverySnapshot>(
                memory.ReadBoundReceiptDelivery());
            Assert.Equal(ActionReceiptDeliveryState.ObservationBound, bound.State);
            Assert.Equal(content, bound.BoundInput);
            Assert.Null(bound.ObservationAddress);
            EventJournalPhysicalAppendFrontier frontier = session.Engine.ReadView.ReadPhysicalAppendFrontier();

            // This is the actual service entry used by the HTTP undo handler.
            // The browser's previously eligible source-Action token became stale
            // when the receipt-bearing input appended. No explicit reconcile.
            Assert.Null(service.PrepareAndCommitPopLatestTurn(session, sourceAction));

            ActionReceiptDeliverySnapshot delivered = Assert.IsType<ActionReceiptDeliverySnapshot>(
                memory.ReadReceiptDeliveryExact(pending.SourceActionAddress));
            Assert.Equal(ActionReceiptDeliveryState.Delivered, delivered.State);
            Assert.Equal(EventAddressTextCodec.Format(appended), delivered.ObservationAddress);
            Assert.Equal(EventAddressTextCodec.Format(sourceAction), delivered.ExpectedSessionHead);
            Assert.Null(memory.ReadPendingReceiptDelivery());
            Assert.Null(memory.ReadBoundReceiptDelivery());
            Assert.Equal(appended, session.Engine.ReadCurrentHead());
            Assert.Equal(SessionExecutionPhase.AwaitingAgentAction, session.Engine.InspectExecutionBoundary().Phase);
            Assert.Equal(frontier, session.Engine.ReadView.ReadPhysicalAppendFrontier());
            Assert.IsType<SessionExpectedObservationTurnReadResult.InProgress>(session.Engine.ReadView
                .ProveExpectedObservationTurnAtSelectedHead(new SessionExpectedObservationTurnRequest(
                    appended, sourceAction, content, appended)));

            // Even an explicit retry using the current head cannot undo this
            // incomplete turn, and cannot cause the delivered receipt to return.
            Assert.Null(service.PrepareAndCommitPopLatestTurn(session, appended));
            Assert.Equal(delivered, memory.ReadReceiptDeliveryExact(pending.SourceActionAddress));
            Assert.Equal(appended, session.Engine.ReadCurrentHead());
            Assert.Equal(frontier, session.Engine.ReadView.ReadPhysicalAppendFrontier());
            Assert.Equal(2, provider.Calls);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    private static GalateaTestHost CreateHost(NoteExtractorProvider provider, bool deleteFilesOnDispose = true) =>
        GalateaTestHost.Create(provider, DisabledGalateaUserMessageNormalizer.Instance,
            deleteFilesOnDispose: deleteFilesOnDispose,
            connections: [Connection("test"), Connection("note-helper")],
            connectionOptionIds: ["test"], characterNoteExtractorConnectionId: "note-helper",
            delegateTransport: new NoDispatchTransport());

    private static async Task<(EventAddress Source, ActionReceiptDeliverySnapshot Mail, ActionReceiptDeliverySnapshot Note)> SeedDualReceiptsAsync(
        CharacterSessionHost session
    ) {
        const string visible = "[Galatea] I submitted a Note save request:\n"
            + ActionReceiptDeliveryFixture.NoteText + "\nI submitted mail to Nobody:\n" + ActionReceiptDeliveryFixture.MailText;
        session.Engine.AppendObservation(GalateaObservationContent.CreatePlayerAction(
            GalateaDelegateTestConfiguration.PlayerSender, "Submit the long Note and Mail.", ActionReceiptDeliveryFixture.Timestamp));
        EventAddress source = session.Engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text(visible)]), ActionReceiptDeliveryFixture.Invocation);
        CharacterNoteDefaultPodReconciler memory = session.CharacterMemoryReconciler!;
        Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(await memory.ReconcileTargetAsync(session.Engine, new(source, visible)));
        GalateaDelegationSqliteStore store = session.DelegationHandle!.Store;
        store.CaptureActionBatch(new(EventAddressTextCodec.Format(source),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(visible))).ToLowerInvariant(),
            Encoding.UTF8.GetByteCount(visible), DisabledOutboundMailExtractor.Instance.ContractId,
            [new SendMailIntent("Nobody", null, ActionReceiptDeliveryFixture.MailText, null, "evidence")],
            new GalateaSenderSnapshot("character", session.Character.CharacterId, session.Character.CharacterName.Value)));
        return (source, Assert.IsType<ActionReceiptDeliverySnapshot>(store.ReadPendingReceiptDelivery()),
            Assert.IsType<ActionReceiptDeliverySnapshot>(memory.ReadPendingReceiptDelivery()));
    }

    private static EventAddress BindBothAndAppend(CharacterSessionHost session,
        (EventAddress Source, ActionReceiptDeliverySnapshot Mail, ActionReceiptDeliverySnapshot Note) seed) {
        SessionInputContent content = GalateaObservationContent.Create(
            new GalateaFreshInput.PlayerAction("continue after both receipts", GalateaDelegateTestConfiguration.PlayerSender),
            ActionReceiptDeliveryFixture.Timestamp,
            new GalateaSenderSnapshot("character", session.Character.CharacterId, session.Character.CharacterName.Value),
            [new PlayerTurnNotice.ActionReceipt(seed.Mail.FrozenBatch!), new PlayerTurnNotice.ActionReceipt(seed.Note.FrozenBatch!)]);
        ActionReceiptDelivery.Bind(session.DelegationHandle!.Store, session.Engine, seed.Mail, seed.Source, content);
        ActionReceiptDelivery.Bind(session.CharacterMemoryReconciler!.ReceiptDeliveryStore, session.Engine, seed.Note, seed.Source, content);
        return session.Engine.AppendObservation(content);
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id, "openai-chat", "model-a", "openai-chat/strict", "http://localhost:8000/", ApiKey: "fixture-key");

    private sealed class NoteExtractorProvider : ICompletionClientFactory, ICompletionClient {
        internal int Calls { get; private set; }
        internal int MainCalls { get; private set; }
        internal string? CurrentObservation { get; private set; }
        public string Name => "rewind-receipt-gate";
        public string ApiSpecId => "fixture-v1";
        public ICompletionClient Create(CompletionConnectionConfig connection) => this;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.PromptPrefix.OutputContract.Tools.Any(tool => tool.Name == CharacterNoteExtractor.ToolName)) {
                if (request.PromptPrefix.OutputContract.Tools.Any(tool => tool.Name == CharacterNoteDerivedInfoEnricher.ToolName)) {
                    var derived = new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                        CharacterNoteDerivedInfoEnricher.ToolName, "derive-note", JsonSerializer.Serialize(new {
                            items = new[] { new { artifactOrdinal = 0, title = "Saved note", gist = "Saved long note", summary = "Saved exact note." } }
                        })))]);
                    return Task.FromResult(new CompletionResult(derived, CompletionDescriptor.From(this, request)));
                }
                MainCalls++;
                CurrentObservation = request.PromptPrefix.SharedContextMessages.OfType<ObservationMessage>().Last().Content;
                observer?.OnTextDelta("Receipt turn completed.");
                return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.Text("Receipt turn completed.")]),
                    CompletionDescriptor.From(this, request)));
            }
            Calls++;
            string target = Assert.IsType<string>(request.TailMessages.OfType<ObservationMessage>().First().Content);
            ActionMessage message = request.TailMessages.OfType<ActionMessage>().Any()
                || (!target.Contains(ActionReceiptDeliveryFixture.NoteText, StringComparison.Ordinal)
                    && !target.Contains("keep the blue key", StringComparison.Ordinal))
                ? new ActionMessage([])
                : new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                    CharacterNoteExtractor.ToolName, "save-note", JsonSerializer.Serialize(new { textStartLine = 2, textEndLine = 2 })))]);
            return Task.FromResult(new CompletionResult(message,
                CompletionDescriptor.From(this, request)));
        }
    }

    private sealed class NoDispatchTransport : IGalateaDurableDelegateTransport {
        public Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(GalateaEnsureDelegateBindingRequest request, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("This receipt fixture must not open an external binding.");
        public Task<GalateaDelegateTurnAccepted> StartTurnAsync(GalateaStartDelegateTurnRequest request, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("This receipt fixture must not dispatch external work.");
        public Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(GalateaInspectDelegateDispatchRequest request, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("This receipt fixture must not inspect external work.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
