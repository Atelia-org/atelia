using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaStructuredDeliveryRewindGateTests {
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
            const string noteAction = "[Galatea] I submitted a Note save request: keep the blue key.";
            session.Engine.AppendObservation(GalateaObservationContent.CreatePlayerAction(
                GalateaDelegateTestConfiguration.PlayerSender, "保存蓝钥匙的记录", timestamp));
            EventAddress sourceAction = session.Engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text(noteAction)]),
                new CompletionDescriptor("fixture", "fixture-v1", "model-a"));
            CharacterNoteDefaultPodReconciler memory = session.CharacterMemoryReconciler!;
            Assert.IsType<CharacterNoteDefaultPodReconcileResult.AppliedNow>(await memory.ReconcileTargetAsync(
                session.Engine, new GalateaTerminalActionExtractionTarget(sourceAction, noteAction)));
            CharacterNoteReceiptDeliverySnapshot pending = Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(
                memory.ReadPendingReceiptDelivery());
            Assert.NotNull(pending.Facts);
            SessionInputContent content = GalateaObservationContent.Create(
                new GalateaFreshInput.PlayerAction("我继续前进。\r\n```\n原文\n```",
                    GalateaDelegateTestConfiguration.PlayerSender), timestamp,
                new GalateaSenderSnapshot("character", session.Character.CharacterId, session.Character.CharacterName.Value),
                [CharacterNoteSaveReceipt.SelectForObservation(pending)]);
            GalateaNoteReceiptDelivery.Bind(memory, session.Engine, pending, sourceAction, content);
            EventAddress appended = session.Engine.AppendObservation(content);

            // A reachable crash window: the input has committed, but neither
            // Completion nor the host's receipt acknowledgement has happened.
            // Do not manufacture a terminal Action to make this turn undoable.
            CharacterNoteReceiptDeliverySnapshot bound = Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(
                memory.ReadBoundReceiptDelivery());
            Assert.Equal(CharacterNoteReceiptDeliveryState.ObservationBound, bound.State);
            Assert.Equal(content, bound.BoundInput);
            Assert.Null(bound.ObservationAddress);
            EventJournalPhysicalAppendFrontier frontier = session.Engine.ReadView.ReadPhysicalAppendFrontier();

            // This is the actual service entry used by the HTTP undo handler.
            // The browser's previously eligible source-Action token became stale
            // when the receipt-bearing input appended. No explicit reconcile.
            Assert.Null(service.PrepareAndCommitPopLatestTurn(session, sourceAction));

            CharacterNoteReceiptDeliverySnapshot delivered = Assert.IsType<CharacterNoteReceiptDeliverySnapshot>(
                memory.ReadReceiptDeliveryExact(pending.SourceActionAddress));
            Assert.Equal(CharacterNoteReceiptDeliveryState.Delivered, delivered.State);
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
            Assert.Equal(1, provider.Calls);
        }
        finally {
            session.TurnLock.Release();
        }
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id, "openai-chat", "model-a", "openai-chat/strict", "http://localhost:8000/", ApiKey: "fixture-key");

    private sealed class NoteExtractorProvider : ICompletionClientFactory, ICompletionClient {
        internal int Calls { get; private set; }
        public string Name => "rewind-receipt-gate";
        public string ApiSpecId => "fixture-v1";
        public ICompletionClient Create(CompletionConnectionConfig connection) => this;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains(request.PromptPrefix.OutputContract.Tools, tool => tool.Name == CharacterNoteExtractor.ToolName);
            Calls++;
            return Task.FromResult(new CompletionResult(new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(
                CharacterNoteExtractor.ToolName, "save-note", JsonSerializer.Serialize(new { text = "keep the blue key" })))]),
                CompletionDescriptor.From(this, request)));
        }
    }
}
